using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Realtime;

/// <summary>
/// Produces the realtime hints of every unit of work, for the API and the Workers alike (registered in
/// <c>AddInfrastructure</c>), so no service can forget one:
/// <list type="number">
/// <item><b>capture</b> before the save, from the change tracker: added <see cref="Order"/> → <c>order.created</c>; a
/// modified <see cref="Order"/> → <c>order.changed</c> (one per saved order, whatever changed); added
/// <see cref="InventoryMovement"/> → <c>inventory.changed</c> (every stock change writes a movement, also the raw-SQL
/// updates); added <see cref="ProcessingTask"/> or a changed task status → <c>processing.task.changed</c>;</item>
/// <item><b>send</b> after the save succeeded, as one <c>pg_notify</c> on the context's connection. Inside the caller's
/// transaction PostgreSQL delivers it at the commit and discards it on a rollback; without one, EF Core has already
/// committed when this runs. A hint therefore never precedes its data and never outlives a rollback;</item>
/// <item><b>forget</b> the captured hints when the save fails.</item>
/// </list>
/// One instance per DbContext (scoped): the captured hints belong to that context's save.
/// </summary>
public sealed class RealtimeHintInterceptor(ILogger<RealtimeHintInterceptor> logger) : SaveChangesInterceptor
{
    private List<RealtimeHint>? _pending;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        _pending = eventData.Context is { } context ? Capture(context) : null;
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _pending = eventData.Context is { } context ? Capture(context) : null;
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Take() is { } payload && eventData.Context is { } context)
        {
            var committed = context.Database.CurrentTransaction is null;
            try
            {
                context.Database.ExecuteSql($"SELECT pg_notify({RealtimeChannel.Name}, {payload})");
            }
            catch (Exception exception) when (committed)
            {
                LogNotSent(exception);
            }
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (Take() is { } payload && eventData.Context is { } context)
        {
            // No transaction of the caller: the data is committed already. A notification that cannot be sent must not
            // turn that into an error (clients miss a hint and catch up on their next refetch). Inside a transaction a
            // failed statement aborts the transaction anyway, so the error is passed on.
            var committed = context.Database.CurrentTransaction is null;
            try
            {
                // The caller's token is not used: the save has happened and its hint belongs to it.
                await context.Database.ExecuteSqlAsync($"SELECT pg_notify({RealtimeChannel.Name}, {payload})", CancellationToken.None);
            }
            catch (Exception exception) when (committed)
            {
                LogNotSent(exception);
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _pending = null;
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending = null;
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        _pending = null;
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending = null;
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    /// <summary>The hints of the changes that are about to be saved (empty when nothing of interest changes).</summary>
    public static List<RealtimeHint> Capture(DbContext context)
    {
        var orders = new List<RealtimeHint>();
        var tasks = new List<RealtimeHint>();
        var productIds = new HashSet<Guid>();

        // One pass (Entries() detects changes once).
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case Order order when entry.State == EntityState.Added:
                    orders.Add(RealtimeHint.OrderCreated(order.OrderId, order.ChannelId, order.Status));
                    break;

                case Order order when entry.State == EntityState.Modified:
                    orders.Add(RealtimeHint.OrderChanged(order.OrderId));
                    break;

                case InventoryMovement movement when entry.State == EntityState.Added:
                    productIds.Add(movement.ProductId);
                    break;

                case ProcessingTask task when entry.State == EntityState.Added || StatusChanged(entry):
                    tasks.Add(RealtimeHint.TaskChanged(task.TaskId, task.TaskType, task.Status, task.OrderId, task.ChannelId, task.ProductId));
                    break;
            }
        }

        // Fixed order inside one unit of work: orders, inventory, tasks.
        var hints = orders;
        if (productIds.Count > 0)
        {
            hints.Add(RealtimeHint.InventoryChanged(productIds));
        }

        hints.AddRange(tasks);
        return hints;

        static bool StatusChanged(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var status = entry.Property(nameof(ProcessingTask.Status));
            return entry.State == EntityState.Modified && !Equals(status.OriginalValue, status.CurrentValue);
        }
    }

    private string? Take()
    {
        var hints = _pending;
        _pending = null;
        return hints is { Count: > 0 } ? new RealtimeHintBatch(hints).ToPayload() : null;
    }

    private void LogNotSent(Exception exception) =>
        logger.LogWarning(exception, "Realtime hint could not be sent after the commit; clients catch up on their next refetch");
}

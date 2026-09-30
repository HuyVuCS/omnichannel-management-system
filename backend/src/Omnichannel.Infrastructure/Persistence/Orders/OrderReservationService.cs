using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Configurations;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// Stock reservation for the Order Worker (doc §8.2).
/// <list type="bullet">
/// <item>The conditional <c>UPDATE ... WHERE "AvailableStock" &gt;= @stock</c> <b>is</b> the availability check: there is no
/// separate read, so two concurrent orders can never both take the last unit (and the CHECK constraints are the last line).</item>
/// <item>Items are updated in <c>ProductId</c> order, the same global lock order as the stock import, so concurrent multi-item
/// orders and imports cannot deadlock.</item>
/// <item>All or nothing: if any item lacks stock the transaction is rolled back and an <c>InsufficientStock</c> order is stored
/// in a separate transaction without touching stock.</item>
/// <item>The task row is locked (<c>FOR UPDATE</c>) and re-checked inside the reservation transaction, so two concurrent
/// deliveries of the same message cannot both reserve; <c>UX_Orders_ChannelId_ExternalOrderId</c> is the final guard.</item>
/// </list>
/// </summary>
internal sealed class OrderReservationService(
    OmnichannelDbContext dbContext,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<OrderReservationService> logger) : IOrderReservationService
{
    private const string SystemActor = "system";

    public async Task<ProcessingTaskStatus?> GetTaskStatusAsync(Guid taskId, CancellationToken cancellationToken) =>
        await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => (ProcessingTaskStatus?)t.Status)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var task = await LockTaskAsync(taskId, cancellationToken);
        if (task.IsTerminal)
        {
            return null;
        }

        var attempt = task.StartAttempt(workerName, workerInstanceId, timeProvider.GetUtcNow());
        dbContext.ProcessingAttempts.Add(attempt);

        // The unique (TaskId, AttemptNo) index is the last guard against two workers numbering the same attempt.
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AttemptStart(attempt.AttemptId, attempt.AttemptNo);
    }

    public async Task<ReservationResult> ReserveAsync(
        OrderCreatedPayload order,
        Guid attemptId,
        string workerName,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var actor = currentUser.UserId ?? SystemActor;
        bool orderAlreadyExists;

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var task = await LockTaskAsync(order.TaskId, cancellationToken);
            if (task.IsTerminal)
            {
                // Another delivery of the same message finished first (we waited for its lock).
                task.CloseAttemptOfCompletedTask(attemptId, now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ReservationResult(ReservationOutcome.TaskAlreadyCompleted, task.OrderId);
            }

            var reserved = await TryReserveAllAsync(order, now, actor, cancellationToken);
            if (reserved is not null)
            {
                var stored = new Order(order.ChannelId, order.CustomerId, order.ExternalOrderId, OrderStatus.Reserved);
                AddItems(stored, order);
                dbContext.Orders.Add(stored);

                foreach (var (item, stock) in reserved)
                {
                    var movement = new InventoryMovement(
                        item.ProductId,
                        InventoryMovementType.Reserve,
                        item.Stock,
                        InventoryReferenceType.Order,
                        stored.OrderId.ToString(),
                        documentNumber: null,
                        documentSeries: null);
                    dbContext.InventoryMovements.Add(movement);
                    dbContext.OutboxEvents.Add(new OutboxEvent(
                        EventTypes.InventoryChanged,
                        item.ProductId.ToString(),
                        MessageJson.Serialize(new InventoryChangedPayload(
                            item.ProductId,
                            movement.InventoryMovementId,
                            movement.Type,
                            stock.TotalStock,
                            stock.ReservedStock,
                            stock.AvailableStock,
                            now)),
                        correlationContext.CorrelationId,
                        movement.InventoryMovementId));
                }

                task.Succeed(stored.OrderId, attemptId, now);

                if (await TrySaveAsync(cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new ReservationResult(ReservationOutcome.Reserved, stored.OrderId);
                }

                // The order already exists: rolling back also undoes the stock updates.
                orderAlreadyExists = true;
            }
            else
            {
                // Nothing may stay reserved when one item is short.
                orderAlreadyExists = false;
            }

            await transaction.RollbackAsync(cancellationToken);
        }

        // Follow-up work runs in a fresh transaction, after the rolled-back one is disposed.
        dbContext.ChangeTracker.Clear();
        return orderAlreadyExists
            ? await CompleteWithExistingOrderAsync(order, attemptId, workerName, cancellationToken)
            : await StoreInsufficientStockOrderAsync(order, attemptId, workerName, cancellationToken);
    }

    public async Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // The failed unit of work may have left tracked entities behind in this scope.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var task = await LockTaskAsync(taskId, cancellationToken);
        task.ScheduleRetry(attemptId, errorCode, errorMessage, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        string workerName,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Error,
            OrderProcessingEvents.MessageRejected,
            correlationId,
            taskId,
            orderId: null,
            workerName,
            MessageJson.Serialize(new { messageId, reason }),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <returns>Stock after the update per item, in ProductId order; null when any item lacked stock.</returns>
    private async Task<List<(OrderCreatedItem Item, StockRow Stock)>?> TryReserveAllAsync(
        OrderCreatedPayload order,
        DateTimeOffset now,
        string actor,
        CancellationToken cancellationToken)
    {
        var reserved = new List<(OrderCreatedItem, StockRow)>(order.Items.Count);

        // Same global lock order as InventoryImportService: ProductId.
        foreach (var item in order.Items.OrderBy(i => i.ProductId))
        {
            // Check and reserve in one statement; raw SQL bypasses the auditing interceptor, hence UpdatedAt/UpdatedBy.
            var rows = await dbContext.Database
                .SqlQuery<StockRow>($"""
                    UPDATE "Inventories"
                    SET "ReservedStock" = "ReservedStock" + {item.Stock}, "UpdatedAt" = {now}, "UpdatedBy" = {actor}
                    WHERE "ProductId" = {item.ProductId} AND "AvailableStock" >= {item.Stock}
                    RETURNING "TotalStock", "ReservedStock", "AvailableStock"
                    """)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                return null;
            }

            reserved.Add((item, rows[0]));
        }

        return reserved;
    }

    private async Task<ReservationResult> StoreInsufficientStockOrderAsync(
        OrderCreatedPayload order,
        Guid attemptId,
        string workerName,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await LockTaskAsync(order.TaskId, cancellationToken);
        if (task.IsTerminal)
        {
            task.CloseAttemptOfCompletedTask(attemptId, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReservationResult(ReservationOutcome.TaskAlreadyCompleted, task.OrderId);
        }

        var productIds = order.Items.Select(i => i.ProductId).ToList();
        var available = await dbContext.Inventories
            .AsNoTracking()
            .Where(i => productIds.Contains(i.ProductId))
            .ToDictionaryAsync(i => i.ProductId, i => i.AvailableStock, cancellationToken);

        var stored = new Order(order.ChannelId, order.CustomerId, order.ExternalOrderId, OrderStatus.InsufficientStock);
        AddItems(stored, order);
        dbContext.Orders.Add(stored);
        task.Succeed(stored.OrderId, attemptId, now);

        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Warning,
            OrderProcessingEvents.InsufficientStock,
            correlationContext.CorrelationId,
            order.TaskId,
            stored.OrderId,
            workerName,
            MessageJson.Serialize(new
            {
                orderId = stored.OrderId,
                taskId = order.TaskId,
                items = order.Items.Select(i => new
                {
                    productId = i.ProductId,
                    requested = i.Stock,
                    available = available.GetValueOrDefault(i.ProductId)
                })
            }),
            now));

        if (!await TrySaveAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            dbContext.ChangeTracker.Clear();
            return await CompleteWithExistingOrderAsync(order, attemptId, workerName, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderId} ({ExternalOrderId}) stored as InsufficientStock; nothing reserved",
            stored.OrderId,
            order.ExternalOrderId);
        return new ReservationResult(ReservationOutcome.InsufficientStock, stored.OrderId);
    }

    /// <summary>
    /// Final idempotency guard hit: an Order with this channel + external id exists already (should not happen after intake
    /// idempotency). The task is completed pointing to that order; nothing else changes.
    /// </summary>
    private async Task<ReservationResult> CompleteWithExistingOrderAsync(
        OrderCreatedPayload order,
        Guid attemptId,
        string workerName,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await LockTaskAsync(order.TaskId, cancellationToken);

        var existingOrderId = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.ChannelId == order.ChannelId && o.ExternalOrderId == order.ExternalOrderId)
            .Select(o => o.OrderId)
            .SingleAsync(cancellationToken);

        if (task.IsTerminal)
        {
            task.CloseAttemptOfCompletedTask(attemptId, now);
        }
        else
        {
            task.Succeed(existingOrderId, attemptId, now);
        }

        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Warning,
            OrderProcessingEvents.OrderAlreadyExists,
            correlationContext.CorrelationId,
            order.TaskId,
            existingOrderId,
            workerName,
            MessageJson.Serialize(new { taskId = order.TaskId, orderId = existingOrderId, externalOrderId = order.ExternalOrderId }),
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Order {ExternalOrderId} already exists as {OrderId}; task {TaskId} completed without a new reservation",
            order.ExternalOrderId,
            existingOrderId,
            order.TaskId);
        return new ReservationResult(ReservationOutcome.OrderAlreadyExists, existingOrderId);
    }

    /// <returns>False when the Order violates <c>UX_Orders_ChannelId_ExternalOrderId</c>.</returns>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(OrderConfiguration.IdempotencyIndexName))
        {
            return false;
        }
    }

    /// <summary>Locks the task row for the rest of the transaction and loads it with all attempts.</summary>
    private async Task<ProcessingTask> LockTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "ProcessingTasks" WHERE "TaskId" = {taskId} FOR UPDATE""",
            cancellationToken);

        return await dbContext.ProcessingTasks
            .Include(t => t.Attempts)
            .SingleAsync(t => t.TaskId == taskId, cancellationToken);
    }

    /// <summary>Order lines keep the request order; unit price is the intake snapshot.</summary>
    private static void AddItems(Order stored, OrderCreatedPayload order)
    {
        foreach (var item in order.Items)
        {
            stored.AddItem(item.ProductId, item.Stock, item.UnitPrice);
        }
    }

    private sealed class StockRow
    {
        public int TotalStock { get; init; }
        public int ReservedStock { get; init; }
        public int AvailableStock { get; init; }
    }
}

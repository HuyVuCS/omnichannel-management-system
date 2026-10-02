using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Dashboard;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Infrastructure.Persistence.Dashboard;

/// <summary>
/// The numbers of the overview page. Every number is an SQL aggregate (nine small statements, none depends on the number
/// of rows returned by another, no entity is loaded); the lists are completed with zeros in memory so the client never has
/// to know which statuses, task types or channels exist.
/// </summary>
internal sealed class DashboardService(OmnichannelDbContext dbContext, TimeProvider timeProvider) : IDashboardService
{
    public async Task<DashboardSummaryResponse> GetSummaryAsync(DashboardSummaryQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (from, to) = DashboardRange.Resolve(query, now);

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        var (fromUtc, toUtc) = (from.ToUniversalTime(), to.ToUniversalTime());

        var channels = await dbContext.Channels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.ChannelId)
            .Select(c => new { c.ChannelId, c.Slug, c.Name })
            .ToListAsync(cancellationToken);

        var orderGroups = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.CreatedAt >= fromUtc && o.CreatedAt <= toUtc)
            .GroupBy(o => new { o.ChannelId, o.Status })
            .Select(g => new { g.Key.ChannelId, g.Key.Status, Count = g.Count(), Amount = g.Sum(o => o.TotalAmount) })
            .ToListAsync(cancellationToken);

        var products = await dbContext.Products.CountAsync(cancellationToken);
        // The shared rule: the same rows as GET /api/inventory?lowStockOnly=true.
        var lowStock = await dbContext.Inventories.CountAsync(InventoryPredicates.IsLowStock, cancellationToken);
        var outOfStock = await dbContext.Inventories.CountAsync(i => i.AvailableStock == 0, cancellationToken);

        // Waiting or running now, or finished within the range (a task's UpdatedAt is the moment it reached its status).
        var taskGroups = await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.Status == ProcessingTaskStatus.Pending
                || t.Status == ProcessingTaskStatus.InProgress
                || t.Status == ProcessingTaskStatus.RetryScheduled
                || ((t.Status == ProcessingTaskStatus.Failed || t.Status == ProcessingTaskStatus.Succeeded)
                    && t.UpdatedAt >= fromUtc && t.UpdatedAt <= toUtc))
            .GroupBy(t => new { t.TaskType, t.Status })
            .Select(g => new { g.Key.TaskType, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var syncGroups = await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskType == ProcessingTaskType.StockSync && t.ChannelId != null)
            .GroupBy(t => t.ChannelId)
            .Select(g => new
            {
                ChannelId = g.Key,
                RetryScheduled = g.Count(t => t.Status == ProcessingTaskStatus.RetryScheduled),
                Failed = g.Count(t => t.Status == ProcessingTaskStatus.Failed && t.UpdatedAt >= fromUtc && t.UpdatedAt <= toUtc),
                LastSucceededAt = g.Where(t => t.Status == ProcessingTaskStatus.Succeeded).Max(t => t.UpdatedAt)
            })
            .ToListAsync(cancellationToken);

        var outboxPending = await dbContext.OutboxEvents
            .AsNoTracking()
            .Where(e => e.Status == OutboxEventStatus.Pending)
            .GroupBy(e => e.Status)
            .Select(g => new { Count = g.Count(), OldestAt = (DateTimeOffset?)g.Min(e => e.CreatedAt) })
            .SingleOrDefaultAsync(cancellationToken);
        var outboxFailed = await dbContext.OutboxEvents.CountAsync(e => e.Status == OutboxEventStatus.Failed, cancellationToken);

        int Orders(Guid? channelId = null, OrderStatus? status = null) => orderGroups
            .Where(g => (channelId is null || g.ChannelId == channelId) && (status is null || g.Status == status))
            .Sum(g => g.Count);

        int Tasks(ProcessingTaskType type, ProcessingTaskStatus status) =>
            taskGroups.Where(g => g.TaskType == type && g.Status == status).Sum(g => g.Count);

        return new DashboardSummaryResponse(
            now,
            from,
            to,
            new DashboardOrders(
                Orders(),
                Enum.GetValues<OrderStatus>().Select(status => new DashboardOrderStatusCount(status, Orders(status: status))).ToList(),
                channels
                    .Select(c => new DashboardChannelOrders(
                        c.ChannelId,
                        c.Slug,
                        c.Name,
                        Orders(c.ChannelId),
                        Orders(c.ChannelId, OrderStatus.Reserved),
                        Orders(c.ChannelId, OrderStatus.InsufficientStock)))
                    .ToList(),
                orderGroups.Where(g => g.Status is OrderStatus.Reserved or OrderStatus.Completed).Sum(g => g.Amount)),
            new DashboardInventory(products, lowStock, outOfStock),
            Enum.GetValues<ProcessingTaskType>()
                .Select(type => new DashboardProcessing(
                    type,
                    Tasks(type, ProcessingTaskStatus.Pending),
                    Tasks(type, ProcessingTaskStatus.InProgress),
                    Tasks(type, ProcessingTaskStatus.RetryScheduled),
                    Tasks(type, ProcessingTaskStatus.Failed),
                    Tasks(type, ProcessingTaskStatus.Succeeded)))
                .ToList(),
            channels
                .Where(c => RabbitMqTopology.StockSyncQueueOf(c.Slug) is not null)
                .Select(c =>
                {
                    var sync = syncGroups.SingleOrDefault(g => g.ChannelId == c.ChannelId);
                    return new DashboardStockSync(
                        c.ChannelId, c.Slug, c.Name, sync?.RetryScheduled ?? 0, sync?.Failed ?? 0, sync?.LastSucceededAt);
                })
                .ToList(),
            new DashboardOutbox(outboxPending?.Count ?? 0, outboxFailed, outboxPending?.OldestAt));
    }
}

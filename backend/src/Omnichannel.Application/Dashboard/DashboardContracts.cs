using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Dashboard;

/// <summary>
/// <c>GET /api/dashboard/summary</c>. <see cref="From"/>/<see cref="To"/> (inclusive) apply to orders (<c>CreatedAt</c>) and to
/// finished tasks (<c>UpdatedAt</c>); numbers called "current" ignore the range. Defaults: <c>to</c> = now,
/// <c>from</c> = <c>to</c> − 24 h. The range is at most <see cref="MaxRangeDays"/> days.
/// </summary>
public sealed record DashboardSummaryQuery
{
    public const int MaxRangeDays = 92;
    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

/// <summary>Every number comes from the database at <see cref="GeneratedAt"/>; clients never compute one.</summary>
public sealed record DashboardSummaryResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset From,
    DateTimeOffset To,
    DashboardOrders Orders,
    DashboardInventory Inventory,
    IReadOnlyList<DashboardProcessing> Processing,
    IReadOnlyList<DashboardStockSync> StockSync,
    DashboardOutbox Outbox);

/// <param name="Total">Orders created in the range.</param>
/// <param name="ByStatus">One entry per <see cref="OrderStatus"/>, zeros included.</param>
/// <param name="ByChannel">One entry per channel, zeros included.</param>
/// <param name="SalesAmount">Sum of <c>TotalAmount</c> of the orders created in the range that are Reserved or Completed.</param>
public sealed record DashboardOrders(
    int Total,
    IReadOnlyList<DashboardOrderStatusCount> ByStatus,
    IReadOnlyList<DashboardChannelOrders> ByChannel,
    decimal SalesAmount);

public sealed record DashboardOrderStatusCount(OrderStatus Status, int Count);

public sealed record DashboardChannelOrders(Guid ChannelId, string Slug, string Name, int Total, int Reserved, int InsufficientStock);

/// <summary>Current stock picture.</summary>
/// <param name="LowStock">The rows the low-stock filter of <c>GET /api/inventory</c> returns (same rule).</param>
/// <param name="OutOfStock">Products with no available stock.</param>
public sealed record DashboardInventory(int Products, int LowStock, int OutOfStock);

/// <summary>One entry per <see cref="ProcessingTaskType"/>, zeros included.</summary>
/// <param name="Pending">Current.</param>
/// <param name="InProgress">Current.</param>
/// <param name="RetryScheduled">Current.</param>
/// <param name="Failed">Tasks that became Failed in the range.</param>
/// <param name="Succeeded">Tasks that became Succeeded in the range.</param>
public sealed record DashboardProcessing(
    ProcessingTaskType TaskType,
    int Pending,
    int InProgress,
    int RetryScheduled,
    int Failed,
    int Succeeded);

/// <summary>Stock sync per channel that has a stock-sync queue.</summary>
/// <param name="RetryScheduled">Current.</param>
/// <param name="Failed">Stock-sync tasks of the channel that failed in the range.</param>
/// <param name="LastSucceededAt">The channel's most recent successful sync (any time); null when there was none.</param>
public sealed record DashboardStockSync(
    Guid ChannelId,
    string Slug,
    string Name,
    int RetryScheduled,
    int Failed,
    DateTimeOffset? LastSucceededAt);

/// <summary>Current state of the transactional outbox.</summary>
/// <param name="Pending">Rows waiting to be published.</param>
/// <param name="Failed">Rows the publisher gave up on.</param>
/// <param name="OldestPendingAt">When the oldest waiting row was written; null when none waits.</param>
public sealed record DashboardOutbox(int Pending, int Failed, DateTimeOffset? OldestPendingAt);

public interface IDashboardService
{
    /// <exception cref="FluentValidation.ValidationException">
    /// The range resolved with the defaults is empty or longer than <see cref="DashboardSummaryQuery.MaxRangeDays"/> days.
    /// </exception>
    Task<DashboardSummaryResponse> GetSummaryAsync(DashboardSummaryQuery query, CancellationToken cancellationToken);
}

using Omnichannel.Application.Common.Paging;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Alerts;

/// <summary>
/// An alert as the list and the detail show it. Names come from the same query; no error message, payload or outbox data.
/// </summary>
/// <param name="ProductId">The product of a <c>LowStock</c>, <c>InsufficientStock</c> or <c>StockSyncFailed</c> alert.</param>
/// <param name="ChannelId">The channel of a <c>StockSyncFailed</c> alert.</param>
/// <param name="TaskId">The latest task of a failure alert.</param>
/// <param name="OrderId">The latest order (<c>InsufficientStock</c>; <c>ProcessingFailed</c> when the task has one).</param>
/// <param name="ErrorCode">Error code of the latest failure (failure alerts only).</param>
/// <param name="AcknowledgedByEmail">E-mail of the user who acknowledged it; null when nobody did or the id matches no user.</param>
/// <param name="AvailableStock"><c>LowStock</c> only: the product's available stock <b>now</b>, not when the alert opened.</param>
/// <param name="LowStockThreshold"><c>LowStock</c> only: the product's threshold <b>now</b>.</param>
public sealed record AlertResponse(
    Guid AlertId,
    AlertType Type,
    AlertStatus Status,
    Guid? ProductId,
    string? ProductSku,
    string? ProductName,
    Guid? ChannelId,
    string? ChannelName,
    Guid? TaskId,
    Guid? OrderId,
    string? ErrorCode,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    int OccurrenceCount,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedByEmail,
    DateTimeOffset? ResolvedAt,
    int? AvailableStock,
    int? LowStockThreshold);

/// <summary>
/// <see cref="Status"/> and <see cref="Type"/> are repeatable (<c>?status=Open&amp;status=Resolved</c>); without a status
/// the list shows what needs attention: <c>Open</c> and <c>Acknowledged</c>. <see cref="From"/>/<see cref="To"/> filter on
/// <c>LastOccurredAt</c> (inclusive, at most <see cref="MaxRangeDays"/> days when both are given). <c>search</c> is not
/// supported (ignored). Default order: most recent occurrence first; every sort field is descending unless <c>sortDir=asc</c>.
/// </summary>
public sealed record AlertListQuery : PageQuery
{
    public const int MaxRangeDays = 92;

    public const string SortByLastOccurredAt = "lastOccurredAt";
    public const string SortByFirstOccurredAt = "firstOccurredAt";
    public const string SortByOccurrenceCount = "occurrenceCount";

    public static readonly IReadOnlyList<string> SortFields = [SortByLastOccurredAt, SortByFirstOccurredAt, SortByOccurrenceCount];

    /// <summary>What the list shows when no status is asked for.</summary>
    public static readonly IReadOnlyList<AlertStatus> DefaultStatuses = [AlertStatus.Open, AlertStatus.Acknowledged];

    public IReadOnlyList<AlertStatus>? Status { get; init; }
    public IReadOnlyList<AlertType>? Type { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? ChannelId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>The statuses to show: the ones asked for, or <see cref="DefaultStatuses"/>.</summary>
    public IReadOnlyList<AlertStatus> Statuses => Status is { Count: > 0 } ? Status : DefaultStatuses;
}

public static class AlertErrorCodes
{
    public const string AlertNotFound = "alert_not_found";

    /// <summary>Acknowledge of an alert the system has already resolved.</summary>
    public const string AlertResolved = "alert_resolved";
}

/// <summary>SystemEventLog event types of alerts. Opening and resolving are not logged there: the table is the record.</summary>
public static class AlertEvents
{
    /// <summary>A manager acknowledged an alert (Information). Payload: <c>{alertId, type, acknowledgedBy}</c>.</summary>
    public const string Acknowledged = "alert.acknowledged";
}

/// <summary>
/// What an alert is about, as text: one unresolved alert exists per key. Ids are written without dashes (<c>:N</c>), like
/// the other business keys.
/// </summary>
public static class AlertDedupKey
{
    public static string LowStock(Guid productId) => $"low_stock:{productId:N}";

    public static string InsufficientStock(Guid productId) => $"insufficient_stock:{productId:N}";

    /// <summary>Per channel and product: only the newest stock-sync task of a pair matters (every push sends the current stock).</summary>
    public static string StockSyncFailed(Guid channelId, Guid productId) => $"stock_sync_failed:{channelId:N}:{productId:N}";

    public static string ProcessingFailed(Guid taskId) => $"processing_failed:{taskId:N}";
}

public interface IAlertService
{
    Task<PagedResult<AlertResponse>> ListAsync(AlertListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException"><c>alert_not_found</c>.</exception>
    Task<AlertResponse> GetAsync(Guid alertId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Open</c> → <c>Acknowledged</c>; an alert that is acknowledged already is returned unchanged. Never resolves: that is
    /// the system's decision.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException"><c>alert_not_found</c>.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><c>alert_resolved</c>.</exception>
    Task<AlertResponse> AcknowledgeAsync(Guid alertId, CancellationToken cancellationToken);
}

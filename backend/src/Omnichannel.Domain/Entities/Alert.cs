using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Something that needs attention now (low stock, an order rejected for lack of stock, a failed task), with a lifecycle
/// <c>Open</c> → <c>Acknowledged</c> → <c>Resolved</c>. At most one unresolved alert exists per <see cref="DedupKey"/>: a
/// condition that occurs again refreshes it, a condition that returns after resolution opens a new row.
/// <para>
/// Read-only for the application: rows are written by single SQL statements inside the transaction of the change that causes
/// them (the alert store), never through the change tracker. <c>SystemEventLog</c> stays the event history.
/// </para>
/// </summary>
public class Alert
{
    public const int MaxDedupKeyLength = 100;
    public const int MaxErrorCodeLength = 100;

    private Alert()
    {
    }

    public Guid AlertId { get; private set; }
    public AlertType Type { get; private set; }
    public AlertStatus Status { get; private set; }

    /// <summary>What the alert is about (product, channel + product, or task); unique among unresolved alerts.</summary>
    public string DedupKey { get; private set; } = null!;

    public Guid? ProductId { get; private set; }
    public Guid? ChannelId { get; private set; }

    /// <summary>The latest task of the alert (failures).</summary>
    public Guid? TaskId { get; private set; }

    /// <summary>The latest order of the alert (insufficient stock; a failed task that has one).</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>Error code of the latest failure (failures only).</summary>
    public string? ErrorCode { get; private set; }

    public DateTimeOffset FirstOccurredAt { get; private set; }
    public DateTimeOffset LastOccurredAt { get; private set; }
    public int OccurrenceCount { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>User id of who acknowledged it.</summary>
    public string? AcknowledgedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Product? Product { get; private set; }
    public Channel? Channel { get; private set; }
}

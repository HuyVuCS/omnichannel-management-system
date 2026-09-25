using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Business/audit event queryable from the dashboard. Deliberately has no foreign keys so that
/// logging never fails because a referenced task/order is missing or being deleted.
/// </summary>
public class SystemEventLog
{
    private SystemEventLog()
    {
    }

    public SystemEventLog(
        SystemEventLevel level,
        string eventType,
        string? correlationId,
        Guid? taskId,
        Guid? orderId,
        string? workerName,
        string? payload,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        Level = level;
        EventType = eventType;
        CorrelationId = correlationId;
        TaskId = taskId;
        OrderId = orderId;
        WorkerName = workerName;
        Payload = payload;
        CreatedAt = createdAt;
    }

    /// <summary>Database-generated (bigint identity).</summary>
    public long LogId { get; private set; }

    public SystemEventLevel Level { get; private set; }
    public string EventType { get; private set; } = null!;
    public string? CorrelationId { get; private set; }
    public Guid? TaskId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? WorkerName { get; private set; }

    /// <summary>JSON payload (jsonb).</summary>
    public string? Payload { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

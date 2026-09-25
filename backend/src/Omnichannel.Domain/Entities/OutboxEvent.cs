using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Transactional outbox row, written in the same transaction as the business change and
/// published to RabbitMQ afterwards by the Outbox Publisher. OutboxEventId is used as the
/// RabbitMQ messageId. Not a replacement for ProcessingAttempt history.
/// </summary>
public class OutboxEvent : IHasTimestamps
{
    private OutboxEvent()
    {
    }

    public OutboxEvent(string eventType, string aggregateId, string payload, string correlationId, Guid? inventoryMovementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        OutboxEventId = Guid.CreateVersion7();
        EventType = eventType;
        AggregateId = aggregateId;
        Payload = payload;
        CorrelationId = correlationId;
        InventoryMovementId = inventoryMovementId;
        Status = OutboxEventStatus.Pending;
    }

    public Guid OutboxEventId { get; private set; }
    public Guid? InventoryMovementId { get; private set; }
    public string EventType { get; private set; } = null!;
    public string AggregateId { get; private set; } = null!;

    /// <summary>JSON payload (jsonb).</summary>
    public string Payload { get; private set; } = null!;

    public string CorrelationId { get; private set; } = null!;
    public OutboxEventStatus Status { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public InventoryMovement? InventoryMovement { get; private set; }
}

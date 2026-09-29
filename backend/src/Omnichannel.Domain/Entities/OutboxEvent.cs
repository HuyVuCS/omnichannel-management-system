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

    /// <param name="outboxEventId">
    /// Pre-generated id, for callers that must reference the RabbitMQ messageId before creating the row
    /// (e.g. <c>ProcessingTask.MessageId</c>). A new UUIDv7 when null.
    /// </param>
    public OutboxEvent(
        string eventType,
        string aggregateId,
        string payload,
        string correlationId,
        Guid? inventoryMovementId,
        Guid? outboxEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (outboxEventId == Guid.Empty) throw new ArgumentException("Outbox event id must not be empty.", nameof(outboxEventId));

        OutboxEventId = outboxEventId ?? Guid.CreateVersion7();
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

    /// <summary>Number of failed publish attempts.</summary>
    public int PublishAttempts { get; private set; }

    /// <summary>Earliest time of the next publish attempt after a failure; null = publish as soon as possible.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Last publish error (sanitised, truncated to <see cref="MaxLastErrorLength"/>).</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public InventoryMovement? InventoryMovement { get; private set; }

    public const int MaxLastErrorLength = 1000;

    /// <summary>Only after a positive publisher confirm.</summary>
    public void MarkPublished(DateTimeOffset publishedAt)
    {
        EnsurePending();

        Status = OutboxEventStatus.Published;
        PublishedAt = publishedAt;
        NextAttemptAt = null;
    }

    /// <summary>
    /// Records a failed attempt. Below <paramref name="maxAttempts"/> the row stays Pending until
    /// <paramref name="now"/> + <paramref name="retryDelay"/>; at the limit it becomes Failed (terminal).
    /// </summary>
    /// <returns>True when the row has just become Failed.</returns>
    public bool RecordPublishFailure(string error, DateTimeOffset now, TimeSpan retryDelay, int maxAttempts)
    {
        EnsurePending();
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        PublishAttempts++;
        LastError = error.Length <= MaxLastErrorLength ? error : error[..MaxLastErrorLength];

        if (PublishAttempts >= maxAttempts)
        {
            Status = OutboxEventStatus.Failed;
            NextAttemptAt = null;
            return true;
        }

        NextAttemptAt = now + retryDelay;
        return false;
    }

    private void EnsurePending()
    {
        if (Status != OutboxEventStatus.Pending)
        {
            throw new InvalidOperationException($"Outbox event {OutboxEventId} is {Status}, not Pending.");
        }
    }
}

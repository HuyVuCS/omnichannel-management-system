using System.Text.Json;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Body of every RabbitMQ message (doc §10), serialised with <see cref="MessageJson"/>.
/// <see cref="MessageId"/> is the <c>OutboxEventId</c>; consumers deduplicate by it (delivery is at-least-once).
/// </summary>
/// <param name="TaskId">The processing task the message drives, when it already exists (<c>order.created</c>); otherwise null.</param>
/// <param name="OccurredAt">When the business change happened (<c>OutboxEvent.CreatedAt</c>), not when it was published.</param>
/// <param name="Payload">The event payload as a JSON object (not a string).</param>
public sealed record MessageEnvelope(
    string MessageId,
    string CorrelationId,
    Guid? TaskId,
    string EventType,
    DateTimeOffset OccurredAt,
    JsonElement Payload)
{
    public static MessageEnvelope FromOutboxEvent(OutboxEvent outboxEvent)
    {
        using var payload = JsonDocument.Parse(outboxEvent.Payload);

        return new MessageEnvelope(
            outboxEvent.OutboxEventId.ToString(),
            outboxEvent.CorrelationId,
            TaskIdOf(outboxEvent),
            outboxEvent.EventType,
            outboxEvent.CreatedAt,
            payload.RootElement.Clone());
    }

    /// <summary>
    /// <c>order.created</c> rows use the task id as aggregate id (task 004). <c>inventory.changed</c> has no task yet:
    /// stock-sync tasks are created per channel by the consumer.
    /// </summary>
    private static Guid? TaskIdOf(OutboxEvent outboxEvent) =>
        outboxEvent.EventType == EventTypes.OrderCreated && Guid.TryParse(outboxEvent.AggregateId, out var taskId)
            ? taskId
            : null;
}

using System.Text.Json;
using Omnichannel.Application.Common.Messaging;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>A well-formed <c>order.created</c> message.</summary>
public sealed record OrderMessage(MessageEnvelope Envelope, Guid TaskId, OrderCreatedPayload Order);

/// <summary>
/// Parses an <c>order.created</c> delivery. Anything that can never be processed, however often it is redelivered, is
/// <b>poison</b> and goes to the DLQ: invalid JSON, wrong event type, missing task id, missing or inconsistent payload.
/// (An unknown task id is detected later, against the database.)
/// </summary>
public static class OrderMessageParser
{
    public static bool TryParse(ReadOnlyMemory<byte> body, out OrderMessage? message, out string? poisonReason)
    {
        message = null;
        poisonReason = Validate(body, out var parsed);
        if (poisonReason is null)
        {
            message = parsed;
        }

        return poisonReason is null;
    }

    private static string? Validate(ReadOnlyMemory<byte> body, out OrderMessage? message)
    {
        message = null;

        MessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope>(body.Span, MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return $"Body is not a valid message envelope: {exception.Message}";
        }

        if (envelope is null)
        {
            return "Body is empty (null envelope).";
        }

        if (envelope.EventType != EventTypes.OrderCreated)
        {
            return $"Unexpected event type '{envelope.EventType}'; expected '{EventTypes.OrderCreated}'.";
        }

        if (envelope.TaskId is not { } taskId || taskId == Guid.Empty)
        {
            return "Envelope has no taskId.";
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageId) || string.IsNullOrWhiteSpace(envelope.CorrelationId))
        {
            return "Envelope has no messageId or correlationId.";
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return "Envelope payload is not a JSON object.";
        }

        OrderCreatedPayload? order;
        try
        {
            order = envelope.Payload.Deserialize<OrderCreatedPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return $"Payload is not a valid order: {exception.Message}";
        }

        if (order is null)
        {
            return "Payload is empty.";
        }

        if (order.TaskId != taskId)
        {
            return $"Payload taskId {order.TaskId} does not match envelope taskId {taskId}.";
        }

        if (order.ChannelId == Guid.Empty || order.CustomerId == Guid.Empty || string.IsNullOrWhiteSpace(order.ExternalOrderId))
        {
            return "Payload lacks channelId, customerId or externalOrderId.";
        }

        if (order.Items is not { Count: > 0 } items)
        {
            return "Payload has no items.";
        }

        if (items.Any(i => i is null || i.ProductId == Guid.Empty || i.Stock <= 0 || i.UnitPrice < 0))
        {
            return "Payload has an item without productId, with a non-positive stock or a negative unit price.";
        }

        if (items.Select(i => i.ProductId).Distinct().Count() != items.Count)
        {
            return "Payload contains the same product more than once.";
        }

        message = new OrderMessage(envelope, taskId, order);
        return null;
    }
}

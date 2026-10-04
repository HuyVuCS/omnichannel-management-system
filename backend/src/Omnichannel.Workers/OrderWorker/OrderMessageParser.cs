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
    public static bool TryParse(ReadOnlyMemory<byte> body, out OrderMessage? message, out MessageRejection? rejection)
    {
        message = null;
        rejection = Validate(body, out var parsed);
        if (rejection is null)
        {
            message = parsed;
        }

        return rejection is null;
    }

    private static MessageRejection? Validate(ReadOnlyMemory<byte> body, out OrderMessage? message)
    {
        message = null;

        MessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope>(body.Span, MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidJson, $"Body is not a valid message envelope: {exception.Message}");
        }

        if (envelope is null)
        {
            return new(MessageRejectionCodes.EmptyBody, "Body is empty (null envelope).");
        }

        if (envelope.EventType != EventTypes.OrderCreated)
        {
            return new(
                MessageRejectionCodes.UnexpectedEventType,
                $"Unexpected event type '{envelope.EventType}'; expected '{EventTypes.OrderCreated}'.");
        }

        if (envelope.TaskId is not { } taskId || taskId == Guid.Empty)
        {
            return new(MessageRejectionCodes.MissingTaskId, "Envelope has no taskId.");
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageId) || string.IsNullOrWhiteSpace(envelope.CorrelationId))
        {
            return new(MessageRejectionCodes.MissingMessageIds, "Envelope has no messageId or correlationId.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return new(MessageRejectionCodes.PayloadNotObject, "Envelope payload is not a JSON object.");
        }

        OrderCreatedPayload? order;
        try
        {
            order = envelope.Payload.Deserialize<OrderCreatedPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidPayload, $"Payload is not a valid order: {exception.Message}");
        }

        if (order is null)
        {
            return new(MessageRejectionCodes.EmptyPayload, "Payload is empty.");
        }

        if (order.TaskId != taskId)
        {
            return new(
                MessageRejectionCodes.TaskIdMismatch, $"Payload taskId {order.TaskId} does not match envelope taskId {taskId}.");
        }

        if (order.ChannelId == Guid.Empty || order.CustomerId == Guid.Empty || string.IsNullOrWhiteSpace(order.ExternalOrderId))
        {
            return new(MessageRejectionCodes.MissingOrderFields, "Payload lacks channelId, customerId or externalOrderId.");
        }

        if (order.Items is not { Count: > 0 } items)
        {
            return new(MessageRejectionCodes.NoItems, "Payload has no items.");
        }

        if (items.Any(i => i is null || i.ProductId == Guid.Empty || i.Stock <= 0 || i.UnitPrice < 0))
        {
            return new(
                MessageRejectionCodes.InvalidItem,
                "Payload has an item without productId, with a non-positive stock or a negative unit price.");
        }

        if (items.Select(i => i.ProductId).Distinct().Count() != items.Count)
        {
            return new(MessageRejectionCodes.DuplicateProduct, "Payload contains the same product more than once.");
        }

        message = new OrderMessage(envelope, taskId, order);
        return null;
    }
}

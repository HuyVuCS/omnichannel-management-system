using System.Text.Json;
using Omnichannel.Application.Common.Messaging;

namespace Omnichannel.Workers.StockSyncWorker;

/// <summary>
/// A well-formed message of a stock-sync queue: either an <c>inventory.changed</c> (every channel gets a copy; the task is
/// found or created per channel) or a directed <c>stock.sync.requested</c> (names an existing task).
/// </summary>
/// <param name="TaskId">The task of a <c>stock.sync.requested</c>; null for <c>inventory.changed</c>.</param>
public sealed record StockSyncMessage(MessageEnvelope Envelope, Guid ProductId, Guid? TaskId, Guid? ChannelId)
{
    public bool IsDirected => TaskId is not null;
}

/// <summary>
/// Parses a delivery of a stock-sync queue. Anything that can never be processed, however often it is redelivered, is
/// <b>poison</b> and goes to the DLQ: invalid JSON, an event type the queue does not handle, missing ids, inconsistent
/// payload. (An unknown task or a task of another channel is detected later, against the database.)
/// </summary>
public static class StockSyncMessageParser
{
    // Column lengths of ProcessingTasks.MessageId / CorrelationId: longer values could not be stored with the task.
    private const int MaxIdLength = 100;

    public static bool TryParse(ReadOnlyMemory<byte> body, out StockSyncMessage? message, out MessageRejection? rejection)
    {
        message = null;
        rejection = Validate(body, out var parsed);
        if (rejection is null)
        {
            message = parsed;
        }

        return rejection is null;
    }

    private static MessageRejection? Validate(ReadOnlyMemory<byte> body, out StockSyncMessage? message)
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

        if (envelope.EventType is not (EventTypes.InventoryChanged or EventTypes.StockSyncRequested))
        {
            return new(
                MessageRejectionCodes.UnexpectedEventType,
                $"Unexpected event type '{envelope.EventType}'; expected '{EventTypes.InventoryChanged}' or '{EventTypes.StockSyncRequested}'.");
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageId) || string.IsNullOrWhiteSpace(envelope.CorrelationId))
        {
            return new(MessageRejectionCodes.MissingMessageIds, "Envelope has no messageId or correlationId.");
        }

        if (envelope.MessageId.Length > MaxIdLength || envelope.CorrelationId.Length > MaxIdLength)
        {
            return new(
                MessageRejectionCodes.MessageIdsTooLong,
                $"Envelope messageId or correlationId is longer than {MaxIdLength} characters.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return new(MessageRejectionCodes.PayloadNotObject, "Envelope payload is not a JSON object.");
        }

        return envelope.EventType == EventTypes.InventoryChanged
            ? ValidateInventoryChanged(envelope, out message)
            : ValidateSyncRequested(envelope, out message);
    }

    private static MessageRejection? ValidateInventoryChanged(MessageEnvelope envelope, out StockSyncMessage? message)
    {
        message = null;

        InventoryChangedPayload? change;
        try
        {
            change = envelope.Payload.Deserialize<InventoryChangedPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidPayload, $"Payload is not a valid inventory change: {exception.Message}");
        }

        if (change is null || change.ProductId == Guid.Empty)
        {
            return new(MessageRejectionCodes.MissingProductId, "Payload has no productId.");
        }

        // The stock values of the payload are a snapshot and are not used: the worker reads the current stock.
        message = new StockSyncMessage(envelope, change.ProductId, TaskId: null, ChannelId: null);
        return null;
    }

    private static MessageRejection? ValidateSyncRequested(MessageEnvelope envelope, out StockSyncMessage? message)
    {
        message = null;

        if (envelope.TaskId is not { } taskId || taskId == Guid.Empty)
        {
            return new(MessageRejectionCodes.MissingTaskId, "Envelope has no taskId.");
        }

        StockSyncRequestedPayload? request;
        try
        {
            request = envelope.Payload.Deserialize<StockSyncRequestedPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidPayload, $"Payload is not a valid stock-sync request: {exception.Message}");
        }

        if (request is null)
        {
            return new(MessageRejectionCodes.EmptyPayload, "Payload is empty.");
        }

        if (request.TaskId != taskId)
        {
            return new(
                MessageRejectionCodes.TaskIdMismatch, $"Payload taskId {request.TaskId} does not match envelope taskId {taskId}.");
        }

        if (request.ChannelId == Guid.Empty || request.ProductId == Guid.Empty)
        {
            return new(MessageRejectionCodes.MissingSyncTarget, "Payload lacks channelId or productId.");
        }

        message = new StockSyncMessage(envelope, request.ProductId, taskId, request.ChannelId);
        return null;
    }
}

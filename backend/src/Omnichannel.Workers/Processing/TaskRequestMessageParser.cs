using System.Text.Json;
using Omnichannel.Application.Common.Messaging;

namespace Omnichannel.Workers.Processing;

/// <summary>A well-formed message whose payload is <c>{ taskId }</c>: it names its task and nothing else.</summary>
public sealed record TaskRequestMessage(MessageEnvelope Envelope, Guid TaskId);

/// <summary>
/// Parses a delivery of a queue whose messages only name a task (<c>invoice.requested</c>, <c>email.requested</c>).
/// Anything that can never be processed, however often it is redelivered, is <b>poison</b> and goes to the DLQ: invalid
/// JSON, another event type, missing ids, inconsistent payload. (An unknown task, or a task of another kind, is detected
/// later, against the database.)
/// </summary>
public static class TaskRequestMessageParser
{
    public static bool TryParse(
        ReadOnlyMemory<byte> body,
        string eventType,
        out TaskRequestMessage? message,
        out MessageRejection? rejection)
    {
        message = null;
        rejection = Validate(body, eventType, out var parsed);
        if (rejection is null)
        {
            message = parsed;
        }

        return rejection is null;
    }

    private static MessageRejection? Validate(ReadOnlyMemory<byte> body, string eventType, out TaskRequestMessage? message)
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

        if (envelope.EventType != eventType)
        {
            return new(
                MessageRejectionCodes.UnexpectedEventType,
                $"Unexpected event type '{envelope.EventType}'; expected '{eventType}'.");
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

        TaskIdPayload? request;
        try
        {
            request = envelope.Payload.Deserialize<TaskIdPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidPayload, $"Payload is not a valid task request: {exception.Message}");
        }

        if (request is null)
        {
            return new(MessageRejectionCodes.EmptyPayload, "Payload is empty.");
        }

        if (request.TaskId != taskId)
        {
            return new(MessageRejectionCodes.TaskIdMismatch, $"Payload taskId {request.TaskId} does not match envelope taskId {taskId}.");
        }

        message = new TaskRequestMessage(envelope, taskId);
        return null;
    }

    /// <summary>The shape of <c>InvoiceRequestedPayload</c> and <c>EmailRequestedPayload</c>.</summary>
    private sealed record TaskIdPayload(Guid TaskId);
}

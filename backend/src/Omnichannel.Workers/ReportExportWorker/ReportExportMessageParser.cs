using System.Text.Json;
using Omnichannel.Application.Common.Messaging;

namespace Omnichannel.Workers.ReportExportWorker;

/// <summary>A well-formed <c>report.export.requested</c> message: it names the export task and nothing else.</summary>
public sealed record ReportExportMessage(MessageEnvelope Envelope, Guid TaskId);

/// <summary>
/// Parses a delivery of the export queue. Anything that can never be processed, however often it is redelivered, is
/// <b>poison</b> and goes to the DLQ: invalid JSON, another event type, missing ids, inconsistent payload. (An unknown
/// task, or a task that is no export, is detected later, against the database.)
/// </summary>
public static class ReportExportMessageParser
{
    public static bool TryParse(ReadOnlyMemory<byte> body, out ReportExportMessage? message, out MessageRejection? rejection)
    {
        message = null;
        rejection = Validate(body, out var parsed);
        if (rejection is null)
        {
            message = parsed;
        }

        return rejection is null;
    }

    private static MessageRejection? Validate(ReadOnlyMemory<byte> body, out ReportExportMessage? message)
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

        if (envelope.EventType != EventTypes.ReportExportRequested)
        {
            return new(
                MessageRejectionCodes.UnexpectedEventType,
                $"Unexpected event type '{envelope.EventType}'; expected '{EventTypes.ReportExportRequested}'.");
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

        ReportExportRequestedPayload? request;
        try
        {
            request = envelope.Payload.Deserialize<ReportExportRequestedPayload>(MessageJson.Options);
        }
        catch (JsonException exception)
        {
            return new(MessageRejectionCodes.InvalidPayload, $"Payload is not a valid export request: {exception.Message}");
        }

        if (request is null)
        {
            return new(MessageRejectionCodes.EmptyPayload, "Payload is empty.");
        }

        if (request.TaskId != taskId)
        {
            return new(MessageRejectionCodes.TaskIdMismatch, $"Payload taskId {request.TaskId} does not match envelope taskId {taskId}.");
        }

        message = new ReportExportMessage(envelope, taskId);
        return null;
    }
}

namespace Omnichannel.Application.Common.Messaging;

/// <summary>SystemEventLog event types written by the Outbox Publisher.</summary>
public static class OutboxEvents
{
    /// <summary>
    /// A message was given up after the publish attempt limit (Error). Payload:
    /// <c>{outboxEventId, eventType, publishAttempts, lastError}</c>; <c>lastError</c> is broker or exception text and is
    /// never returned by the API.
    /// </summary>
    public const string PublishFailed = "outbox.publish_failed";
}

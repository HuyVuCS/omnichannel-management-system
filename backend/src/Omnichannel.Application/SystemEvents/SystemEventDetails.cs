using System.Text.Json;
using System.Text.Json.Nodes;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Users;

namespace Omnichannel.Application.SystemEvents;

/// <summary>
/// What of a <c>SystemEventLog</c> payload leaves the API. Payloads are free-form JSON written for operators, so the
/// rule is an <b>allowlist</b> per event type: only the top-level fields named here are returned, with their values as
/// they are stored; an event type without an entry returns no details at all.
/// <para>
/// <b>Adding an event type:</b> give it a constant in its feature's <c>…Events</c> class and an entry here with the fields
/// that are safe to show - the application's own ids, codes, counts and names; never exception or broker text and never a
/// fragment of a message the application did not write. A unit test fails for a constant without an entry.
/// </para>
/// </summary>
public static class SystemEventDetails
{
    /// <summary>Payload fields that hold the id of the user who did it (one per event type at most).</summary>
    public static readonly IReadOnlyList<string> ActorFields = ["actorUserId", "requestedBy", "acknowledgedBy"];

    /// <summary>Payload field that holds the id of the user it was done to.</summary>
    public const string TargetField = "targetUserId";

    private static readonly string[] UserChange = [TargetField, "actorUserId"];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedFields =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [InventoryEvents.Imported] = ["referenceId", "itemCount", "totalQuantity"],
            [OrderProcessingEvents.InsufficientStock] = ["orderId", "taskId", "items"],
            [OrderProcessingEvents.OrderAlreadyExists] = ["taskId", "orderId", "externalOrderId"],
            // Not "reason": it can quote the JSON parser's exception and values of the rejected message (its event type).
            [OrderProcessingEvents.MessageRejected] = ["messageId"],
            [StockSyncEvents.MessageRejected] = ["messageId", "queue"],
            // "errorMessage" is the attempt's message, sanitised when it is stored (doc §5.1).
            [ProcessingEvents.Failed] = ["taskId", "taskType", "attemptNo", "errorCode", "errorMessage", "queue"],
            [ProcessingEvents.RetryRequested] =
                ["taskId", "taskType", "requestedBy", "previousMessageId", "newMessageId", "attemptBudgetStart"],
            // Never "lastError": broker or exception text.
            [OutboxEvents.PublishFailed] = ["outboxEventId", "eventType", "publishAttempts"],
            [AlertEvents.Acknowledged] = ["alertId", "type", "acknowledgedBy"],
            [UserEvents.Created] = [.. UserChange, "role"],
            [UserEvents.RoleChanged] = [.. UserChange, "from", "to"],
            [UserEvents.Locked] = UserChange,
            [UserEvents.Unlocked] = UserChange,
            [UserEvents.PasswordReset] = UserChange
        };

    /// <summary>
    /// The allowed fields of <paramref name="payload"/>, in the order of the allowlist; null when the event type has no
    /// entry or the payload is missing or not a JSON object.
    /// </summary>
    public static JsonObject? Filter(string eventType, string? payload)
    {
        if (!AllowedFields.TryGetValue(eventType, out var allowed) || string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        JsonObject? stored;
        try
        {
            stored = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null)
        {
            return null;
        }

        var details = new JsonObject();
        foreach (var field in allowed)
        {
            if (stored.TryGetPropertyValue(field, out var value))
            {
                details[field] = value?.DeepClone();
            }
        }

        return details;
    }

    /// <summary>The user who did it, when the event names one; null for an actor that is no user id (<c>system</c>).</summary>
    public static Guid? ActorIdOf(JsonObject? details) =>
        ActorFields.Select(field => UserIdOf(details, field)).FirstOrDefault(id => id is not null);

    /// <summary>The user it was done to, when the event names one.</summary>
    public static Guid? TargetIdOf(JsonObject? details) => UserIdOf(details, TargetField);

    private static Guid? UserIdOf(JsonObject? details, string field) =>
        details is not null
        && details.TryGetPropertyValue(field, out var value)
        && value is JsonValue text
        && text.TryGetValue<string>(out var raw)
        && Guid.TryParse(raw, out var id)
            ? id
            : null;
}

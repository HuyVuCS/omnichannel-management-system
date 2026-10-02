using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Realtime;

/// <summary>
/// The realtime channel between whoever commits a change (API or Workers) and the API instances that push to browsers:
/// PostgreSQL <c>NOTIFY</c> on <see cref="Name"/>. Hints only say <i>what kind of thing changed and which ids</i>; clients
/// refetch through the API. SignalR and this channel are never the source of truth (doc §12).
/// </summary>
public static class RealtimeChannel
{
    /// <summary>PostgreSQL notification channel (<c>LISTEN omnichannel_ui</c>).</summary>
    public const string Name = "omnichannel_ui";

    /// <summary>PostgreSQL rejects a <c>NOTIFY</c> payload of 8000 bytes or more.</summary>
    public const int MaxPayloadBytes = 7999;

    /// <summary>Name of the hub message clients receive.</summary>
    public const string HubMethod = "hints";
}

public static class RealtimeEventTypes
{
    /// <summary>An order was stored (<c>orderId</c>, <c>channelId</c>, <c>status</c>).</summary>
    public const string OrderCreated = "order.created";

    /// <summary>Stock of some products changed (<c>productIds</c>, or <c>all</c> when there are too many to list).</summary>
    public const string InventoryChanged = "inventory.changed";

    /// <summary>
    /// A processing task was created or its status changed (<c>taskId</c>, <c>taskType</c>, <c>status</c>, <c>orderId</c>,
    /// <c>channelId</c>, <c>productId</c>). Covers the document's <c>processing.failed</c> and <c>sync.succeeded/failed</c>:
    /// the client filters by <c>taskType</c> and <c>status</c>.
    /// </summary>
    public const string ProcessingTaskChanged = "processing.task.changed";

    /// <summary>
    /// An alert was opened, refreshed, acknowledged or resolved (<c>alertId</c>, <c>alertType</c>, <c>status</c>,
    /// <c>productId</c>, <c>channelId</c>).
    /// </summary>
    public const string AlertChanged = "alert.changed";

    /// <summary>A low-stock alert was opened (<c>alertId</c>, <c>productId</c>); sent in addition to <see cref="AlertChanged"/>.</summary>
    public const string LowStockCreated = "low_stock.created";
}

/// <summary>
/// One hint. Only the members of its <see cref="Type"/> are set; the others are omitted from the JSON.
/// <see cref="All"/> = "more changed than is listed here: refetch everything of this type".
/// </summary>
public sealed record RealtimeHint(string Type)
{
    public Guid? OrderId { get; init; }
    public Guid? ChannelId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? TaskId { get; init; }
    public string? TaskType { get; init; }
    public string? Status { get; init; }
    public Guid? AlertId { get; init; }
    public string? AlertType { get; init; }
    public IReadOnlyList<Guid>? ProductIds { get; init; }
    public bool? All { get; init; }

    public static RealtimeHint OrderCreated(Guid orderId, Guid channelId, OrderStatus status) =>
        new(RealtimeEventTypes.OrderCreated) { OrderId = orderId, ChannelId = channelId, Status = status.ToString() };

    /// <summary>At most <see cref="RealtimeHintBatch.MaxProductIds"/> ids are listed; beyond that the hint says <c>all</c>.</summary>
    public static RealtimeHint InventoryChanged(IReadOnlyCollection<Guid> productIds) =>
        productIds.Count > RealtimeHintBatch.MaxProductIds
            ? AllOf(RealtimeEventTypes.InventoryChanged)
            : new RealtimeHint(RealtimeEventTypes.InventoryChanged) { ProductIds = productIds.ToList() };

    public static RealtimeHint TaskChanged(
        Guid taskId,
        ProcessingTaskType taskType,
        ProcessingTaskStatus status,
        Guid? orderId,
        Guid? channelId,
        Guid? productId) =>
        new(RealtimeEventTypes.ProcessingTaskChanged)
        {
            TaskId = taskId,
            TaskType = taskType.ToString(),
            Status = status.ToString(),
            OrderId = orderId,
            ChannelId = channelId,
            ProductId = productId
        };

    public static RealtimeHint AlertChanged(Guid alertId, AlertType alertType, AlertStatus status, Guid? productId, Guid? channelId) =>
        new(RealtimeEventTypes.AlertChanged)
        {
            AlertId = alertId,
            AlertType = alertType.ToString(),
            Status = status.ToString(),
            ProductId = productId,
            ChannelId = channelId
        };

    public static RealtimeHint LowStockCreated(Guid alertId, Guid productId) =>
        new(RealtimeEventTypes.LowStockCreated) { AlertId = alertId, ProductId = productId };

    public static RealtimeHint AllOf(string type) => new(type) { All = true };
}

/// <summary>Payload of one <c>NOTIFY</c>: every hint of one committed unit of work.</summary>
public sealed record RealtimeHintBatch(IReadOnlyList<RealtimeHint> Events)
{
    /// <summary>Product ids listed in one <c>inventory.changed</c> hint (100 ids are about 3.9 kB of JSON).</summary>
    public const int MaxProductIds = 100;

    /// <summary>
    /// The JSON to send, always below PostgreSQL's limit: if the hints of one unit of work do not fit (many tasks or orders
    /// in one save), each event type is reduced to a single "all of this type" hint.
    /// </summary>
    public string ToPayload()
    {
        var payload = JsonSerializer.Serialize(this, RealtimeJson.Options);
        if (Encoding.UTF8.GetByteCount(payload) <= RealtimeChannel.MaxPayloadBytes)
        {
            return payload;
        }

        var reduced = new RealtimeHintBatch(Events.Select(e => e.Type).Distinct().Select(RealtimeHint.AllOf).ToList());
        return JsonSerializer.Serialize(reduced, RealtimeJson.Options);
    }

    /// <summary>Null when <paramref name="payload"/> is not a hint batch (not JSON, no events, an event without a type).</summary>
    public static RealtimeHintBatch? TryParse(string payload)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<RealtimeHintBatch>(payload, RealtimeJson.Options);
            return batch?.Events is { Count: > 0 } events && events.All(e => e is not null && !string.IsNullOrWhiteSpace(e.Type))
                ? batch
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a browser receives as hub message <see cref="RealtimeChannel.HubMethod"/>: the hints of a short window in arrival
/// order. <see cref="Resync"/> = hints may have been missed (the API lost its listener connection, or there were too many
/// to list): refetch everything that is on screen.
/// </summary>
public sealed record RealtimeHintMessage(IReadOnlyList<RealtimeHint> Events, bool Resync);

/// <summary>JSON of the notification payload: camelCase, members without a value omitted.</summary>
public static class RealtimeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Why a worker sent a message to the DLQ without processing it.
/// </summary>
/// <param name="Code">One of <see cref="MessageRejectionCodes"/>: stored as <c>reasonCode</c> and returned by the Logs API.</param>
/// <param name="Reason">Text for operators with database access: stored as <c>reason</c>, never returned - it can quote
/// the JSON parser's exception and values of the rejected message.</param>
public sealed record MessageRejection(string Code, string Reason);

/// <summary>
/// The <c>reasonCode</c> of <c>order.message_rejected</c>, <c>stock_sync.message_rejected</c>,
/// <c>report_export.message_rejected</c>, <c>invoice.message_rejected</c> and <c>email.message_rejected</c>: one code per
/// way a message can be rejected, shared by the workers because most of their checks are the same. The client translates the
/// codes (<c>logs.rejection.*</c>); a new code needs a label there, until then its raw value is shown.
/// </summary>
public static class MessageRejectionCodes
{
    // ---- the envelope (both workers) ----

    /// <summary>The body is not JSON, or not the JSON of an envelope.</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>The body is the JSON value <c>null</c>.</summary>
    public const string EmptyBody = "empty_body";

    /// <summary>An event type the queue does not handle.</summary>
    public const string UnexpectedEventType = "unexpected_event_type";

    /// <summary>The envelope names no task although the event type needs one.</summary>
    public const string MissingTaskId = "missing_task_id";

    /// <summary>The envelope has no message id or no correlation id.</summary>
    public const string MissingMessageIds = "missing_message_ids";

    /// <summary>Message id or correlation id longer than the columns that store them (stock sync).</summary>
    public const string MessageIdsTooLong = "message_ids_too_long";

    /// <summary>The payload is not a JSON object.</summary>
    public const string PayloadNotObject = "payload_not_object";

    // ---- the payload ----

    /// <summary>The payload cannot be read as the payload of its event type.</summary>
    public const string InvalidPayload = "invalid_payload";

    /// <summary>The payload is empty.</summary>
    public const string EmptyPayload = "empty_payload";

    /// <summary>Payload and envelope name different tasks.</summary>
    public const string TaskIdMismatch = "task_id_mismatch";

    /// <summary>An order without channel, customer or external order id.</summary>
    public const string MissingOrderFields = "missing_order_fields";

    /// <summary>An order without items.</summary>
    public const string NoItems = "no_items";

    /// <summary>An order item without product, with a non-positive quantity or a negative price.</summary>
    public const string InvalidItem = "invalid_item";

    /// <summary>An order with the same product on more than one item.</summary>
    public const string DuplicateProduct = "duplicate_product";

    /// <summary>An inventory change without a product.</summary>
    public const string MissingProductId = "missing_product_id";

    /// <summary>A stock-sync request without channel or product.</summary>
    public const string MissingSyncTarget = "missing_sync_target";

    // ---- against the database ----

    /// <summary>The task the message names does not exist.</summary>
    public const string UnknownTask = "unknown_task";

    /// <summary>
    /// The task is not what the queue processes: not a stock-sync task for the product and channel of the message, not
    /// an export task with its export row, or not an invoice / e-mail task with its order.
    /// </summary>
    public const string TaskMismatch = "task_mismatch";

    /// <summary>The task belongs to another channel than the queue the message arrived on.</summary>
    public const string TaskOfOtherChannel = "task_of_other_channel";

    public static readonly IReadOnlyList<string> All =
    [
        InvalidJson, EmptyBody, UnexpectedEventType, MissingTaskId, MissingMessageIds, MessageIdsTooLong, PayloadNotObject,
        InvalidPayload, EmptyPayload, TaskIdMismatch, MissingOrderFields, NoItems, InvalidItem, DuplicateProduct,
        MissingProductId, MissingSyncTarget, UnknownTask, TaskMismatch, TaskOfOtherChannel
    ];
}

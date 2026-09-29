using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Orders;

/// <summary>Body of <c>POST /api/orders/{slug}</c> (doc §3.1).</summary>
public sealed record SubmitOrderRequest(string ExternalOrderId, Guid CustomerId, IReadOnlyList<SubmitOrderItem> Items);

public sealed record SubmitOrderItem(Guid ProductId, int Stock);

/// <summary>202 body: the order is processed asynchronously by the Order Worker; track it by <see cref="TaskId"/>.</summary>
/// <param name="Status">Current status of the processing task (for a duplicate, the existing task's status).</param>
/// <param name="CorrelationId">Correlation id of the task (for a duplicate, the one of the original submission).</param>
/// <param name="Duplicate">True when the same channel + external order id was already accepted; nothing new was written.</param>
public sealed record SubmitOrderResponse(Guid TaskId, ProcessingTaskStatus Status, string CorrelationId, bool Duplicate);

public static class OrderErrorCodes
{
    /// <summary>The channel slug does not exist or the channel is inactive.</summary>
    public const string ChannelNotFound = "channel_not_found";

    /// <summary>The external order id was already submitted on this channel with different content.</summary>
    public const string OrderConflict = "order_conflict";
}

using Omnichannel.Application.Orders;

namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Payload of <see cref="EventTypes.OrderCreated"/>: the normalised order. Kept as its own record (same fields as
/// <see cref="NormalizedOrder"/>) so the message contract changes only deliberately.
/// </summary>
public sealed record OrderCreatedPayload(
    Guid TaskId,
    Guid ChannelId,
    string ChannelSlug,
    string ExternalOrderId,
    Guid CustomerId,
    IReadOnlyList<OrderCreatedItem> Items,
    DateTimeOffset ReceivedAt,
    string ReceivedBy)
{
    public static OrderCreatedPayload From(NormalizedOrder order) => new(
        order.TaskId,
        order.ChannelId,
        order.ChannelSlug,
        order.ExternalOrderId,
        order.CustomerId,
        order.Items.Select(i => new OrderCreatedItem(i.ProductId, i.Stock, i.UnitPrice)).ToList(),
        order.ReceivedAt,
        order.ReceivedBy);
}

public sealed record OrderCreatedItem(Guid ProductId, int Stock, decimal UnitPrice);

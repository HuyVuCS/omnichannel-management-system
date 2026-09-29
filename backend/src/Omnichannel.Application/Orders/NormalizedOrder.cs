namespace Omnichannel.Application.Orders;

/// <summary>
/// Channel-independent order ("Common Order DTO", doc §8.1) built at intake by the order-entry API and, later, the webhook.
/// Prices are a snapshot of <c>Product.Price</c> at intake time.
/// </summary>
/// <param name="ReceivedBy">User id of the submitter, or a system actor.</param>
public sealed record NormalizedOrder(
    Guid TaskId,
    Guid ChannelId,
    string ChannelSlug,
    string ExternalOrderId,
    Guid CustomerId,
    IReadOnlyList<NormalizedOrderItem> Items,
    DateTimeOffset ReceivedAt,
    string ReceivedBy);

public sealed record NormalizedOrderItem(Guid ProductId, int Stock, decimal UnitPrice);

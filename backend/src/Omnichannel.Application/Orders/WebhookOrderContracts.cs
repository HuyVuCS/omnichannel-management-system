using Omnichannel.Application.Channels;

namespace Omnichannel.Application.Orders;

/// <summary>
/// Body of <c>POST /api/webhooks/{slug}</c>: a new order in the channel's own words. The channel names its items by the
/// external ids of their mappings and its customer by its own customer id; the API turns it into the order every intake
/// path shares (<see cref="SubmitOrderRequest"/>, doc §8.1 "Normalize → Common Order DTO").
/// </summary>
/// <param name="ExternalOrderId">The channel's order id: the idempotency key of the order on this channel.</param>
public sealed record WebhookOrderRequest(string ExternalOrderId, WebhookCustomer Customer, IReadOnlyList<WebhookOrderItem> Items);

/// <param name="ExternalCustomerId">The channel's id of the customer (trimmed, compared exactly).</param>
/// <param name="Name">Stored when the channel names this customer for the first time; ignored afterwards, like phone and e-mail.</param>
public sealed record WebhookCustomer(string ExternalCustomerId, string Name, string? Phone, string? Email);

/// <summary>
/// One line of a webhook order. Exactly one of <see cref="ExternalProductId"/> and <see cref="ExternalSku"/> names the
/// product (a blank one counts as not given): the external product id of a mapping, or the external SKU of a mapping
/// that has no external product id - how the channel knows the product (<see cref="ChannelProductKey"/>).
/// </summary>
public sealed record WebhookOrderItem(string? ExternalProductId, string? ExternalSku, int Quantity)
{
    /// <summary>The identifier that is given, trimmed; null when it is blank or missing.</summary>
    public static string? Given(string? identifier) => string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();

    // Methods, not properties: nothing that walks the properties of a request body (model validation, serialisers)
    // may call them on a line that names no product.

    /// <summary>True when exactly one of the two identifiers is given.</summary>
    public bool NamesOneProduct() => (Given(ExternalProductId) is null) != (Given(ExternalSku) is null);

    /// <summary>The key of the mapping this line names; only for a line that <see cref="NamesOneProduct"/>.</summary>
    public string Key() => ChannelProductKey.Of(Given(ExternalProductId), Given(ExternalSku));
}

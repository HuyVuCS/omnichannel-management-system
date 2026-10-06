namespace Omnichannel.Application.Channels;

/// <summary>
/// How a channel knows a mapped product: by the external product id of its mapping when it has one, otherwise by its
/// external SKU. The one rule for everything that speaks to a channel in the channel's words - stock pushes and reads
/// (the simulated channels key their state by it), and the items of a webhook order.
/// <para>
/// A key belongs to at most one mapping of a channel: the external product id is unique per channel, and so is the
/// external SKU among the mappings without an external product id (unique indexes on <c>ChannelProducts</c>).
/// </para>
/// </summary>
public static class ChannelProductKey
{
    private const string ExternalProductIdPrefix = "id:";
    private const string ExternalSkuPrefix = "sku:";

    /// <summary><c>id:{externalProductId}</c>, or <c>sku:{externalSku}</c> when there is no external product id.</summary>
    /// <exception cref="ArgumentException">Neither is given.</exception>
    public static string Of(string? externalProductId, string? externalSku) =>
        externalProductId is not null
            ? ExternalProductIdPrefix + externalProductId
            : externalSku is not null
                ? ExternalSkuPrefix + externalSku
                : throw new ArgumentException("A mapped product has an external product id or an external SKU.", nameof(externalProductId));
}

namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>
/// What a simulated channel holds for one of its products: the last stock value it accepted (table
/// <c>SimulatedChannelStocks</c>, decision 2026-10-05). It is <b>the channel's</b> data, not business data of this
/// system: keyed the way a channel knows a product (<see cref="ExternalKeyOf"/>), without a foreign key and without
/// audit fields. Only the simulator reads and writes it; with a real channel adapter the table stays empty.
/// </summary>
public sealed class SimulatedChannelStock
{
    public const int MaxChannelSlugLength = 64;

    /// <summary>The <c>id:</c> / <c>sku:</c> prefix plus an external id of a mapping (128).</summary>
    public const int MaxExternalKeyLength = 140;

    public const int MaxRequestIdLength = 100;

    private const string ExternalProductIdPrefix = "id:";
    private const string ExternalSkuPrefix = "sku:";

    private SimulatedChannelStock()
    {
    }

    public string ChannelSlug { get; private set; } = null!;

    public string ExternalKey { get; private set; } = null!;

    public int AvailableStock { get; private set; }

    /// <summary>The request id of the push that wrote the value (<c>{taskId}:{attemptNo}:{n}</c>).</summary>
    public string LastRequestId { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// How a channel identifies a product: by its external product id when the mapping has one, otherwise by its external
    /// SKU. The one rule for writing and for reading.
    /// </summary>
    public static string ExternalKeyOf(string? externalProductId, string? externalSku) =>
        externalProductId is not null
            ? ExternalProductIdPrefix + externalProductId
            : externalSku is not null
                ? ExternalSkuPrefix + externalSku
                : throw new ArgumentException("A mapped product has an external product id or an external SKU.", nameof(externalProductId));
}

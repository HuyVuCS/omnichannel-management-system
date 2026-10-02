using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Maps a central product to its identifier on an external channel. At least one of <see cref="ExternalProductId"/> and
/// <see cref="ExternalSku"/> is set: without one the channel cannot be told which product a stock value belongs to.
/// </summary>
public class ChannelProduct : AuditableEntity
{
    public const int MaxExternalIdLength = 128;

    private ChannelProduct()
    {
    }

    public ChannelProduct(Guid channelId, Guid productId, string? externalProductId, string? externalSku)
    {
        if (channelId == Guid.Empty) throw new ArgumentException("ChannelId is required.", nameof(channelId));
        if (productId == Guid.Empty) throw new ArgumentException("ProductId is required.", nameof(productId));

        ChannelProductId = Guid.CreateVersion7();
        ChannelId = channelId;
        ProductId = productId;
        SetExternalIds(externalProductId, externalSku);
        Status = ChannelProductStatus.Active;
    }

    public Guid ChannelProductId { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid ProductId { get; private set; }
    public string? ExternalProductId { get; private set; }
    public string? ExternalSku { get; private set; }
    public ChannelProductStatus Status { get; private set; }

    public Channel Channel { get; private set; } = null!;
    public Product Product { get; private set; } = null!;

    /// <summary>Channel and product of a mapping never change.</summary>
    /// <returns>
    /// True when the channel has to receive the product's stock again: the mapping became Active, or its external ids changed
    /// while it stays Active. Deactivating never asks for a sync.
    /// </returns>
    public bool Update(string? externalProductId, string? externalSku, ChannelProductStatus status)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown mapping status.");

        var (previousProductId, previousSku, previousStatus) = (ExternalProductId, ExternalSku, Status);
        SetExternalIds(externalProductId, externalSku);
        Status = status;

        if (status != ChannelProductStatus.Active)
        {
            return false;
        }

        return previousStatus != ChannelProductStatus.Active
            || !string.Equals(previousProductId, ExternalProductId, StringComparison.Ordinal)
            || !string.Equals(previousSku, ExternalSku, StringComparison.Ordinal);
    }

    private void SetExternalIds(string? externalProductId, string? externalSku)
    {
        var productId = Normalize(externalProductId, nameof(externalProductId));
        var sku = Normalize(externalSku, nameof(externalSku));
        if (productId is null && sku is null)
        {
            throw new ArgumentException("A mapping needs an external product id or an external SKU.", nameof(externalProductId));
        }

        ExternalProductId = productId;
        ExternalSku = sku;
    }

    /// <summary>Trimmed; blank becomes null (the unique index on the external product id ignores nulls, not empty strings).</summary>
    private static string? Normalize(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, MaxExternalIdLength, parameterName);
        return trimmed;
    }
}

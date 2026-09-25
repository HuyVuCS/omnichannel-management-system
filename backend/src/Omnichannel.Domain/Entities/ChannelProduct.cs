using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>Maps a central product to its identifier on an external channel.</summary>
public class ChannelProduct : AuditableEntity
{
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
        ExternalProductId = externalProductId?.Trim();
        ExternalSku = externalSku?.Trim();
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
}

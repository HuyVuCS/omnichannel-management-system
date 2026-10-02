using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Channels;

/// <summary>Channels and their product mappings (doc §7.1).</summary>
public interface IChannelService
{
    Task<IReadOnlyList<ChannelResponse>> ListAsync(CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    Task<ChannelResponse> GetAsync(Guid channelId, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.ConflictException">The slug is already used (<see cref="ChannelErrorCodes.SlugExists"/>).</exception>
    Task<ChannelResponse> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    Task<ChannelResponse> UpdateAsync(Guid channelId, UpdateChannelRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    Task<PagedResult<ChannelProductResponse>> ListProductsAsync(
        Guid channelId,
        ChannelProductListQuery query,
        CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">
    /// <see cref="ChannelProductErrorCodes.MappingNotFound"/>: no such mapping on this channel (also when it belongs to another one).
    /// </exception>
    Task<ChannelProductResponse> GetProductAsync(Guid channelId, Guid channelProductId, CancellationToken cancellationToken);

    /// <summary>
    /// Maps a product on the channel. When the channel is Active and has stock sync, a <c>StockSync</c> task and its directed
    /// <c>stock.sync.requested</c> message are written in the same transaction (initial sync).
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    /// <exception cref="FluentValidation.ValidationException">The product does not exist (<c>productId</c>).</exception>
    /// <exception cref="Common.Exceptions.ConflictException">
    /// <see cref="ChannelProductErrorCodes.MappingExists"/>, <see cref="ChannelProductErrorCodes.ExternalProductIdTaken"/>.
    /// </exception>
    Task<ChannelProductSavedResponse> CreateProductAsync(
        Guid channelId,
        CreateChannelProductRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes the external ids and the status of a mapping. Re-activating it, or changing its external ids while it is Active,
    /// requests a sync like <see cref="CreateProductAsync"/>; deactivating does nothing on the channel.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException"><see cref="ChannelProductErrorCodes.MappingNotFound"/>.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><see cref="ChannelProductErrorCodes.ExternalProductIdTaken"/>.</exception>
    Task<ChannelProductSavedResponse> UpdateProductAsync(
        Guid channelId,
        Guid channelProductId,
        UpdateChannelProductRequest request,
        CancellationToken cancellationToken);
}

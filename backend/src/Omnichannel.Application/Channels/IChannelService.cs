namespace Omnichannel.Application.Channels;

public interface IChannelService
{
    Task<IReadOnlyList<ChannelResponse>> ListAsync(CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    Task<ChannelResponse> GetAsync(Guid channelId, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.ConflictException">The slug is already used (<see cref="ChannelErrorCodes.SlugExists"/>).</exception>
    Task<ChannelResponse> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The channel does not exist.</exception>
    Task<ChannelResponse> UpdateAsync(Guid channelId, UpdateChannelRequest request, CancellationToken cancellationToken);
}

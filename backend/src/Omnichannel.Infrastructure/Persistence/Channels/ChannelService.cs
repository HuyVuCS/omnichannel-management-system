using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Channels;

internal sealed class ChannelService(OmnichannelDbContext dbContext) : IChannelService
{
    private static readonly Expression<Func<Channel, ChannelResponse>> ToResponse = c => new ChannelResponse(
        c.ChannelId, c.Name, c.Slug, c.Type, c.Status, c.CreatedAt, c.UpdatedAt);

    private static readonly Func<Channel, ChannelResponse> ToResponseCompiled = ToResponse.Compile();

    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Channels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.ChannelId)
            .Select(ToResponse)
            .ToListAsync(cancellationToken);

    public async Task<ChannelResponse> GetAsync(Guid channelId, CancellationToken cancellationToken) =>
        await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .Select(ToResponse)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(Channel), channelId);

    public async Task<ChannelResponse> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = new Channel(request.Name, request.Slug, request.Type);
        dbContext.Channels.Add(channel);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ChannelSlug))
        {
            throw new ConflictException(ChannelErrorCodes.SlugExists, $"A channel with slug '{channel.Slug}' already exists.");
        }

        return ToResponseCompiled(channel);
    }

    public async Task<ChannelResponse> UpdateAsync(Guid channelId, UpdateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await dbContext.Channels.SingleOrDefaultAsync(c => c.ChannelId == channelId, cancellationToken)
            ?? throw new NotFoundException(nameof(Channel), channelId);

        channel.Update(request.Name, request.Status);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponseCompiled(channel);
    }
}

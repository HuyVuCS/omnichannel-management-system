using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Channels;

/// <param name="StockSyncEnabled">
/// True when stock is pushed to this channel: its slug has a stock-sync queue. A capability of the channel, not its state -
/// an Inactive channel keeps it. A channel without it can have product mappings, but they are never synced.
/// </param>
public sealed record ChannelResponse(
    Guid ChannelId,
    string Name,
    string Slug,
    ChannelType Type,
    ChannelStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool StockSyncEnabled);

public sealed record CreateChannelRequest(string Name, string Slug, ChannelType Type);

/// <summary>Slug and type are immutable: orders and webhooks are routed by slug.</summary>
public sealed record UpdateChannelRequest(string Name, ChannelStatus Status);

public static class ChannelErrorCodes
{
    public const string SlugExists = "channel_slug_exists";
}

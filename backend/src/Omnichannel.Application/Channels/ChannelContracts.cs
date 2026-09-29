using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Channels;

public sealed record ChannelResponse(
    Guid ChannelId,
    string Name,
    string Slug,
    ChannelType Type,
    ChannelStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateChannelRequest(string Name, string Slug, ChannelType Type);

/// <summary>Slug and type are immutable: orders and webhooks are routed by slug.</summary>
public sealed record UpdateChannelRequest(string Name, ChannelStatus Status);

public static class ChannelErrorCodes
{
    public const string SlugExists = "channel_slug_exists";
}

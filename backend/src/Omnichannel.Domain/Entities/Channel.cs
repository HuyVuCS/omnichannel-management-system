using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

public class Channel : AuditableEntity
{
    private Channel()
    {
    }

    public Channel(string name, string slug, ChannelType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        ChannelId = Guid.CreateVersion7();
        Name = name.Trim();
        Slug = slug.Trim().ToLowerInvariant();
        Type = type;
        Status = ChannelStatus.Active;
    }

    public Guid ChannelId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>URL identifier used by POST /api/orders/{slug} and /api/webhooks/{slug}.</summary>
    public string Slug { get; private set; } = null!;

    public ChannelType Type { get; private set; }
    public ChannelStatus Status { get; private set; }
}

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

    /// <summary>Slug and type are immutable: orders and webhooks are routed by slug.</summary>
    public void Update(string name, ChannelStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown channel status.");

        Name = name.Trim();
        Status = status;
    }
}

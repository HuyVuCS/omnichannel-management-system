using Omnichannel.Application.Auth;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence;

/// <summary>
/// Reference data inserted by migrations. IDs and stamps are constants so that
/// re-generating a migration never produces spurious UPDATEs.
/// </summary>
internal static class SeedData
{
    private static readonly DateTimeOffset SeededAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string SeededBy = "system:seed";

    public static class ChannelIds
    {
        public static readonly Guid DigitalPlatform = new("0199a1b0-0000-7000-8000-000000000001");
        public static readonly Guid Website = new("0199a1b0-0000-7000-8000-000000000002");
        public static readonly Guid Pos = new("0199a1b0-0000-7000-8000-000000000003");
    }

    public static object[] Channels =>
    [
        new
        {
            ChannelId = ChannelIds.DigitalPlatform,
            Name = "Digital Platform Online",
            Slug = "digital-platform",
            Type = ChannelType.DigitalPlatform,
            Status = ChannelStatus.Active,
            CreatedAt = SeededAt,
            CreatedBy = SeededBy
        },
        new
        {
            ChannelId = ChannelIds.Website,
            Name = "Website",
            Slug = "website",
            Type = ChannelType.Website,
            Status = ChannelStatus.Active,
            CreatedAt = SeededAt,
            CreatedBy = SeededBy
        },
        new
        {
            ChannelId = ChannelIds.Pos,
            Name = "POS",
            Slug = "pos",
            Type = ChannelType.Pos,
            Status = ChannelStatus.Active,
            CreatedAt = SeededAt,
            CreatedBy = SeededBy
        }
    ];

    public static object[] Roles =>
    [
        Role(new Guid("0199a1b0-0000-7000-8000-000000000101"), AppRoles.Admin),
        Role(new Guid("0199a1b0-0000-7000-8000-000000000102"), AppRoles.Manager),
        Role(new Guid("0199a1b0-0000-7000-8000-000000000103"), AppRoles.Staff)
    ];

    private static object Role(Guid id, string name) => new
    {
        Id = id,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        // Constant stamp: a random one would change on every migration generation.
        ConcurrencyStamp = id.ToString()
    };
}

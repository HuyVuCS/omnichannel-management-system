namespace Omnichannel.Infrastructure.Identity;

/// <summary>Bound from the <c>TokenRevocation</c> configuration section.</summary>
public sealed class TokenRevocationOptions
{
    public const string SectionName = "TokenRevocation";

    public static readonly TimeSpan MaxCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a user's security state (security stamp, lockout) is cached per API instance. It is the upper bound for how
    /// long a revoked access token is still accepted by <b>another</b> instance; zero disables the cache (one query per request).
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}

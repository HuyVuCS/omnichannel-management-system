namespace Omnichannel.Api.Auth;

/// <summary>
/// Claim names used in access tokens (RFC 7519 short names). Inbound claim mapping is disabled,
/// so these are exactly what controllers and policies see.
/// </summary>
public static class JwtClaimNames
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string TokenId = "jti";
    public const string Role = "role";

    /// <summary>The user's Identity security stamp at issue time (token revocation).</summary>
    public const string SecurityStamp = "sstamp";
}

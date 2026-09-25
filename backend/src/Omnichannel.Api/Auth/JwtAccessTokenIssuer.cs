using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Omnichannel.Application.Auth;

namespace Omnichannel.Api.Auth;

public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(Guid userId, string email, IReadOnlyCollection<string> roles)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        // JWT timestamps have second precision; keep the reported expiry identical to the "exp" claim.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
            now.AddMinutes(jwt.AccessTokenLifetimeMinutes).ToUnixTimeSeconds());

        var claims = new List<Claim>
        {
            new(JwtClaimNames.Subject, userId.ToString()),
            new(JwtClaimNames.Email, email),
            new(JwtClaimNames.TokenId, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(roles.Select(role => new Claim(JwtClaimNames.Role, role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt.SigningKey), SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(string signingKey) => new(Encoding.UTF8.GetBytes(signingKey));
}

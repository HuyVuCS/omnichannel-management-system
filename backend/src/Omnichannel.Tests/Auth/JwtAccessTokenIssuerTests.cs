using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Omnichannel.Api.Auth;

namespace Omnichannel.Tests.Auth;

public sealed class JwtAccessTokenIssuerTests
{
    private static JwtOptions CreateOptions(string? signingKey = null) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = signingKey ?? new string('k', 48),
        AccessTokenLifetimeMinutes = 30
    };

    private static TokenValidationParameters ValidationParameters(JwtOptions options) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = JwtAccessTokenIssuer.CreateSigningKey(options.SigningKey),
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
    };

    [Fact]
    public async Task Issued_token_is_valid_and_carries_subject_email_and_roles()
    {
        var options = CreateOptions();
        var issuer = new JwtAccessTokenIssuer(Options.Create(options), TimeProvider.System);
        var userId = Guid.NewGuid();

        var token = issuer.Issue(userId, "staff@example.com", new[] { "Staff", "Manager" }, "stamp-1");
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, ValidationParameters(options));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(userId.ToString(), result.ClaimsIdentity.FindFirst(JwtClaimNames.Subject)?.Value);
        Assert.Equal("stamp-1", result.ClaimsIdentity.FindFirst(JwtClaimNames.SecurityStamp)?.Value);
        Assert.Equal("staff@example.com", result.ClaimsIdentity.FindFirst(JwtClaimNames.Email)?.Value);
        Assert.Equal(
            new[] { "Manager", "Staff" },
            result.ClaimsIdentity.FindAll(JwtClaimNames.Role).Select(c => c.Value).Order().ToArray());
    }

    [Fact]
    public async Task Token_signed_with_a_different_key_is_rejected()
    {
        var issuer = new JwtAccessTokenIssuer(Options.Create(CreateOptions()), TimeProvider.System);
        var token = issuer.Issue(Guid.NewGuid(), "a@example.com", Array.Empty<string>(), "stamp");

        var otherKeyOptions = CreateOptions(new string('x', 48));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, ValidationParameters(otherKeyOptions));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_token_without_a_security_stamp_is_never_issued(string securityStamp)
    {
        var issuer = new JwtAccessTokenIssuer(Options.Create(CreateOptions()), TimeProvider.System);

        Assert.Throws<ArgumentException>(() => issuer.Issue(Guid.NewGuid(), "a@example.com", Array.Empty<string>(), securityStamp));
    }

    [Fact]
    public void ExpiresAt_follows_configured_lifetime()
    {
        var options = CreateOptions();
        var issuer = new JwtAccessTokenIssuer(Options.Create(options), TimeProvider.System);
        var before = DateTimeOffset.UtcNow;

        var token = issuer.Issue(Guid.NewGuid(), "a@example.com", Array.Empty<string>(), "stamp");

        var expected = before.AddMinutes(options.AccessTokenLifetimeMinutes);
        Assert.InRange(token.ExpiresAt, expected.AddSeconds(-2), expected.AddSeconds(5));
    }
}

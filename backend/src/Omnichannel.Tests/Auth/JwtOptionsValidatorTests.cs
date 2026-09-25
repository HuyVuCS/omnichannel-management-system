using Omnichannel.Api.Auth;

namespace Omnichannel.Tests.Auth;

public sealed class JwtOptionsValidatorTests
{
    private readonly JwtOptionsValidator _validator = new();

    [Fact]
    public void Accepts_complete_configuration()
    {
        var result = _validator.Validate(null, Valid());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Rejects_signing_key_shorter_than_256_bits_without_echoing_it()
    {
        var options = Valid();
        options.SigningKey = "too-short-secret";

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.DoesNotContain("too-short-secret", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Rejects_out_of_range_lifetime(int minutes)
    {
        var options = Valid();
        options.AccessTokenLifetimeMinutes = minutes;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    private static JwtOptions Valid() => new()
    {
        Issuer = "issuer",
        Audience = "audience",
        SigningKey = new string('k', JwtOptions.MinimumSigningKeyBytes),
        AccessTokenLifetimeMinutes = 60
    };
}

using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Auth;

namespace Omnichannel.Tests.Auth;

public sealed class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Fact]
    public void Accepts_valid_request()
    {
        Assert.True(_validator.Validate(new LoginRequest("admin@example.com", "Secret123456")).IsValid);
    }

    [Theory]
    [InlineData("", "Secret123456", nameof(LoginRequest.Email))]
    [InlineData("not-an-email", "Secret123456", nameof(LoginRequest.Email))]
    [InlineData("admin@example.com", "", nameof(LoginRequest.Password))]
    public void Rejects_missing_or_malformed_fields(string email, string password, string invalidProperty)
    {
        var result = _validator.Validate(new LoginRequest(email, password));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(invalidProperty));
    }

    [Fact]
    public void Rejects_oversized_password()
    {
        var password = new string('a', LoginRequestValidator.MaxPasswordLength + 1);

        var result = _validator.Validate(new LoginRequest("admin@example.com", password));

        Assert.Contains(result.Errors, e => e.PropertyName == ValidationConventions.JsonName(nameof(LoginRequest.Password)));
    }
}

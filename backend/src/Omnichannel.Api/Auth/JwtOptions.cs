using System.Text;
using Microsoft.Extensions.Options;

namespace Omnichannel.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC-SHA256 requires a key of at least 256 bits.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;

    /// <summary>Secret. Supply via environment variable Jwt__SigningKey or user-secrets, never appsettings.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenLifetimeMinutes { get; set; } = 60;
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Jwt:Audience is required.");
        }

        if (Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty) < JwtOptions.MinimumSigningKeyBytes)
        {
            // Never include the key itself in the message.
            failures.Add($"Jwt:SigningKey must be at least {JwtOptions.MinimumSigningKeyBytes} bytes.");
        }

        if (options.AccessTokenLifetimeMinutes is < 1 or > 1440)
        {
            failures.Add("Jwt:AccessTokenLifetimeMinutes must be between 1 and 1440.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

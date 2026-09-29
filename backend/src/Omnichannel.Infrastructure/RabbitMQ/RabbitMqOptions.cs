using Microsoft.Extensions.Options;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>Bound from the <c>RabbitMq</c> configuration section.</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = string.Empty;

    /// <summary>Secret. Supply via environment variable <c>RabbitMq__Password</c> or user-secrets, never appsettings.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Connection name shown in the management UI.</summary>
    public string ClientProvidedName { get; set; } = "omnichannel";

    /// <summary>Wait between automatic reconnection attempts after the connection was lost.</summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class RabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.HostName))
        {
            failures.Add("RabbitMq:HostName is required.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("RabbitMq:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.VirtualHost))
        {
            failures.Add("RabbitMq:VirtualHost is required.");
        }

        if (string.IsNullOrWhiteSpace(options.UserName))
        {
            failures.Add("RabbitMq:UserName is required.");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            // Never include the value itself.
            failures.Add("RabbitMq:Password is required (environment variable or user-secrets).");
        }

        if (options.NetworkRecoveryInterval <= TimeSpan.Zero)
        {
            failures.Add("RabbitMq:NetworkRecoveryInterval must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

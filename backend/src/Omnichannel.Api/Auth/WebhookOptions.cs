using Microsoft.Extensions.Options;

namespace Omnichannel.Api.Auth;

/// <summary>
/// Bound from the <c>Webhooks</c> configuration section. The secrets come from user-secrets or the environment only -
/// never from an <c>appsettings*.json</c> file - and are never logged.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Shortest secret a channel may sign with (characters).</summary>
    public const int MinSecretLength = 32;

    /// <summary>One secret, or two while a channel changes its secret (both are accepted).</summary>
    public const int MaxSecretsPerChannel = 2;

    /// <summary>How far the timestamp of a webhook may be from the clock of the API, in either direction.</summary>
    public TimeSpan TimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Largest body of a webhook; a longer one is refused with 413 before anything is checked.</summary>
    public int MaxBodyBytes { get; set; } = 65_536;

    /// <summary>Webhooks one client address may send to one channel per minute, whatever their outcome; then 429.</summary>
    public int PermitsPerMinute { get; set; } = 120;

    /// <summary>By channel slug. A channel that is not listed, or has no secret, has no webhook.</summary>
    public Dictionary<string, WebhookChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The secrets of the channel with this slug; empty when it has no webhook.</summary>
    public IReadOnlyList<string> SecretsOf(string channelSlug) =>
        Channels.TryGetValue(channelSlug, out var channel) && channel?.Secrets is { } secrets ? secrets : [];
}

public sealed class WebhookChannelOptions
{
    /// <summary>Each at least <see cref="WebhookOptions.MinSecretLength"/> characters; a webhook signed with any of them is accepted.</summary>
    public List<string> Secrets { get; set; } = [];
}

public sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    public static readonly TimeSpan MinTimestampTolerance = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTimestampTolerance = TimeSpan.FromMinutes(15);

    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        var failures = new List<string>();

        if (options.TimestampTolerance < MinTimestampTolerance || options.TimestampTolerance > MaxTimestampTolerance)
        {
            failures.Add("Webhooks:TimestampTolerance must be between 30 s and 15 min.");
        }

        if (options.MaxBodyBytes < 1)
        {
            failures.Add("Webhooks:MaxBodyBytes must be at least 1.");
        }

        if (options.PermitsPerMinute < 1)
        {
            failures.Add("Webhooks:PermitsPerMinute must be at least 1.");
        }

        foreach (var (slug, channel) in options.Channels)
        {
            var secrets = channel?.Secrets ?? [];

            // The messages name the channel, never a secret or its length.
            if (secrets.Count > WebhookOptions.MaxSecretsPerChannel)
            {
                failures.Add($"Webhooks:Channels:{slug}:Secrets must hold at most {WebhookOptions.MaxSecretsPerChannel} secrets.");
            }

            if (secrets.Any(secret => secret is null || secret.Length < WebhookOptions.MinSecretLength || string.IsNullOrWhiteSpace(secret)))
            {
                failures.Add($"Webhooks:Channels:{slug}:Secrets: every secret must be at least {WebhookOptions.MinSecretLength} characters.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

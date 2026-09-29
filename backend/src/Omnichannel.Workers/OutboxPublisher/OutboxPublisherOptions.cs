using Microsoft.Extensions.Options;

namespace Omnichannel.Workers.OutboxPublisher;

/// <summary>Bound from the <c>OutboxPublisher</c> configuration section.</summary>
public sealed class OutboxPublisherOptions
{
    public const string SectionName = "OutboxPublisher";

    /// <summary>Rows locked and published per transaction.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Wait after a batch that was not full. A full batch is followed immediately by the next one.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Failed attempts after which a row becomes <c>Failed</c> (terminal) and is logged to SystemEventLog.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Backoff after the first failed attempt; doubles per attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum wait for the broker's publisher confirms of one batch.</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class OutboxPublisherOptionsValidator : IValidateOptions<OutboxPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxPublisherOptions options)
    {
        var failures = new List<string>();

        if (options.BatchSize is < 1 or > 1000)
        {
            failures.Add("OutboxPublisher:BatchSize must be between 1 and 1000.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("OutboxPublisher:MaxAttempts must be at least 1.");
        }

        if (options.PollInterval <= TimeSpan.Zero || options.InitialBackoff <= TimeSpan.Zero || options.ConfirmTimeout <= TimeSpan.Zero)
        {
            failures.Add("OutboxPublisher:PollInterval, InitialBackoff and ConfirmTimeout must be positive.");
        }

        if (options.MaxBackoff < options.InitialBackoff)
        {
            failures.Add("OutboxPublisher:MaxBackoff must not be smaller than InitialBackoff.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

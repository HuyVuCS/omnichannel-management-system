using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.ExternalChannels;

namespace Omnichannel.Workers.StockSyncWorker;

/// <summary>Bound from the <c>StockSyncWorker</c> configuration section.</summary>
public sealed class StockSyncWorkerOptions
{
    public const string SectionName = "StockSyncWorker";

    /// <summary>
    /// Messages handled in parallel per channel queue by this instance. Default 1: pushes of one channel are sent one after
    /// the other, so two changes of the same product cannot overtake each other within an instance.
    /// </summary>
    public ushort Concurrency { get; set; } = 1;

    /// <summary>Attempts of a task (from the database) before a temporary failure becomes final (task Failed, DLQ).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Pushes within one attempt: after a push the stock is read again and pushed again while it differs from the pushed
    /// value, up to this number.
    /// </summary>
    public int MaxPushesPerAttempt { get; set; } = 3;

    /// <summary>Longest time one call to a channel may take (<c>channel_timeout</c> afterwards).</summary>
    public TimeSpan ChannelCallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public ChannelCircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>Backoff for (re)starting a consumer while RabbitMQ is unavailable.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class StockSyncWorkerOptionsValidator : IValidateOptions<StockSyncWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, StockSyncWorkerOptions options)
    {
        var failures = new List<string>();

        if (options.Concurrency is < 1 or > 16)
        {
            failures.Add("StockSyncWorker:Concurrency must be between 1 and 16.");
        }

        if (options.MaxAttempts is < 1 or > 20)
        {
            failures.Add("StockSyncWorker:MaxAttempts must be between 1 and 20.");
        }

        if (options.MaxPushesPerAttempt is < 1 or > 10)
        {
            failures.Add("StockSyncWorker:MaxPushesPerAttempt must be between 1 and 10.");
        }

        if (options.ChannelCallTimeout < TimeSpan.FromMilliseconds(100) || options.ChannelCallTimeout > TimeSpan.FromMinutes(5))
        {
            failures.Add("StockSyncWorker:ChannelCallTimeout must be between 100 ms and 5 minutes.");
        }

        // The ranges the circuit breaker accepts; checked here so a wrong value stops the host with a clear message.
        var breaker = options.CircuitBreaker;
        if (breaker is null)
        {
            failures.Add("StockSyncWorker:CircuitBreaker is required.");
        }
        else
        {
            if (breaker.FailureRatio is not (> 0 and <= 1))
            {
                failures.Add("StockSyncWorker:CircuitBreaker:FailureRatio must be greater than 0 and at most 1.");
            }

            if (breaker.MinimumThroughput is < 2 or > 10_000)
            {
                failures.Add("StockSyncWorker:CircuitBreaker:MinimumThroughput must be between 2 and 10000.");
            }

            if (breaker.SamplingDuration < TimeSpan.FromMilliseconds(500) || breaker.SamplingDuration > TimeSpan.FromHours(1))
            {
                failures.Add("StockSyncWorker:CircuitBreaker:SamplingDuration must be between 500 ms and 1 hour.");
            }

            if (breaker.BreakDuration < TimeSpan.FromMilliseconds(500) || breaker.BreakDuration > TimeSpan.FromHours(1))
            {
                failures.Add("StockSyncWorker:CircuitBreaker:BreakDuration must be between 500 ms and 1 hour.");
            }
        }

        if (options.InitialBackoff <= TimeSpan.Zero || options.MaxBackoff < options.InitialBackoff)
        {
            failures.Add("StockSyncWorker:InitialBackoff must be positive and not larger than MaxBackoff.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

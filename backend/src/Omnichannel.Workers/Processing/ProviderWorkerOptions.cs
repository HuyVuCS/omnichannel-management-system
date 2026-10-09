using Omnichannel.Infrastructure.ExternalChannels;

namespace Omnichannel.Workers.Processing;

/// <summary>
/// The settings a worker that calls one provider has (Invoice Worker, Notification Worker): its consumer, its attempt
/// limit, and the timeout and circuit breaker of the calls.
/// </summary>
public abstract class ProviderWorkerOptions
{
    /// <summary>
    /// Messages handled in parallel by this instance (consumer dispatch concurrency and prefetch). Default 1; more
    /// throughput comes from a higher value or more instances - the tasks of different orders are independent.
    /// </summary>
    public ushort Concurrency { get; set; } = 1;

    /// <summary>Attempts of a task (from the database) before a temporary failure becomes final (task Failed, DLQ).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Longest time one call to the provider may take (its timeout code afterwards).</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public ChannelCircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>Backoff for (re)starting the consumer while RabbitMQ is unavailable.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>What is wrong with these settings, each message starting with <paramref name="section"/>; empty when nothing is.</summary>
    public List<string> Validate(string section)
    {
        var failures = new List<string>();

        if (Concurrency is < 1 or > 16)
        {
            failures.Add($"{section}:Concurrency must be between 1 and 16.");
        }

        if (MaxAttempts is < 1 or > 20)
        {
            failures.Add($"{section}:MaxAttempts must be between 1 and 20.");
        }

        if (CallTimeout < TimeSpan.FromMilliseconds(100) || CallTimeout > TimeSpan.FromMinutes(5))
        {
            failures.Add($"{section}:CallTimeout must be between 100 ms and 5 minutes.");
        }

        // The ranges the circuit breaker accepts; checked here so a wrong value stops the host with a clear message.
        if (CircuitBreaker is not { } breaker)
        {
            failures.Add($"{section}:CircuitBreaker is required.");
        }
        else
        {
            if (breaker.FailureRatio is not (> 0 and <= 1))
            {
                failures.Add($"{section}:CircuitBreaker:FailureRatio must be greater than 0 and at most 1.");
            }

            if (breaker.MinimumThroughput is < 2 or > 10_000)
            {
                failures.Add($"{section}:CircuitBreaker:MinimumThroughput must be between 2 and 10000.");
            }

            if (breaker.SamplingDuration < TimeSpan.FromMilliseconds(500) || breaker.SamplingDuration > TimeSpan.FromHours(1))
            {
                failures.Add($"{section}:CircuitBreaker:SamplingDuration must be between 500 ms and 1 hour.");
            }

            if (breaker.BreakDuration < TimeSpan.FromMilliseconds(500) || breaker.BreakDuration > TimeSpan.FromHours(1))
            {
                failures.Add($"{section}:CircuitBreaker:BreakDuration must be between 500 ms and 1 hour.");
            }
        }

        if (InitialBackoff <= TimeSpan.Zero || MaxBackoff < InitialBackoff)
        {
            failures.Add($"{section}:InitialBackoff must be positive and not larger than MaxBackoff.");
        }

        return failures;
    }
}

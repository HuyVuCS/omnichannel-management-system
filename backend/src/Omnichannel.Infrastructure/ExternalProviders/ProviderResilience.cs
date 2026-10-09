using Microsoft.Extensions.Logging;
using Omnichannel.Infrastructure.ExternalChannels;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Omnichannel.Infrastructure.ExternalProviders;

/// <summary>
/// Timeout and circuit breaker of the calls to one provider (the invoice provider, the e-mail sender); a named options
/// instance per provider (<see cref="InvoiceProvider"/>, <see cref="EmailSender"/>), configured by the Workers host.
/// </summary>
public sealed class ProviderResilienceOptions
{
    public const string InvoiceProvider = "invoice-provider";
    public const string EmailSender = "email-sender";

    /// <summary>Longest time one call may take; afterwards it fails with the provider's timeout code.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public ChannelCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>
/// The Polly pipeline of a provider: circuit breaker (outer) and attempt timeout (inner), as for the channels.
/// <para>
/// There is <b>no retry here</b>: a retry is a new attempt of the task, scheduled through the broker's delay tiers and
/// recorded in the attempt history. Retrying inside a call would hide tries from that history.
/// </para>
/// Only transient failures and timeouts count toward the breaker; a rejection says nothing about the provider's health.
/// </summary>
internal static class ProviderResilience
{
    public static ResiliencePipeline CreatePipeline<TException>(
        string provider,
        ProviderResilienceOptions options,
        TimeProvider timeProvider,
        Func<TException, bool> isTransient,
        ILogger logger)
        where TException : Exception
    {
        var breaker = options.CircuitBreaker;
        return new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<TimeoutRejectedException>()
                    .Handle(isTransient),
                FailureRatio = breaker.FailureRatio,
                MinimumThroughput = breaker.MinimumThroughput,
                SamplingDuration = breaker.SamplingDuration,
                BreakDuration = breaker.BreakDuration,
                OnOpened = arguments =>
                {
                    logger.LogWarning(
                        "Circuit breaker of the {Provider} opened for {BreakDuration} after repeated failures",
                        provider, arguments.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Circuit breaker of the {Provider} closed; it is called again", provider);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(options.CallTimeout)
            .Build();
    }
}

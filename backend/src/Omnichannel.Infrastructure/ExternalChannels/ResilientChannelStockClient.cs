using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>Timeout and circuit breaker of the calls to one channel.</summary>
public sealed class ChannelResilienceOptions
{
    /// <summary>Longest time one call to a channel may take; afterwards it fails with <c>channel_timeout</c>.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public ChannelCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>
/// The breaker of a channel opens when, within <see cref="SamplingDuration"/>, at least <see cref="MinimumThroughput"/> calls
/// were made and <see cref="FailureRatio"/> of them failed with a transient error or a timeout. While it is open
/// (<see cref="BreakDuration"/>) the channel is not called and calls fail at once with <c>channel_circuit_open</c>.
/// </summary>
public sealed class ChannelCircuitBreakerOptions
{
    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 5;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Calls the channel adapter through one Polly pipeline per channel slug: circuit breaker (outer) and attempt timeout (inner),
/// so one slow or broken channel neither blocks its worker nor affects the other channels (doc §13).
/// <para>
/// There is <b>no retry here</b>: a retry is a new attempt of the task, scheduled through the broker's delay tiers and
/// recorded in the attempt history. Retrying inside a call would hide tries from that history.
/// </para>
/// Only transient failures and timeouts count toward the breaker; a rejection by the channel says nothing about its health.
/// </summary>
public sealed class ResilientChannelStockClient(
    [FromKeyedServices(ResilientChannelStockClient.AdapterKey)] IChannelStockClient adapter,
    IOptions<ChannelResilienceOptions> options,
    TimeProvider timeProvider,
    ILogger<ResilientChannelStockClient> logger) : IChannelStockClient
{
    /// <summary>Service key of the adapter that talks to the channels (the simulator today, a real adapter later).</summary>
    public const string AdapterKey = "channel-stock-adapter";

    private readonly ConcurrentDictionary<string, ResiliencePipeline> _pipelines = new(StringComparer.Ordinal);
    private readonly ChannelResilienceOptions _options = options.Value;

    public Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken) =>
        ExecuteAsync(
            update.ChannelSlug,
            static async (state, token) =>
            {
                await state.adapter.PushAvailableStockAsync(state.update, token);
                return true;
            },
            (adapter, update),
            cancellationToken);

    /// <summary>
    /// Through the same pipeline as the pushes: a read that fails or times out says the same about the channel's health,
    /// and an open breaker stops reads as well.
    /// </summary>
    public Task<IReadOnlyList<ChannelStockReading>> ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken cancellationToken) =>
        ExecuteAsync(
            query.ChannelSlug,
            static async (state, token) => await state.adapter.ReadAvailableStockAsync(state.query, token),
            (adapter, query),
            cancellationToken);

    private async Task<TResult> ExecuteAsync<TState, TResult>(
        string channelSlug,
        Func<TState, CancellationToken, ValueTask<TResult>> call,
        TState state,
        CancellationToken cancellationToken)
    {
        var pipeline = _pipelines.GetOrAdd(channelSlug, CreatePipeline);
        try
        {
            return await pipeline.ExecuteAsync(call, state, cancellationToken);
        }
        catch (TimeoutRejectedException exception)
        {
            throw new ChannelApiException(
                ChannelApiErrorCodes.Timeout,
                $"Channel '{channelSlug}' did not answer within {_options.CallTimeout}.",
                isTransient: true,
                exception);
        }
        catch (BrokenCircuitException exception)
        {
            throw new ChannelApiException(
                ChannelApiErrorCodes.CircuitOpen,
                $"Channel '{channelSlug}' was not called: its circuit breaker is open after repeated failures.",
                isTransient: true,
                exception);
        }
    }

    private ResiliencePipeline CreatePipeline(string channelSlug)
    {
        var breaker = _options.CircuitBreaker;
        return new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<TimeoutRejectedException>()
                    .Handle<ChannelApiException>(exception => exception.IsTransient),
                FailureRatio = breaker.FailureRatio,
                MinimumThroughput = breaker.MinimumThroughput,
                SamplingDuration = breaker.SamplingDuration,
                BreakDuration = breaker.BreakDuration,
                OnOpened = arguments =>
                {
                    logger.LogWarning(
                        "Circuit breaker of channel {Channel} opened for {BreakDuration} after repeated failures",
                        channelSlug, arguments.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Circuit breaker of channel {Channel} closed; the channel is called again", channelSlug);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(_options.CallTimeout)
            .Build();
    }
}

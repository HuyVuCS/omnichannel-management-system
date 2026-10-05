using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Channels;
using Omnichannel.Infrastructure.ExternalChannels;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Test double for the channel adapter: records every push (also the failing ones) and every read, and lets a test script
/// the channel's answers. It replaces the simulator only; the timeout/circuit-breaker pipeline in front of it stays the
/// production one.
/// <para>
/// Like a channel it remembers the value of every push it accepted, and a read answers from that (null for a product it
/// never got a value for) unless the test scripts the read; <see cref="SetStock"/> puts a value there directly.
/// </para>
/// </summary>
public sealed class RecordingChannelClient : IChannelStockClient
{
    private readonly ConcurrentQueue<ChannelStockUpdate> _pushes = new();
    private readonly ConcurrentQueue<ChannelStockQuery> _reads = new();
    private readonly ConcurrentDictionary<(string Channel, Guid ProductId), int> _stock = new();

    /// <summary>What the channel does with a push; null = accept.</summary>
    public Func<ChannelStockUpdate, CancellationToken, Task>? Behaviour { get; set; }

    /// <summary>What the channel answers to a read; null = the values it holds.</summary>
    public Func<ChannelStockQuery, CancellationToken, Task<IReadOnlyList<ChannelStockReading>>>? ReadBehaviour { get; set; }

    public IReadOnlyList<ChannelStockUpdate> Pushes => _pushes.ToArray();

    public IReadOnlyList<ChannelStockUpdate> PushesOf(Guid productId) => _pushes.Where(p => p.ProductId == productId).ToList();

    public IReadOnlyList<ChannelStockUpdate> PushesOfTask(Guid taskId) => _pushes.Where(p => p.TaskId == taskId).ToList();

    /// <summary>Every read, also the failing ones.</summary>
    public IReadOnlyList<ChannelStockQuery> Reads => _reads.ToArray();

    public IReadOnlyList<ChannelStockQuery> ReadsOf(string channelSlug) => _reads.Where(r => r.ChannelSlug == channelSlug).ToList();

    /// <summary>The channel holds <paramref name="availableStock"/> for the product; null = it holds no value.</summary>
    public void SetStock(string channelSlug, Guid productId, int? availableStock)
    {
        if (availableStock is { } stock)
        {
            _stock[(channelSlug, productId)] = stock;
        }
        else
        {
            _stock.TryRemove((channelSlug, productId), out _);
        }
    }

    public async Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken)
    {
        _pushes.Enqueue(update);
        if (Behaviour is { } behaviour)
        {
            await behaviour(update, cancellationToken);
        }

        _stock[(update.ChannelSlug, update.ProductId)] = update.AvailableStock;
    }

    public async Task<IReadOnlyList<ChannelStockReading>> ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken cancellationToken)
    {
        _reads.Enqueue(query);
        if (ReadBehaviour is { } behaviour)
        {
            return await behaviour(query, cancellationToken);
        }

        return query.Items
            .Select(item => new ChannelStockReading(
                item.ProductId,
                _stock.TryGetValue((query.ChannelSlug, item.ProductId), out var stock) ? stock : null))
            .ToList();
    }
}

internal static class RecordingChannelClientExtensions
{
    /// <summary>Replaces the simulated channel adapter of a Workers host with <paramref name="client"/>.</summary>
    public static IServiceCollection UseChannelAdapter(this IServiceCollection services, IChannelStockClient client)
    {
        var adapter = services.Single(d =>
            d.ServiceType == typeof(IChannelStockClient) && d.IsKeyedService && Equals(d.ServiceKey, ResilientChannelStockClient.AdapterKey));
        services.Remove(adapter);
        services.AddKeyedSingleton(ResilientChannelStockClient.AdapterKey, client);
        return services;
    }
}

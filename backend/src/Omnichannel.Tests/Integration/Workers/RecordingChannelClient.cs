using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Channels;
using Omnichannel.Infrastructure.ExternalChannels;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Test double for the channel adapter: records every push (also the failing ones) and lets a test script the channel's
/// answer. It replaces the simulator only; the timeout/circuit-breaker pipeline in front of it stays the production one.
/// </summary>
public sealed class RecordingChannelClient : IChannelStockClient
{
    private readonly ConcurrentQueue<ChannelStockUpdate> _pushes = new();

    /// <summary>What the channel does with a push; null = accept.</summary>
    public Func<ChannelStockUpdate, CancellationToken, Task>? Behaviour { get; set; }

    public IReadOnlyList<ChannelStockUpdate> Pushes => _pushes.ToArray();

    public IReadOnlyList<ChannelStockUpdate> PushesOf(Guid productId) => _pushes.Where(p => p.ProductId == productId).ToList();

    public IReadOnlyList<ChannelStockUpdate> PushesOfTask(Guid taskId) => _pushes.Where(p => p.TaskId == taskId).ToList();

    public async Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken)
    {
        _pushes.Enqueue(update);
        if (Behaviour is { } behaviour)
        {
            await behaviour(update, cancellationToken);
        }
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

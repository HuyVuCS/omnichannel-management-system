using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Channels;

namespace Omnichannel.Infrastructure.ExternalChannels;

public static class ExternalChannelsServiceCollectionExtensions
{
    /// <summary>
    /// The channel client of the Workers host: the simulated adapter behind the per-channel timeout and circuit breaker.
    /// The API does not call this: it never talks to a channel. The host configures <see cref="ChannelResilienceOptions"/>.
    /// </summary>
    public static IServiceCollection AddSimulatedChannelStockClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChannelSimulationOptions>(configuration.GetSection(ChannelSimulationOptions.SectionName));
        // The simulated channels' own state (a PostgreSQL table); only the simulator knows it.
        services.AddSingleton<ISimulatedChannelStockStore, SimulatedChannelStockStore>();
        services.AddKeyedSingleton<IChannelStockClient, SimulatedChannelStockClient>(ResilientChannelStockClient.AdapterKey);
        services.AddSingleton<IChannelStockClient, ResilientChannelStockClient>();

        return services;
    }
}

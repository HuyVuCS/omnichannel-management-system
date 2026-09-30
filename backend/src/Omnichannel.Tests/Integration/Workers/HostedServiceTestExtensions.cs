using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Omnichannel.Tests.Integration.Workers;

internal static class HostedServiceTestExtensions
{
    /// <summary>Removes one background worker from a Workers host so a test can exercise the others in isolation.</summary>
    public static IServiceCollection RemoveHostedService<T>(this IServiceCollection services)
        where T : IHostedService
    {
        services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T)));
        return services;
    }
}

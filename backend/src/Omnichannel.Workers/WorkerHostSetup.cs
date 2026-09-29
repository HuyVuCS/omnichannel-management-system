using Microsoft.Extensions.Options;
using Omnichannel.Application;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Infrastructure;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OutboxPublisher;

namespace Omnichannel.Workers;

/// <summary>Service registrations of the Workers host (shared by <c>Program</c> and the integration tests).</summary>
public static class WorkerHostSetup
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApplication();
        services.AddInfrastructure();
        services.AddSingleton<ICurrentUser, WorkerCurrentUser>();
        services.AddScoped<WorkerCorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<WorkerCorrelationContext>());

        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));

        services.AddRabbitMq();
        services.AddOptions<OutboxPublisherOptions>()
            .Bind(configuration.GetSection(OutboxPublisherOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OutboxPublisherOptions>, OutboxPublisherOptionsValidator>();

        // Order matters: the topology is declared before any publisher/consumer starts.
        services.AddHostedService<RabbitMqTopologyInitializer>();
        services.AddHostedService<OutboxPublisherService>();

        return services;
    }
}

using Microsoft.Extensions.Options;
using Omnichannel.Application;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Infrastructure;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.OutboxPublisher;

namespace Omnichannel.Workers;

/// <summary>Service registrations of the Workers host (shared by <c>Program</c> and the integration tests).</summary>
public static class WorkerHostSetup
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApplication();
        services.AddInfrastructure();
        services.AddScoped<WorkerCurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<WorkerCurrentUser>());
        services.AddScoped<WorkerCorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<WorkerCorrelationContext>());

        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));

        services.AddRabbitMq();
        services.AddOptions<OutboxPublisherOptions>()
            .Bind(configuration.GetSection(OutboxPublisherOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OutboxPublisherOptions>, OutboxPublisherOptionsValidator>();

        services.AddOptions<OrderWorkerOptions>()
            .Bind(configuration.GetSection(OrderWorkerOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OrderWorkerOptions>, OrderWorkerOptionsValidator>();
        services.AddScoped<OrderMessageHandler>();

        // Order matters: the topology is declared before any publisher/consumer starts.
        services.AddHostedService<RabbitMqTopologyInitializer>();
        services.AddHostedService<OutboxPublisherService>();
        services.AddHostedService<OrderWorkerService>();

        return services;
    }
}

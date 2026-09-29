using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Omnichannel.Infrastructure.RabbitMQ;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>RabbitMQ connection for the Workers host. The API does not call this: order intake never talks to the broker.</summary>
    public static IServiceCollection AddRabbitMq(this IServiceCollection services)
    {
        services.AddOptions<RabbitMqOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(RabbitMqOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();
        services.AddSingleton<RabbitMqConnectionProvider>();

        return services;
    }
}

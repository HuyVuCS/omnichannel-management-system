using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Infrastructure.S3;

public static class ObjectStorageServiceCollectionExtensions
{
    /// <summary>
    /// The S3-compatible object storage, for the API host (call it after <c>AddInfrastructure</c>). The options are
    /// validated at start-up - presence and ranges only, nothing connects. The Workers host does not call this.
    /// </summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services)
    {
        services.AddOptions<ObjectStorageOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(ObjectStorageOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();
        services.Replace(ServiceDescriptor.Singleton<IObjectStorage, S3ObjectStorage>());

        return services;
    }
}

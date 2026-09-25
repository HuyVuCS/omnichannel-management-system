using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Omnichannel.Api.Auth;

public static class RateLimitPolicies
{
    public const string Login = "login";

    private const int LoginPermitsPerWindow = 10;
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);

    /// <remarks>
    /// Partitioned by client IP. Behind a reverse proxy/load balancer, configure
    /// ForwardedHeaders first, otherwise every client shares the proxy's IP.
    /// </remarks>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Login, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = LoginPermitsPerWindow,
                        Window = LoginWindow,
                        QueueLimit = 0
                    }));
        });

        return services;
    }
}

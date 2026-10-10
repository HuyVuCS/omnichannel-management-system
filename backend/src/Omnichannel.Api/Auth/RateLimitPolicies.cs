using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Omnichannel.Api.Auth;

public static class RateLimitPolicies
{
    public const string Login = "login";

    /// <summary><c>POST /api/webhooks/{slug}</c>: a fixed window per client address and channel (<c>Webhooks:PermitsPerMinute</c>).</summary>
    public const string Webhook = "webhook";

    private const int LoginPermitsPerWindow = 10;
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WebhookWindow = TimeSpan.FromMinutes(1);

    /// <remarks>
    /// Partitioned by client IP. Behind a reverse proxy the proxy's address must be configured
    /// (<c>ReverseProxy:KnownProxies</c>, see <see cref="Middleware.ReverseProxySetup"/>), otherwise every client
    /// shares the proxy's IP.
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

            // The limit comes first on a webhook: before its body is read and before anything is looked up. Per address
            // and channel, so one sender cannot use up the webhooks of every channel. Only a slug that has a webhook gets
            // a window of its own - any other text shares one per address, so made-up slugs multiply neither the limit
            // nor the limiters kept in memory.
            options.AddPolicy(Webhook, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<WebhookOptions>>().Value;
                var slug = WebhookCaller.NormaliseSlug(httpContext.GetRouteValue(WebhookRejection.SlugRouteValue) as string);
                var channel = settings.SecretsOf(slug).Count > 0 ? slug : "-";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{channel}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitsPerMinute,
                        Window = WebhookWindow,
                        QueueLimit = 0
                    });
            });

            // A refused webhook is answered and logged like its other refusals; every other policy keeps the bare 429.
            options.OnRejected = async (context, cancellationToken) =>
            {
                var httpContext = context.HttpContext;
                if (httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName != Webhook)
                {
                    return;
                }

                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebhookRejection));
                WebhookRejection.Log(logger, httpContext, StatusCodes.Status429TooManyRequests, WebhookErrorCodes.RateLimited);
                await WebhookRejection.WriteAsync(
                    httpContext, StatusCodes.Status429TooManyRequests, WebhookErrorCodes.RateLimited, cancellationToken);
            };
        });

        return services;
    }
}

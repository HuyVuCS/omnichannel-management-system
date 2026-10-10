using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Omnichannel.Api.Middleware;

/// <summary>
/// Bound from the <c>ReverseProxy</c> configuration section: which addresses may tell this host who the client is and
/// whether the client used HTTPS (<c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>). Nothing is trusted unless it is
/// listed here - not even the loopback address.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>Addresses of the reverse proxy in front of this host, e.g. <c>172.30.250.2</c>.</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Networks the reverse proxy is in, in CIDR notation, e.g. <c>172.30.250.0/29</c>.</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>
    /// A request that reached the edge without TLS is redirected to HTTPS when this host knows its HTTPS port
    /// (<c>ASPNETCORE_HTTPS_PORT</c>). <c>false</c> is for a run without TLS anywhere (a local production-like stack).
    /// </summary>
    public bool RedirectToHttps { get; set; } = true;
}

public sealed class ReverseProxyOptionsValidator : IValidateOptions<ReverseProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
    {
        var failures = new List<string>();
        // Blank entries are skipped (an environment variable that is passed on empty); anything else must parse.
        failures.AddRange(ReverseProxySetup.Entries(options.KnownProxies)
            .Where(value => !IPAddress.TryParse(value, out _))
            .Select(_ => "ReverseProxy:KnownProxies must contain IP addresses only."));
        failures.AddRange(ReverseProxySetup.Entries(options.KnownNetworks)
            .Where(value => !System.Net.IPNetwork.TryParse(value, out _))
            .Select(_ => "ReverseProxy:KnownNetworks must contain networks in CIDR notation only (e.g. 172.30.250.0/29)."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures.Distinct());
    }
}

/// <summary>
/// This host behind a TLS-terminating reverse proxy: the client address (rate limits, request log) and the scheme
/// (HTTPS redirection, HSTS) of a request are the ones the proxy reports - from the configured proxy only, and only its
/// own entry (<c>ForwardLimit</c> 1: an <c>X-Forwarded-For</c> a client sent itself is never believed).
/// </summary>
public static class ReverseProxySetup
{
    /// <summary>Container health checks ask this path from inside the network, without TLS: it is never redirected.</summary>
    public const string HealthPath = "/health";

    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReverseProxyOptions>()
            .Bind(configuration.GetSection(ReverseProxyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReverseProxyOptions>, ReverseProxyOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ReverseProxyOptions>>((forwarded, proxy) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = 1;
                // The framework trusts loopback by default; here only what is configured counts.
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var address in Entries(proxy.Value.KnownProxies))
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(address));
                }

                foreach (var network in Entries(proxy.Value.KnownNetworks))
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }

                // The framework reads two empty lists as "believe everyone". Nothing configured means no proxy: the
                // headers are then not read at all.
                if (forwarded.KnownProxies.Count == 0 && forwarded.KnownIPNetworks.Count == 0)
                {
                    forwarded.ForwardedHeaders = ForwardedHeaders.None;
                }
            });

        return services;
    }

    /// <summary>First in the pipeline: everything after it sees the client's address and scheme.</summary>
    public static IApplicationBuilder UseReverseProxySupport(this IApplicationBuilder app) => app.UseForwardedHeaders();

    /// <summary>
    /// HTTPS redirection unless it is switched off (<see cref="ReverseProxyOptions.RedirectToHttps"/>). A request the
    /// proxy reports as HTTPS is never redirected - that is what prevents a redirect loop behind a proxy that ends TLS.
    /// </summary>
    public static IApplicationBuilder UseHttpsRedirectionBehindProxy(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetRequiredService<IOptions<ReverseProxyOptions>>().Value.RedirectToHttps)
        {
            app.UseWhen(
                context => !context.Request.Path.StartsWithSegments(HealthPath),
                branch => branch.UseHttpsRedirection());
        }

        return app;
    }

    internal static IEnumerable<string> Entries(IEnumerable<string>? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim());
}

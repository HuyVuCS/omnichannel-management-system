using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Omnichannel.Api.Middleware;

namespace Omnichannel.Tests.Middleware;

/// <summary>
/// Task 025 C1: the API behind a TLS-terminating reverse proxy. A small host with the API's own registrations
/// (<see cref="ReverseProxySetup"/>) and the HTTPS port a deployment configures; the test chooses the address a request
/// arrives from (TestServer has none of its own).
/// </summary>
public sealed class ReverseProxyTests
{
    private const string Proxy = "172.30.250.2";
    private const string Client = "203.0.113.7";
    private const string RemoteIpHeader = "X-Test-Remote-Ip";

    private static async Task<WebApplication> StartAsync(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            settings.Prepend((Key: "https_port", Value: "443")).ToDictionary(setting => setting.Key, setting => setting.Value));
        builder.Services.AddReverseProxySupport(builder.Configuration);

        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value) && IPAddress.TryParse(value, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return next(context);
        });
        app.UseReverseProxySupport();
        app.UseHttpsRedirectionBehindProxy();
        app.MapGet("/who", (HttpContext context) => $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}");
        app.MapGet(ReverseProxySetup.HealthPath, () => "healthy");
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> GetAsync(
        WebApplication app, string path, string? from, string? forwardedFor = null, string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (from is not null)
        {
            request.Headers.Add(RemoteIpHeader, from);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return app.GetTestClient().SendAsync(request);
    }

    [Fact]
    public async Task A_request_the_proxy_reports_as_https_has_the_client_s_address_and_scheme_and_is_not_redirected()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy));

        var response = await GetAsync(app, "/who", from: Proxy, forwardedFor: Client, forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{Client}|https", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_that_reached_the_edge_without_tls_is_redirected_to_https_by_default()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy));

        var response = await GetAsync(app, "/who?x=1", from: Proxy, forwardedFor: Client, forwardedProto: "http");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost/who?x=1"), response.Headers.Location);
    }

    [Fact]
    public async Task With_the_redirect_switched_off_a_plain_http_request_is_served()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy), ("ReverseProxy:RedirectToHttps", "false"));

        var response = await GetAsync(app, "/who", from: Proxy, forwardedFor: Client, forwardedProto: "http");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{Client}|http", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("10.9.8.7")]
    // Not even this host's own loopback address is believed unless it is configured.
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Headers_of_an_address_that_is_not_the_proxy_are_ignored(string from)
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy), ("ReverseProxy:RedirectToHttps", "false"));

        var response = await GetAsync(app, "/who", from, forwardedFor: Client, forwardedProto: "https");

        Assert.Equal($"{IPAddress.Parse(from)}|http", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nothing_configured_trusts_nobody()
    {
        await using var app = await StartAsync(("ReverseProxy:RedirectToHttps", "false"));

        var response = await GetAsync(app, "/who", from: Proxy, forwardedFor: Client, forwardedProto: "https");

        Assert.Equal($"{Proxy}|http", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_proxy_s_own_entry_counts_an_address_the_client_sent_itself_is_not_believed()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy));

        // The client wrote 198.51.100.1 into the header; the proxy appended the address it saw.
        var response = await GetAsync(app, "/who", from: Proxy, forwardedFor: $"198.51.100.1, {Client}", forwardedProto: "https");

        Assert.Equal($"{Client}|https", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_proxy_inside_a_configured_network_is_trusted_one_outside_is_not()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownNetworks:0", "172.30.250.0/29"), ("ReverseProxy:RedirectToHttps", "false"));

        var inside = await GetAsync(app, "/who", from: "172.30.250.6", forwardedFor: Client, forwardedProto: "https");
        var outside = await GetAsync(app, "/who", from: "172.30.250.9", forwardedFor: Client, forwardedProto: "https");

        Assert.Equal($"{Client}|https", await inside.Content.ReadAsStringAsync());
        Assert.Equal("172.30.250.9|http", await outside.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_health_check_from_inside_the_network_is_never_redirected()
    {
        await using var app = await StartAsync(("ReverseProxy:KnownProxies:0", Proxy));

        var response = await GetAsync(app, ReverseProxySetup.HealthPath, from: "127.0.0.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Every other path of the same plain request is.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await GetAsync(app, "/who", from: "127.0.0.1")).StatusCode);
    }

    [Fact]
    public async Task Blank_entries_are_skipped()
    {
        // What a compose file passes on when a variable is empty.
        await using var app = await StartAsync(
            ("ReverseProxy:KnownProxies:0", ""), ("ReverseProxy:KnownProxies:1", $" {Proxy} "), ("ReverseProxy:KnownNetworks:0", " "));

        var response = await GetAsync(app, "/who", from: Proxy, forwardedFor: Client, forwardedProto: "https");

        Assert.Equal($"{Client}|https", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ReverseProxy:KnownProxies:0", "proxy", "ReverseProxy:KnownProxies")]
    [InlineData("ReverseProxy:KnownProxies:0", "172.30.250.0/29", "ReverseProxy:KnownProxies")]
    [InlineData("ReverseProxy:KnownNetworks:0", "172.30.250.2", "ReverseProxy:KnownNetworks")]
    [InlineData("ReverseProxy:KnownNetworks:0", "everything", "ReverseProxy:KnownNetworks")]
    public async Task A_value_that_is_no_address_or_network_stops_the_host_and_is_not_repeated(string key, string value, string named)
    {
        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync((key, value)));

        var message = Assert.Single(failure.Failures);
        Assert.StartsWith(named, message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_defaults_trust_nothing_and_redirect()
    {
        var options = new ReverseProxyOptions();

        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownNetworks);
        Assert.True(options.RedirectToHttps);
        Assert.True(new ReverseProxyOptionsValidator().Validate(null, options).Succeeded);
    }
}

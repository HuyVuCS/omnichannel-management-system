using System.Net;
using Serilog.Events;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 025 C1 against the API host: what depends on the client's address - the webhook rate limit and the address in
/// the log - uses the one the configured proxy reports, and the frontend origin is the only one with CORS headers.
/// The host of the tests trusts <see cref="ApiFactory.ProxyAddress"/> and allows <see cref="ApiFactory.FrontendOrigin"/>.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReverseProxyEndpointsTests(ApiFactory factory)
{
    private const string Website = "website";

    /// <summary>A webhook without a signature (401, counted by the rate limit), as it arrives through the proxy for a client.</summary>
    private static HttpRequestMessage Unsigned(string arrivesFrom, string? forwardedFor)
    {
        var request = WebhookTesting.Request(Website, "{}"u8.ToArray(), secret: null);
        request.Headers.Add(ApiFactory.RemoteIpHeader, arrivesFrom);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return request;
    }

    private static string? ClientIpOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("ClientIp", out var value) && value is ScalarValue { Value: string text } ? text : null;

    [Fact]
    public async Task The_webhook_rate_limit_counts_per_client_behind_the_proxy_not_per_proxy()
    {
        var client = factory.CreateClient();
        const string first = "203.0.113.41";
        const string second = "203.0.113.42";

        for (var n = 0; n < ApiFactory.WebhookPermitsPerMinute; n++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Unsigned(ApiFactory.ProxyAddress, first))).StatusCode);
        }

        // The first client used up its window; another client behind the same proxy has its own.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Unsigned(ApiFactory.ProxyAddress, first))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Unsigned(ApiFactory.ProxyAddress, second))).StatusCode);
    }

    [Fact]
    public async Task The_log_of_a_refused_webhook_names_the_client_behind_the_proxy()
    {
        var client = factory.CreateClient();
        var since = DateTimeOffset.Now;
        const string behindProxy = "203.0.113.51";

        await client.SendAsync(Unsigned(ApiFactory.ProxyAddress, behindProxy));

        var refusal = Assert.Single(
            factory.Logs.EventsSince(since),
            e => e.MessageTemplate.Text.StartsWith("Webhook refused", StringComparison.Ordinal) && ClientIpOf(e) == behindProxy);
        Assert.Equal(LogEventLevel.Warning, refusal.Level);
    }

    [Fact]
    public async Task A_forwarded_address_from_anyone_but_the_proxy_is_ignored()
    {
        var client = factory.CreateClient();
        var since = DateTimeOffset.Now;
        const string direct = "198.51.100.61";
        const string claimed = "203.0.113.61";

        // A caller that reaches the API directly and claims to be someone else (to get a fresh rate-limit window).
        await client.SendAsync(Unsigned(direct, claimed));

        var addresses = factory.Logs.EventsSince(since)
            .Where(e => e.MessageTemplate.Text.StartsWith("Webhook refused", StringComparison.Ordinal))
            .Select(ClientIpOf)
            .ToList();
        Assert.Contains(direct, addresses);
        Assert.DoesNotContain(claimed, addresses);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    public async Task A_preflight_from_the_frontend_origin_is_allowed(string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/products");
        request.Headers.Add("Origin", ApiFactory.FrontendOrigin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-correlation-id");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ApiFactory.FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(method, string.Join(',', response.Headers.GetValues("Access-Control-Allow-Methods")));
        var allowed = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-correlation-id", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://other.integration.test")]
    // An origin is compared as text: another scheme or port is another origin.
    [InlineData("http://frontend.integration.test")]
    [InlineData("https://frontend.integration.test:8443")]
    public async Task A_preflight_from_another_origin_gets_no_cors_headers(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/products");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_answer_to_the_frontend_origin_exposes_the_correlation_id_and_no_other_origin_gets_one()
    {
        using var fromFrontend = new HttpRequestMessage(HttpMethod.Get, "/health");
        fromFrontend.Headers.Add("Origin", ApiFactory.FrontendOrigin);
        using var fromElsewhere = new HttpRequestMessage(HttpMethod.Get, "/health");
        fromElsewhere.Headers.Add("Origin", "https://other.integration.test");

        var allowed = await factory.CreateClient().SendAsync(fromFrontend);
        var refused = await factory.CreateClient().SendAsync(fromElsewhere);

        Assert.Equal(ApiFactory.FrontendOrigin, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("X-Correlation-Id", string.Join(',', allowed.Headers.GetValues("Access-Control-Expose-Headers")));
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

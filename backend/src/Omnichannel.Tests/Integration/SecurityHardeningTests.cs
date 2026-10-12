using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Omnichannel.Api.Hubs;
using Omnichannel.Api.Middleware;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Reports;
using Serilog.Events;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 026 Part B against the API host: the security headers of every answer (B1), the request body limit (B2), what a
/// body the JSON parser cannot read is answered with (B3) and the request path in the log (B4).
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SecurityHardeningTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    // ---- B1 ------------------------------------------------------------------------------------------------------

    private static void AssertSecurityHeaders(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(
            "default-src 'none'; frame-ancestors 'none'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore, $"Cache-Control: {response.Headers.CacheControl}");
        Assert.False(response.Headers.Contains("Cross-Origin-Resource-Policy"));
    }

    [Fact]
    public async Task Every_kind_of_answer_carries_the_security_headers()
    {
        var anonymous = factory.CreateAnonymousClient();

        AssertSecurityHeaders(await _manager.GetAsync("/api/products"), HttpStatusCode.OK);
        AssertSecurityHeaders(await anonymous.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        AssertSecurityHeaders(await _manager.GetAsync("/api/no-such-endpoint"), HttpStatusCode.NotFound);
        AssertSecurityHeaders(await anonymous.GetAsync("/health"), HttpStatusCode.OK);
        AssertSecurityHeaders(
            await _manager.PostAsync($"{NotificationsHub.Path}/negotiate?negotiateVersion=1", null), HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_answer_of_a_login_that_is_rate_limited_carries_them_and_so_do_the_ones_before()
    {
        var client = factory.CreateAnonymousClient();
        var login = new LoginRequest($"nobody-{TestData.UniqueSuffix()}@integration.test", "Wrong-password-026");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            AssertSecurityHeaders(await client.PostAsJsonAsync("/api/auth/login", login), HttpStatusCode.Unauthorized);
        }

        AssertSecurityHeaders(await client.PostAsJsonAsync("/api/auth/login", login), HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_answer_of_a_request_that_failed_unexpectedly_carries_them()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reports/inventory/export")
        {
            Content = JsonContent.Create(new ExportInventoryRequest(null, false, "UTC"), options: TestData.Json)
        };
        factory.CommitFailures.FailNextCommit();

        var response = await _manager.SendAsync(request);

        AssertSecurityHeaders(response, HttpStatusCode.InternalServerError);
        Assert.Equal("internal_error", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task The_frontend_origin_still_reads_the_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add("Origin", ApiFactory.FrontendOrigin);

        var response = await _manager.SendAsync(request);

        AssertSecurityHeaders(response, HttpStatusCode.OK);
        Assert.Equal(ApiFactory.FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(CorrelationIdMiddleware.HeaderName, response.Headers.GetValues("Access-Control-Expose-Headers"));
    }

    // ---- B2 ------------------------------------------------------------------------------------------------------

    private static ByteArrayContent JsonOfLength(long length)
    {
        // Valid JSON of any length: an object, then spaces.
        var body = Enumerable.Repeat((byte)' ', (int)length).ToArray();
        "{}"u8.CopyTo(body);
        return new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
    }

    private static async Task AssertTooLargeAsync(HttpResponseMessage response, string correlationId)
    {
        AssertSecurityHeaders(response, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RequestBodyLimit.ErrorCode, problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(413, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(correlationId, problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));
    }

    [Fact]
    public async Task A_json_body_above_the_limit_is_answered_with_413_problem_details()
    {
        var correlationId = $"limit-{TestData.UniqueSuffix()}";
        using var above = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/import") { Content = JsonOfLength(RequestBodyLimit.MaxBytes + 1) };
        above.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        await AssertTooLargeAsync(await _manager.SendAsync(above), correlationId);

        // Exactly the limit is read and judged like any other body (an empty object: 400 from the validator).
        using var atLimit = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/import") { Content = JsonOfLength(RequestBodyLimit.MaxBytes) };
        var read = await _manager.SendAsync(atLimit);
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal("validation_failed", await read.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task A_webhook_above_the_limit_of_the_application_is_answered_with_the_same_413()
    {
        var correlationId = $"limit-{TestData.UniqueSuffix()}";
        var body = Enumerable.Repeat((byte)' ', (int)RequestBodyLimit.MaxBytes + 1).ToArray();

        var response = await factory.CreateAnonymousClient().SendAsync(
            WebhookTesting.Request("website", body, ApiFactory.WebsiteWebhookSecret, correlationId: correlationId));

        // Refused before the webhook's own steps (its limit of 64 KiB answers webhook_body_too_large).
        await AssertTooLargeAsync(response, correlationId);
    }

    // ---- B3 ------------------------------------------------------------------------------------------------------

    private async Task<(string Raw, Dictionary<string, string[]> Errors)> PostRawAsync(string path, string json, string? language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (language is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
        }

        var response = await _manager.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(raw);
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("errorCode").GetString());
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(m => m.GetString()!).ToArray());

        // Nothing of the parser or of .NET in any language.
        foreach (var leak in new[] { "System.", "Omnichannel.", "Int32", "Guid", "JSON value", "Path:", "LineNumber", "BytePosition", "$." })
        {
            Assert.DoesNotContain(leak, raw, StringComparison.Ordinal);
        }

        return (raw, errors);
    }

    private static string Import(string items) => $$"""{ "documentNumber": "GRN-026", "items": {{items}} }""";

    private static string Line(string stock) => $$"""[{ "productId": "{{Guid.NewGuid()}}", "stock": {{stock}} }]""";

    public static TheoryData<string, string, string, string, string> UnreadableBodies() => new()
    {
        // path, body, expected key, English, Vietnamese
        { "/api/inventory/import", Import(Line("\"abc\"")), "items[0].stock", "'Quantity' has a value of the wrong type or format.", "'Số lượng' có giá trị không đúng kiểu hoặc định dạng." },
        // Out of the range of int.
        { "/api/inventory/import", Import(Line("99999999999")), "items[0].stock", "'Quantity' has a value of the wrong type or format.", "'Số lượng' có giá trị không đúng kiểu hoặc định dạng." },
        // Whatever casing the client used for the member names.
        { "/api/inventory/import", """{ "DocumentNumber": "GRN-026", "Items": [{ "ProductId": "not-a-guid", "Stock": 1 }] }""", "items[0].productId", "'Product' has a value of the wrong type or format.", "'Sản phẩm' có giá trị không đúng kiểu hoặc định dạng." },
        { "/api/inventory/import", Import("{}"), "items", "'Lines' has a value of the wrong type or format.", "'Các dòng hàng' có giá trị không đúng kiểu hoặc định dạng." },
        // An enum value that does not exist.
        { $"/api/orders/{Guid.NewGuid()}/payment", """{ "paymentMethod": "Barter" }""", "paymentMethod", "'Payment method' has a value of the wrong type or format.", "'Phương thức thanh toán' có giá trị không đúng kiểu hoặc định dạng." },
        // Not JSON at all, and JSON that breaks off.
        { "/api/inventory/import", "this is not json", "$", "The request body is not valid JSON.", "Nội dung yêu cầu không phải JSON hợp lệ." },
        { "/api/inventory/import", "[]", "$", "The request body is not valid JSON.", "Nội dung yêu cầu không phải JSON hợp lệ." },
    };

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task A_body_the_parser_cannot_read_is_answered_per_field_in_the_language_of_the_request(
        string path, string body, string expectedKey, string english, string vietnamese)
    {
        var (_, en) = await PostRawAsync(path, body, "en");
        var (_, vi) = await PostRawAsync(path, body, "vi");
        var (_, none) = await PostRawAsync(path, body, null);

        Assert.Equal([expectedKey], en.Keys);
        Assert.Equal([english], en[expectedKey]);
        Assert.Equal([expectedKey], vi.Keys);
        Assert.Equal([vietnamese], vi[expectedKey]);
        Assert.Equal([english], none[expectedKey]);
    }

    // ---- B4 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_request_log_holds_the_path_as_it_was_sent_so_it_cannot_start_a_line_of_its_own()
    {
        var marker = TestData.UniqueSuffix();
        var since = DateTimeOffset.Now;

        // Decoded, this path is two lines - the second one made up to look like a log line.
        var response = await _manager.GetAsync($"/api/forged-{marker}%0A%5B12:00:00%20INF%5D%20Omnichannel:%20all%20is%20well%0D");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var paths = factory.Logs.EventsSince(since)
            .Where(e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RequestPath}", StringComparison.Ordinal))
            .Select(e => e.Properties["RequestPath"] is ScalarValue { Value: string path } ? path : string.Empty)
            .Where(path => path.Contains(marker, StringComparison.Ordinal))
            .ToList();
        var logged = Assert.Single(paths);
        Assert.DoesNotContain(logged, char.IsControl);
        Assert.Contains("%0A", logged, StringComparison.Ordinal);
        Assert.Contains("%0D", logged, StringComparison.Ordinal);
        // Nothing else this application writes about the request carries the decoded path. (The framework's own
        // "Request starting / finished" lines do; this host switches them on, no configuration of the API does.)
        Assert.DoesNotContain(
            factory.Logs.EventsSince(since),
            e => !SourceOf(e).StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal)
                && e.RenderMessage().Contains(marker, StringComparison.Ordinal)
                && e.RenderMessage().Any(char.IsControl));
    }

    private static string SourceOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string source } ? source : string.Empty;

    [Fact]
    public void The_server_cuts_off_a_body_without_an_announced_length_at_the_same_limit()
    {
        var kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Equal(RequestBodyLimit.MaxBytes, kestrel.Limits.MaxRequestBodySize);
    }
}

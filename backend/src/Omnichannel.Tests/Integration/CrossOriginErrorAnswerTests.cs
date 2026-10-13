using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Omnichannel.Api.Middleware;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Reports;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 026b A1: the frontend is on another origin than the API, and a browser hides an answer without
/// <c>Access-Control-Allow-Origin</c> from the page. Every error answer - whatever part of the pipeline wrote it - is
/// readable from the configured frontend origin and carries the correlation id in the header the page may read and in
/// its body.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CrossOriginErrorAnswerTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    private static HttpRequestMessage FromTheFrontend(HttpMethod method, string path, string correlationId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Origin", ApiFactory.FrontendOrigin);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        return request;
    }

    private static async Task AssertReadableAsync(HttpResponseMessage response, HttpStatusCode expected, string correlationId, string? errorCode)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(ApiFactory.FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(CorrelationIdMiddleware.HeaderName, response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(correlationId, problem.RootElement.GetProperty("correlationId").GetString());
        if (errorCode is not null)
        {
            Assert.Equal(errorCode, problem.RootElement.GetProperty("errorCode").GetString());
        }
    }

    private static byte[] JsonOfLength(long length)
    {
        // Valid JSON of any length: an object, then spaces.
        var body = Enumerable.Repeat((byte)' ', (int)length).ToArray();
        "{}"u8.CopyTo(body);
        return body;
    }

    [Fact]
    public async Task A_413_for_an_announced_length_is_readable_from_the_frontend_origin()
    {
        var correlationId = $"cors-413-{TestData.UniqueSuffix()}";
        var content = new ByteArrayContent(JsonOfLength(RequestBodyLimit.MaxBytes + 1))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };

        var response = await _manager.SendAsync(FromTheFrontend(HttpMethod.Post, "/api/inventory/import", correlationId, content));

        await AssertReadableAsync(response, HttpStatusCode.RequestEntityTooLarge, correlationId, RequestBodyLimit.ErrorCode);
    }

    [Fact]
    public async Task A_413_for_a_body_without_a_length_cut_off_by_the_server_is_readable_from_the_frontend_origin()
    {
        var correlationId = $"cors-413c-{TestData.UniqueSuffix()}";
        // The default test client buffers a body (and so announces its length); this one does not.
        var streaming = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        streaming.DefaultRequestHeaders.Authorization = _manager.DefaultRequestHeaders.Authorization;
        var content = new WebhookTesting.UnknownLengthContent(JsonOfLength(RequestBodyLimit.MaxBytes + 1))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        };

        var response = await streaming.SendAsync(FromTheFrontend(HttpMethod.Post, "/api/inventory/import", correlationId, content));

        await AssertReadableAsync(response, HttpStatusCode.RequestEntityTooLarge, correlationId, RequestBodyLimit.ErrorCode);
    }

    [Fact]
    public async Task A_500_is_readable_from_the_frontend_origin_and_keeps_the_correlation_id_header()
    {
        var correlationId = $"cors-500-{TestData.UniqueSuffix()}";
        var content = JsonContent.Create(new ExportInventoryRequest(null, false, "UTC"), options: TestData.Json);
        factory.CommitFailures.FailNextCommit();

        var response = await _manager.SendAsync(FromTheFrontend(HttpMethod.Post, "/api/reports/inventory/export", correlationId, content));

        await AssertReadableAsync(response, HttpStatusCode.InternalServerError, correlationId, "internal_error");
    }

    [Fact]
    public async Task A_500_without_an_origin_keeps_the_correlation_id_header_too()
    {
        var correlationId = $"plain-500-{TestData.UniqueSuffix()}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reports/inventory/export")
        {
            Content = JsonContent.Create(new ExportInventoryRequest(null, false, "UTC"), options: TestData.Json)
        };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        factory.CommitFailures.FailNextCommit();

        var response = await _manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_429_is_readable_from_the_frontend_origin()
    {
        var client = factory.CreateAnonymousClient();
        var login = new LoginRequest($"nobody-{TestData.UniqueSuffix()}@integration.test", "Wrong-password-026b");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var counted = await client.SendAsync(
                FromTheFrontend(HttpMethod.Post, "/api/auth/login", $"cors-429-{attempt}", JsonContent.Create(login)));
            Assert.Equal(HttpStatusCode.Unauthorized, counted.StatusCode);
        }

        var correlationId = $"cors-429-{TestData.UniqueSuffix()}";
        var response = await client.SendAsync(FromTheFrontend(HttpMethod.Post, "/api/auth/login", correlationId, JsonContent.Create(login)));

        await AssertReadableAsync(response, HttpStatusCode.TooManyRequests, correlationId, errorCode: null);
    }

    [Fact]
    public async Task A_401_is_readable_from_the_frontend_origin()
    {
        var correlationId = $"cors-401-{TestData.UniqueSuffix()}";

        var response = await factory.CreateAnonymousClient().SendAsync(FromTheFrontend(HttpMethod.Get, "/api/auth/me", correlationId));

        await AssertReadableAsync(response, HttpStatusCode.Unauthorized, correlationId, errorCode: null);
    }

    [Fact]
    public async Task An_error_answer_for_another_origin_gets_no_cors_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/import")
        {
            Content = new ByteArrayContent(JsonOfLength(RequestBodyLimit.MaxBytes + 1))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
        request.Headers.Add("Origin", "https://not-the-frontend.integration.test");

        var response = await _manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

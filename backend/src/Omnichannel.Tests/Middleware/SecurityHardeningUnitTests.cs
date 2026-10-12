using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Omnichannel.Api.Auth;
using Omnichannel.Api.Middleware;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;

namespace Omnichannel.Tests.Middleware;

/// <summary>Task 026 Part B without the API host: the numbers behind the body limit and the pieces around it.</summary>
public sealed class SecurityHardeningUnitTests
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ---- B2: the limit and what it is measured against --------------------------------------------------------------

    [Fact]
    public void The_largest_bodies_the_api_accepts_fit_the_limit_with_a_margin()
    {
        // The largest stock import and the largest order the validators accept, written the wasteful way (indented).
        var import = JsonSerializer.SerializeToUtf8Bytes(
            new ImportInventoryRequest(
                new string('9', ImportInventoryRequestValidator.MaxDocumentNumberLength),
                new string('9', ImportInventoryRequestValidator.MaxDocumentSeriesLength),
                [.. Enumerable.Range(0, ImportInventoryRequestValidator.MaxItems)
                    .Select(_ => new ImportInventoryItem(Guid.NewGuid(), ImportInventoryRequestValidator.MaxStockPerItem))]),
            Indented);
        var order = JsonSerializer.SerializeToUtf8Bytes(
            new SubmitOrderRequest(
                new string('9', SubmitOrderRequestValidator.MaxExternalOrderIdLength),
                Guid.NewGuid(),
                [.. Enumerable.Range(0, SubmitOrderRequestValidator.MaxItems)
                    .Select(_ => new SubmitOrderItem(Guid.NewGuid(), SubmitOrderRequestValidator.MaxStockPerItem))]),
            Indented);
        var webhook = new WebhookOptions().MaxBodyBytes;

        // Measured 2026-10-12: the import is about 19 to 20 kB and the order 9 to 10 kB (the line ends of the platform
        // make the difference); a webhook may be 65,536 bytes.
        Assert.InRange(import.Length, 15_000, 24_000);
        Assert.InRange(order.Length, 7_000, 12_000);
        Assert.Equal(65_536, webhook);

        // The limit is twice the largest of them.
        Assert.Equal(131_072, RequestBodyLimit.MaxBytes);
        Assert.Equal(RequestBodyLimit.MaxBytes, 2L * new[] { import.Length, order.Length, webhook }.Max());
    }

    [Fact]
    public void The_reverse_proxy_limits_the_api_site_to_the_same_size()
    {
        var caddyfile = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker", "proxy", "Caddyfile"));

        var sizes = Regex.Matches(caddyfile, @"^\s*max_size\s+(\d+)KiB\s*$", RegexOptions.Multiline);

        // One limit in the file (the API site; the storage site has none), written in KiB.
        Assert.Equal(RequestBodyLimit.MaxBytes, 1024L * int.Parse(Assert.Single(sizes).Groups[1].Value));
        Assert.Single(Regex.Matches(caddyfile, @"^\s*max_size\b", RegexOptions.Multiline));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docker", "proxy", "Caddyfile")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("docker/proxy/Caddyfile not found above the test directory.");
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "request_body_too_large")] // Kestrel: a body without Content-Length passed the limit
    [InlineData(StatusCodes.Status400BadRequest, "bad_request")]                 // Kestrel: the body broke off
    public async Task A_body_the_server_refuses_while_reading_is_a_problem_of_the_client_not_a_500(int statusCode, string errorCode)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(), NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new BadHttpRequestException("text of the server, never passed on", statusCode), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(statusCode, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(errorCode, problem.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("text of the server", problem.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    // ---- B1: the documentation pages of Development ----------------------------------------------------------------------

    private static async Task<HttpResponseMessage> GetAsync(string environment, string path)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.Run(context => context.Request.Path.StartsWithSegments("/own-cache")
            ? Task.FromResult(context.Response.Headers.CacheControl = "private, max-age=60")
            : Task.CompletedTask);
        await app.StartAsync();

        return await app.GetTestClient().GetAsync(path);
    }

    [Theory]
    [InlineData("Development", "/swagger/index.html", false)]
    [InlineData("Development", "/openapi/v1.json", false)]
    [InlineData("Development", "/api/products", true)]
    [InlineData("Development", "/swagger-like", true)]
    [InlineData("Production", "/swagger/index.html", true)]
    public async Task Only_the_documentation_of_development_is_served_without_the_policy(string environment, string path, bool expectPolicy)
    {
        var response = await GetAsync(environment, path);

        Assert.Equal(expectPolicy, response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task An_answer_that_sets_its_own_cache_rule_keeps_it()
    {
        var response = await GetAsync("Production", "/own-cache");

        var cache = response.Headers.CacheControl!;
        Assert.Equal((true, TimeSpan.FromSeconds(60), false), (cache.Private, cache.MaxAge, cache.NoStore));
    }
}

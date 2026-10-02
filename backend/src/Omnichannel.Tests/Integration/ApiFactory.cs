using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Auth;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Persistence;
using Serilog.Core;
using Serilog.Events;
using Testcontainers.PostgreSql;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// One PostgreSQL container and one API host for the whole test run, shared through the "api" collection.
/// A single host is required: Serilog's bootstrap logger throws "logger is already frozen" when a
/// second host is built in the same process.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Request header a test client uses to pick its own "remote IP" (the login rate limit is per IP).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    /// <summary>Window in which this host caches a user's security state (production default: 30 s).</summary>
    public static readonly TimeSpan RevocationCacheDuration = TimeSpan.FromSeconds(2);

    // Test-only key, never used outside the test host (>= 32 bytes for HS256).
    private const string TestSigningKey = "integration-tests-signing-key-0123456789abcdef";

    private static int _nextRemoteIp;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly Dictionary<string, (Guid UserId, string Email, string SecurityStamp)> _roleUsers = [];

    /// <summary>The API test database (for Workers hosts in end-to-end tests).</summary>
    public string DatabaseConnectionString => _postgres.GetConnectionString();

    /// <summary>Everything the API host logged (Serilog), for assertions on what must never be logged.</summary>
    public CapturingLogSink Logs { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<OmnichannelDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using (var dbContext = new OmnichannelDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        // Build the host once, now, so tests never race to create it.
        _ = Server;

        // Access tokens are only accepted for existing users (security-stamp check): one shared user per role. Tests that
        // lock users or change roles create their own users through the API and never touch these.
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var role in AppRoles.All)
        {
            var email = $"{role.ToLowerInvariant()}@integration.test";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            EnsureSucceeded(await userManager.CreateAsync(user)); // no password: these users never log in
            EnsureSucceeded(await userManager.AddToRoleAsync(user, role));
            _roleUsers[role] = (user.Id, email, user.SecurityStamp!);
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": no user-secrets are loaded, so no BootstrapAdmin and no developer connection string.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("TokenRevocation:CacheDuration", RevocationCacheDuration.ToString("c"));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILogEventSink>(Logs);
            services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
        });
    }

    /// <summary>Client carrying a token of the shared test user in <paramref name="role"/>, issued by the app's own issuer.</summary>
    public HttpClient CreateClientAs(string role)
    {
        var (userId, email, securityStamp) = _roleUsers[role];
        var token = Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, email, [role], securityStamp);
        return CreateClientWithToken(token.Token);
    }

    /// <summary>Id of the shared test user behind <see cref="CreateClientAs"/> (the JWT <c>sub</c>).</summary>
    public Guid UserIdOf(string role) => _roleUsers[role].UserId;

    public HttpClient CreateClientWithToken(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Anonymous client with its own remote IP, i.e. its own login rate-limit partition (10 logins per minute and IP).</summary>
    public HttpClient CreateAnonymousClient()
    {
        var n = Interlocked.Increment(ref _nextRemoteIp);
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpHeader, $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}");
        return client;
    }

    public async Task<T> QueryDbAsync<T>(Func<OmnichannelDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>());
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Test user setup failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
        }
    }

    /// <summary>TestServer has no remote IP; lets a client choose one so the per-IP login rate limit can be kept as in production.</summary>
    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value) && IPAddress.TryParse(value, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Serilog sink collecting the API host's log events in memory.</summary>
public sealed class CapturingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Every event logged at or after <paramref name="since"/>, in order.</summary>
    public IReadOnlyList<LogEvent> EventsSince(DateTimeOffset since) => _events.Where(e => e.Timestamp >= since).ToList();

    /// <summary>Message, property values and exception of every event logged at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<string> RenderedSince(DateTimeOffset since) =>
        _events
            .Where(e => e.Timestamp >= since)
            .Select(e => $"{e.RenderMessage()} | {string.Join(" ", e.Properties.Select(p => $"{p.Key}={p.Value}"))} | {e.Exception}")
            .ToList();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    // Webhook secrets of the test host (task 023): test-only values, never used outside it. website has one secret, pos
    // two (a channel that is changing its secret), digital-platform none (an Active channel without a webhook), and
    // "webhook-off" belongs to a channel a test creates and switches off.
    public const string WebsiteWebhookSecret = "integration-tests-webhook-secret-website-01";
    public const string PosWebhookSecret = "integration-tests-webhook-secret-pos-new-02";
    public const string PosPreviousWebhookSecret = "integration-tests-webhook-secret-pos-old-03";
    public const string SwitchedOffWebhookSlug = "webhook-off";
    public const string SwitchedOffWebhookSecret = "integration-tests-webhook-secret-off-04";

    /// <summary>Webhooks per minute, client address and channel in this host (production default: 120).</summary>
    public const int WebhookPermitsPerMinute = 20;

    private static int _nextRemoteIp;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly StorageEmulator _storage = new();
    private readonly SemaphoreSlim _storageStart = new(1, 1);
    private bool _storageStarted;
    private readonly Dictionary<string, (Guid UserId, string Email, string SecurityStamp)> _roleUsers = [];

    /// <summary>The API test database (for Workers hosts in end-to-end tests).</summary>
    public string DatabaseConnectionString => _postgres.GetConnectionString();

    /// <summary>How long one request of the API to the storage may take in this host (production default: 10 s).</summary>
    public static readonly TimeSpan StorageRequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The object storage the API host is configured for, started by the first caller. The host itself never needs it
    /// to run: only a test that uploads, confirms or removes an image does.
    /// </summary>
    public async Task<StorageEmulator> StorageAsync()
    {
        await _storageStart.WaitAsync();
        try
        {
            if (!_storageStarted)
            {
                await _storage.StartAsync();
                _storageStarted = true;
            }

            return _storage;
        }
        finally
        {
            _storageStart.Release();
        }
    }

    /// <summary>Everything the API host logged (Serilog), for assertions on what must never be logged.</summary>
    public CapturingLogSink Logs { get; } = new();

    /// <summary>Makes a commit of the API host fail on demand ("the request wrote its rows, then the transaction failed").</summary>
    public CommitFailureInjection CommitFailures { get; } = new();

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
        await _storage.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": no user-secrets are loaded, so no BootstrapAdmin and no developer connection string.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("TokenRevocation:CacheDuration", RevocationCacheDuration.ToString("c"));
        // Configuration only: nothing connects to the storage until a test uses an image (StorageAsync starts it).
        builder.UseSetting("ObjectStorage:ServiceUrl", _storage.ServiceUrl);
        builder.UseSetting("ObjectStorage:Region", StorageEmulator.Region);
        builder.UseSetting("ObjectStorage:ForcePathStyle", "true");
        builder.UseSetting("ObjectStorage:BucketName", StorageEmulator.Bucket);
        builder.UseSetting("ObjectStorage:AccessKeyId", _storage.AccessKeyId);
        builder.UseSetting("ObjectStorage:SecretAccessKey", _storage.SecretAccessKey);
        builder.UseSetting("ObjectStorage:RequestTimeout", StorageRequestTimeout.ToString("c"));
        builder.UseSetting("Webhooks:PermitsPerMinute", WebhookPermitsPerMinute.ToString());
        builder.UseSetting("Webhooks:Channels:website:Secrets:0", WebsiteWebhookSecret);
        builder.UseSetting("Webhooks:Channels:pos:Secrets:0", PosWebhookSecret);
        builder.UseSetting("Webhooks:Channels:pos:Secrets:1", PosPreviousWebhookSecret);
        builder.UseSetting($"Webhooks:Channels:{SwitchedOffWebhookSlug}:Secrets:0", SwitchedOffWebhookSecret);
        // Log sources that see request URLs or the realtime traffic are captured at their most talkative level, so the
        // tests can show that the hub's access token (query string) is in none of them.
        builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting.Diagnostics", "Information");
        builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore.SignalR", "Debug");
        builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Http.Connections", "Debug");
        builder.UseSetting("Serilog:MinimumLevel:Override:Omnichannel.Api.Hubs", "Debug");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILogEventSink>(Logs);
            services.ConfigureDbContext<OmnichannelDbContext>(options => options.AddInterceptors(CommitFailures));
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

    /// <summary>An access token of the shared test user in <paramref name="role"/> (for clients that are not an HttpClient).</summary>
    public string IssueTokenFor(string role)
    {
        var (userId, email, securityStamp) = _roleUsers[role];
        return Services.GetRequiredService<IAccessTokenIssuer>().Issue(userId, email, [role], securityStamp).Token;
    }

    /// <summary>Id of the shared test user behind <see cref="CreateClientAs"/> (the JWT <c>sub</c>).</summary>
    public Guid UserIdOf(string role) => _roleUsers[role].UserId;

    /// <summary>
    /// A user of its own in <paramref name="role"/> with a client carrying its token - for what is kept per user (a
    /// user's exports and their limit), where the shared users would make one test depend on another.
    /// </summary>
    public async Task<(HttpClient Client, Guid UserId, string Email)> CreateUserClientAsync(string role)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{role.ToLowerInvariant()}.{Guid.NewGuid():N}@integration.test";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        EnsureSucceeded(await userManager.CreateAsync(user)); // no password: this user never logs in
        EnsureSucceeded(await userManager.AddToRoleAsync(user, role));

        var token = Services.GetRequiredService<IAccessTokenIssuer>().Issue(user.Id, email, [role], user.SecurityStamp!);
        return (CreateClientWithToken(token.Token), user.Id, email);
    }

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

/// <summary>
/// Test seam of the API host's DbContext: once armed, the next commit throws instead of committing, so whatever the
/// request wrote in that transaction is rolled back. Tests of the "api" collection run one after the other, so the next
/// commit is the one of the request the test sends.
/// </summary>
public sealed class CommitFailureInjection : DbTransactionInterceptor
{
    private int _armed;

    public void FailNextCommit() => Interlocked.Exchange(ref _armed, 1);

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _armed, 0) == 1
            ? throw new InvalidOperationException("injected failure before the commit")
            : base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
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

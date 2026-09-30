using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Auth;
using Omnichannel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// One PostgreSQL container and one API host for the whole test run, shared through the "api" collection.
/// A single host is required: Serilog's bootstrap logger throws "logger is already frozen" when a
/// second host is built in the same process.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Test-only key, never used outside the test host (>= 32 bytes for HS256).
    private const string TestSigningKey = "integration-tests-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>The API test database (for Workers hosts in end-to-end tests).</summary>
    public string DatabaseConnectionString => _postgres.GetConnectionString();

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
    }

    /// <summary>
    /// Client carrying a token for <paramref name="userId"/> (a random user id when null) in <paramref name="role"/>, issued by
    /// the app's own issuer.
    /// </summary>
    public HttpClient CreateClientAs(string role, Guid? userId = null)
    {
        var issuer = Services.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(userId ?? Guid.NewGuid(), $"{role.ToLowerInvariant()}@integration.test", [role]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    public async Task<T> QueryDbAsync<T>(Func<OmnichannelDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

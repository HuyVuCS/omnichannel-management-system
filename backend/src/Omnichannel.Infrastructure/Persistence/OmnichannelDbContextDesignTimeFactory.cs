using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Omnichannel.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef`. Migrations run with the schema-owner role (omnichannel_migrator),
/// never with the runtime application role, so the connection string comes from a dedicated
/// environment variable instead of the API configuration.
/// `dotnet ef migrations add` does not open a connection; `database update` does.
/// </summary>
public sealed class OmnichannelDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OmnichannelDbContext>
{
    public const string ConnectionEnvironmentVariable = "OMNICHANNEL_MIGRATOR_CONNECTION";

    public OmnichannelDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)
            ?? "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator";

        var options = new DbContextOptionsBuilder<OmnichannelDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OmnichannelDbContext(options);
    }
}

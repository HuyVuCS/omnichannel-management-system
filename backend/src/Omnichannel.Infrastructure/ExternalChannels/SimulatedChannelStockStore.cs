using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>The state of the simulated channels: the stock value each one holds per product key.</summary>
public interface ISimulatedChannelStockStore
{
    /// <summary>The channel now holds <paramref name="availableStock"/> for the product (the last write wins).</summary>
    Task SaveAsync(string channelSlug, string externalKey, int availableStock, string requestId, CancellationToken cancellationToken);

    /// <summary>The values the channel holds for <paramref name="externalKeys"/>; a key without a value is not in the result.</summary>
    Task<IReadOnlyDictionary<string, int>> ReadAsync(
        string channelSlug,
        IReadOnlyCollection<string> externalKeys,
        CancellationToken cancellationToken);
}

/// <summary>
/// <c>SimulatedChannelStocks</c> in PostgreSQL, so the state survives a restart and is the same for every Workers
/// instance. Every call opens its own connection: the simulator stands for an external system and must never take part
/// in a transaction of its caller. One statement per call.
/// </summary>
public sealed class SimulatedChannelStockStore(IOptions<DatabaseOptions> database, TimeProvider timeProvider) : ISimulatedChannelStockStore
{
    private const string SaveSql = """
        INSERT INTO "SimulatedChannelStocks" ("ChannelSlug", "ExternalKey", "AvailableStock", "LastRequestId", "UpdatedAt")
        VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT ("ChannelSlug", "ExternalKey") DO UPDATE
        SET "AvailableStock" = EXCLUDED."AvailableStock", "LastRequestId" = EXCLUDED."LastRequestId", "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    private const string ReadSql = """
        SELECT "ExternalKey", "AvailableStock" FROM "SimulatedChannelStocks" WHERE "ChannelSlug" = $1 AND "ExternalKey" = ANY($2)
        """;

    public async Task SaveAsync(string channelSlug, string externalKey, int availableStock, string requestId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SaveSql, connection)
        {
            Parameters =
            {
                new() { Value = channelSlug },
                new() { Value = externalKey },
                new() { Value = availableStock },
                new() { Value = requestId },
                new() { Value = timeProvider.GetUtcNow() }
            }
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> ReadAsync(
        string channelSlug,
        IReadOnlyCollection<string> externalKeys,
        CancellationToken cancellationToken)
    {
        var stocks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (externalKeys.Count == 0)
        {
            return stocks;
        }

        await using var connection = new NpgsqlConnection(database.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ReadSql, connection)
        {
            Parameters = { new() { Value = channelSlug }, new() { Value = externalKeys.ToArray() } }
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stocks[reader.GetString(0)] = reader.GetInt32(1);
        }

        return stocks;
    }
}

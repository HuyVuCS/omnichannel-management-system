using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Channels;
using Xunit.Abstractions;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 022 C4: the plan of the reconciliation candidate query on a volume like production's - 1,000 mappings of one
/// channel and 50,000 stock-sync tasks (50 per pair) with their attempts. It must not read the task or attempt table
/// from end to end for a page of 100 mappings; the existing indexes have to be enough (the plan is written to the
/// test output).
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class ReconciliationQueryShapeTests(WorkersFixture fixture, ITestOutputHelper output)
{
    private const int Mappings = 1_000;
    private const int TasksPerPair = 50;

    [GeneratedRegex(@"Execution Time: (?<ms>[0-9.]+) ms")]
    private static partial Regex ExecutionTime();

    [Fact]
    public async Task A_page_of_candidates_is_found_through_the_existing_indexes()
    {
        var database = await fixture.CreateIsolatedDatabaseAsync();
        var websiteId = await SeedAsync(database);

        // The statement the run sends, with its three parameters as literals.
        var sql = StockReconciliation.CandidateSql
            .Replace("{0}", $"'{websiteId}'::uuid", StringComparison.Ordinal)
            .Replace("{1}", $"'{Guid.Empty}'::uuid", StringComparison.Ordinal)
            .Replace("{2}", "100", StringComparison.Ordinal);
        var plan = new List<string>();
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        foreach (var line in plan)
        {
            output.WriteLine(line);
        }

        Assert.DoesNotContain(plan, line => line.Contains("Seq Scan on \"ProcessingTasks\"", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Seq Scan on \"ProcessingAttempts\"", StringComparison.Ordinal));
        var milliseconds = double.Parse(
            ExecutionTime().Match(string.Join('\n', plan)).Groups["ms"].Value, CultureInfo.InvariantCulture);
        Assert.True(milliseconds < 500, $"a page of 100 candidates took {milliseconds} ms");

        // And it answers what the run asks: the first hundred mappings in product order, each with its newest finished
        // task (task 1 of 50 is the newest: Succeeded) and none unfinished - except the pairs seeded otherwise.
        using var host = fixture.CreateHost("reconciliation-shape", databaseConnectionString: database);
        await using var scope = host.Services.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>().Database
            .SqlQueryRaw<StockReconciliation.Candidate>(StockReconciliation.CandidateSql, websiteId, Guid.Empty, 100)
            .ToListAsync();

        Assert.Equal(100, page.Count);
        Assert.Equal(page.Select(c => c.ProductId).Order(), page.Select(c => c.ProductId));
        Assert.All(page, c =>
        {
            Assert.Equal(10, c.AvailableStock);
            Assert.StartsWith("EXT-QS-", c.ExternalSku);
            Assert.Equal("Succeeded", c.LastFinishedStatus);
            Assert.Null(c.LastErrorCode);
            Assert.False(c.HasUnfinishedSync);
        });
    }

    /// <summary>
    /// 1,000 products mapped on website, 50 finished stock-sync tasks per pair spread over the last 50 minutes (every
    /// tenth Failed with <c>channel_unavailable</c>, the newest Succeeded), one attempt each; statistics refreshed.
    /// </summary>
    private static async Task<Guid> SeedAsync(string database)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand(
            $"""
            INSERT INTO "Products" ("ProductId", "Sku", "Name", "Price", "LowStockThreshold", "CreatedAt")
            SELECT gen_random_uuid(), 'QS-' || n, 'Query shape ' || n, 1, 0, now() FROM generate_series(1, {Mappings}) n;

            INSERT INTO "Inventories" ("ProductId", "TotalStock", "ReservedStock", "CreatedAt")
            SELECT "ProductId", 10, 0, now() FROM "Products";

            INSERT INTO "ChannelProducts" ("ChannelProductId", "ChannelId", "ProductId", "ExternalSku", "Status", "CreatedAt")
            SELECT gen_random_uuid(), c."ChannelId", p."ProductId", 'EXT-' || p."Sku", 'Active', now()
            FROM "Products" p CROSS JOIN (SELECT "ChannelId" FROM "Channels" WHERE "Slug" = 'website') c;

            INSERT INTO "ProcessingTasks" ("TaskId", "TaskType", "Status", "RetryCount", "CorrelationId", "ChannelId", "ProductId", "AttemptBudgetStart", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), 'StockSync', CASE WHEN n % 10 = 0 THEN 'Failed' ELSE 'Succeeded' END, 0, 'seed', m."ChannelId", m."ProductId", 0,
                   now() - make_interval(mins => n), now() - make_interval(mins => n)
            FROM "ChannelProducts" m CROSS JOIN generate_series(1, {TasksPerPair}) n;

            INSERT INTO "ProcessingAttempts" ("AttemptId", "TaskId", "WorkerName", "WorkerInstanceId", "AttemptNo", "Status", "StartedAt", "FinishedAt", "ErrorCode")
            SELECT gen_random_uuid(), t."TaskId", 'StockSyncWorker', 'seed', 1, t."Status", t."CreatedAt", t."UpdatedAt",
                   CASE WHEN t."Status" = 'Failed' THEN 'channel_unavailable' END
            FROM "ProcessingTasks" t;

            ANALYZE;
            """,
            connection) { CommandTimeout = 120 };
        await seed.ExecuteNonQueryAsync();

        await using var counts = new NpgsqlCommand(
            """SELECT (SELECT count(*) FROM "ChannelProducts"), (SELECT count(*) FROM "ProcessingTasks"), (SELECT count(*) FROM "ProcessingAttempts")""",
            connection);
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Assert.Equal(
                ((long)Mappings, (long)Mappings * TasksPerPair, (long)Mappings * TasksPerPair),
                (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        await using var channel = new NpgsqlCommand("""SELECT "ChannelId" FROM "Channels" WHERE "Slug" = 'website'""", connection);
        return (Guid)(await channel.ExecuteScalarAsync())!;
    }
}

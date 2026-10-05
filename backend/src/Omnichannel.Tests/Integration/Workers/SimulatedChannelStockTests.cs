using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 022 Part B: the channel-side stock of the simulated channels in PostgreSQL (table <c>SimulatedChannelStocks</c>),
/// with the production wiring of the Workers host. A database of its own: the tests look at the whole table.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SimulatedChannelStockTests(WorkersFixture fixture) : IAsyncLifetime
{
    private const string Website = "website";
    private const string Unavailable = "pos";
    private const string Rejecting = "digital-platform";

    /// <summary>The simulator answers for any slug; this one never does.</summary>
    private const string Silent = "silent-channel";

    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var database = await fixture.CreateIsolatedDatabaseAsync();
        _host = fixture.CreateHost(
            "simulated-stock",
            new Dictionary<string, string?>
            {
                ["ChannelSimulation:SkuRules"] = "true",
                [$"ChannelSimulation:Channels:{Unavailable}:Mode"] = "TransientError",
                [$"ChannelSimulation:Channels:{Rejecting}:Mode"] = "PermanentError",
                [$"ChannelSimulation:Channels:{Silent}:Mode"] = "Timeout",
                ["StockSyncWorker:ChannelCallTimeout"] = "00:00:00.300",
                // The breaker has its own unit tests; here every call must reach the simulator.
                ["StockSyncWorker:CircuitBreaker:MinimumThroughput"] = "1000"
            },
            databaseConnectionString: database);
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The channel client as the workers use it: the simulator behind the timeout and the breaker.</summary>
    private IChannelStockClient Client => _host.Services.GetRequiredService<IChannelStockClient>();

    [Fact]
    public async Task An_accepted_push_stores_the_value_and_the_next_one_replaces_it()
    {
        var first = Push(Website, externalProductId: "P-1", externalSku: "SKU-1", stock: 7);
        await Client.PushAvailableStockAsync(first, CancellationToken.None);

        var stored = Assert.Single(await RowsAsync());
        Assert.Equal((Website, "id:P-1", 7, first.RequestId), (stored.ChannelSlug, stored.ExternalKey, stored.AvailableStock, stored.LastRequestId));
        Assert.True(stored.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        // The last write wins, whatever the order of the values; a product known by its SKU is another row.
        var second = Push(Website, externalProductId: "P-1", externalSku: "SKU-1", stock: 3);
        await Client.PushAvailableStockAsync(second, CancellationToken.None);
        var bySku = Push(Website, externalProductId: null, externalSku: "SKU-1", stock: 0);
        await Client.PushAvailableStockAsync(bySku, CancellationToken.None);

        var rows = await RowsAsync();
        Assert.Equal(
            [("id:P-1", 3, second.RequestId), ("sku:SKU-1", 0, bySku.RequestId)],
            rows.Select(r => (r.ExternalKey, r.AvailableStock, r.LastRequestId)));
        Assert.True(rows[0].UpdatedAt >= stored.UpdatedAt);
    }

    [Fact]
    public async Task A_push_that_is_not_accepted_stores_nothing()
    {
        async Task<string> FailsAsync(ChannelStockUpdate update) =>
            (await Assert.ThrowsAsync<ChannelApiException>(() => Client.PushAvailableStockAsync(update, CancellationToken.None))).ErrorCode;

        Assert.Equal(ChannelApiErrorCodes.Unavailable, await FailsAsync(Push(Unavailable, "P-1", "SKU-1")));
        Assert.Equal(ChannelApiErrorCodes.Rejected, await FailsAsync(Push(Rejecting, "P-1", "SKU-1")));
        Assert.Equal(ChannelApiErrorCodes.Timeout, await FailsAsync(Push(Silent, "P-1", "SKU-1")));
        Assert.Equal(ChannelApiErrorCodes.Rejected, await FailsAsync(Push(Website, "P-2", "SIM-PERM-1")));
        Assert.Equal(ChannelApiErrorCodes.Unavailable, await FailsAsync(Push(Website, "P-3", "SIM-TEMP-1", attemptNo: 1)));
        // Cancelled by its caller before the channel answered (the host is stopping).
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.PushAvailableStockAsync(Push(Website, "P-4", "SKU-4"), cancelled.Token));

        Assert.Empty(await RowsAsync());

        // The same product on its second attempt is accepted, and stored then.
        await Client.PushAvailableStockAsync(Push(Website, "P-3", "SIM-TEMP-1", attemptNo: 2, stock: 5), CancellationToken.None);
        Assert.Equal(("id:P-3", 5), Assert.Single((await RowsAsync()).Select(r => (r.ExternalKey, r.AvailableStock))));
    }

    [Fact]
    public async Task The_lost_update_rule_accepts_the_push_and_stores_nothing()
    {
        // Accepted: no exception for the caller (the task succeeds) - and no value on the channel.
        await Client.PushAvailableStockAsync(Push(Website, externalProductId: null, externalSku: "SIM-LOST-1", stock: 9), CancellationToken.None);
        await Client.PushAvailableStockAsync(Push(Website, externalProductId: "P-9", externalSku: "SIM-LOST-2", stock: 9), CancellationToken.None);

        Assert.Empty(await RowsAsync());

        var productId = Guid.NewGuid();
        var reading = Assert.Single(await Client.ReadAvailableStockAsync(
            new ChannelStockQuery(Website, [new ChannelStockItem(productId, "S", null, "SIM-LOST-1")]), CancellationToken.None));
        Assert.Equal((productId, (int?)null), (reading.ProductId, reading.AvailableStock));
    }

    [Fact]
    public async Task A_read_returns_the_stored_values_and_nothing_for_a_product_the_channel_never_got()
    {
        await Client.PushAvailableStockAsync(Push(Website, "P-1", "SKU-1", stock: 7), CancellationToken.None);
        await Client.PushAvailableStockAsync(Push(Website, null, "SKU-2", stock: 0), CancellationToken.None);
        var (byId, bySku, never, otherKey) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var readings = await Client.ReadAvailableStockAsync(
            new ChannelStockQuery(Website,
            [
                new ChannelStockItem(byId, "S-1", "P-1", "SKU-1"),
                new ChannelStockItem(bySku, "S-2", null, "SKU-2"),
                new ChannelStockItem(never, "S-3", "P-404", null),
                // The mapping of SKU-1 without its external product id is another product for the channel.
                new ChannelStockItem(otherKey, "S-4", null, "SKU-1")
            ]),
            CancellationToken.None);

        Assert.Equal(
            new Dictionary<Guid, int?> { [byId] = 7, [bySku] = 0, [never] = null, [otherKey] = null },
            readings.ToDictionary(r => r.ProductId, r => r.AvailableStock));

        // Channels do not see each other's values, and an empty read is answered without a query.
        var elsewhere = Assert.Single(await _host.Services
            .GetRequiredKeyedService<IChannelStockClient>(ResilientChannelStockClient.AdapterKey)
            .ReadAvailableStockAsync(new ChannelStockQuery("another-channel", [new ChannelStockItem(byId, "S-1", "P-1", "SKU-1")]), CancellationToken.None));
        Assert.Null(elsewhere.AvailableStock);
        Assert.Empty(await Client.ReadAvailableStockAsync(new ChannelStockQuery(Website, []), CancellationToken.None));
    }

    [Theory]
    [InlineData(Unavailable, ChannelApiErrorCodes.Unavailable, true)]
    [InlineData(Rejecting, ChannelApiErrorCodes.Rejected, false)]
    [InlineData(Silent, ChannelApiErrorCodes.Timeout, true)]
    public async Task A_read_follows_the_mode_of_its_channel(string channel, string errorCode, bool transient)
    {
        var query = new ChannelStockQuery(channel, [new ChannelStockItem(Guid.NewGuid(), "S-1", "P-1", null)]);

        var failure = await Assert.ThrowsAsync<ChannelApiException>(() => Client.ReadAvailableStockAsync(query, CancellationToken.None));

        Assert.Equal((errorCode, transient), (failure.ErrorCode, failure.IsTransient));
        Assert.Contains(channel, failure.Message);
    }

    [Fact]
    public async Task The_sku_rules_do_not_apply_to_a_read()
    {
        var items = new[] { "SIM-PERM-1", "SIM-TEMP-1", "SIM-LOST-1" }
            .Select(sku => new ChannelStockItem(Guid.NewGuid(), "S", null, sku))
            .ToList();

        var readings = await Client.ReadAvailableStockAsync(new ChannelStockQuery(Website, items), CancellationToken.None);

        Assert.Equal(3, readings.Count);
        Assert.All(readings, r => Assert.Null(r.AvailableStock));
    }

    [Fact]
    public async Task The_simulator_writes_outside_the_transaction_of_its_caller()
    {
        // A caller that (wrongly) pushes inside a transaction and then rolls back: the channel has the value anyway,
        // as a real channel would. And the caller's uncommitted work is invisible to the simulator's own connection.
        await using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        await using (var transaction = await dbContext.Database.BeginTransactionAsync())
        {
            dbContext.Products.Add(new Product($"TX-{Guid.NewGuid():N}"[..20], "Rolled back", 1m, 0, null));
            await dbContext.SaveChangesAsync();

            await Client.PushAvailableStockAsync(Push(Website, "P-TX", "SKU-TX", stock: 4), CancellationToken.None);

            await transaction.RollbackAsync();
        }

        Assert.Equal(("id:P-TX", 4), Assert.Single((await RowsAsync()).Select(r => (r.ExternalKey, r.AvailableStock))));
        Assert.Equal(0, await dbContext.Products.CountAsync(p => p.Name == "Rolled back"));
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_is_a_channel_that_is_unavailable()
    {
        // Port 9 ("discard"): nothing listens there.
        var store = new SimulatedChannelStockStore(
            Options.Create(new DatabaseOptions { ConnectionString = "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=3" }),
            TimeProvider.System);
        var logs = new CapturingLoggerProvider();
        var client = new SimulatedChannelStockClient(
            new StaticOptions(new ChannelSimulationOptions()),
            store,
            new Logger<SimulatedChannelStockClient>(new LoggerFactory([logs])));

        var push = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Push(Website, "P-1", "SKU-1"), CancellationToken.None));
        var read = await Assert.ThrowsAsync<ChannelApiException>(() => client.ReadAvailableStockAsync(
            new ChannelStockQuery(Website, [new ChannelStockItem(Guid.NewGuid(), "S", "P-1", null)]), CancellationToken.None));

        Assert.All(new[] { push, read }, failure => Assert.Equal((ChannelApiErrorCodes.Unavailable, true), (failure.ErrorCode, failure.IsTransient)));
        // Neither the answer nor the log names the database server.
        Assert.All(new[] { push.Message, read.Message }.Concat(logs.Entries.Select(e => e.Message)), text => Assert.DoesNotContain("127.0.0.1", text));
        Assert.All(logs.Entries, e => Assert.Null(e.Exception));
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    private async Task<List<SimulatedChannelStock>> RowsAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>()
            .Set<SimulatedChannelStock>()
            .AsNoTracking()
            .OrderBy(s => s.ChannelSlug)
            .ThenBy(s => s.ExternalKey)
            .ToListAsync();
    }

    private static ChannelStockUpdate Push(string channel, string? externalProductId, string? externalSku, int stock = 7, int attemptNo = 1)
    {
        var taskId = Guid.NewGuid();
        return new ChannelStockUpdate(
            channel, Guid.NewGuid(), "SKU", externalProductId, externalSku, stock, taskId, attemptNo, ChannelStockUpdate.RequestIdOf(taskId, attemptNo, 1));
    }

    private sealed class StaticOptions(ChannelSimulationOptions value) : IOptionsMonitor<ChannelSimulationOptions>
    {
        public ChannelSimulationOptions CurrentValue => value;

        public ChannelSimulationOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ChannelSimulationOptions, string?> listener) => null;
    }
}

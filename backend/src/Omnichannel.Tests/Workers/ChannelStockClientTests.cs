using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 014: the simulated channel client (modes, latency, SKU rules).</summary>
public sealed class SimulatedChannelStockClientTests
{
    [Fact]
    public async Task Succeeds_by_default_and_logs_the_push()
    {
        var logs = new CapturingLoggerProvider();
        var client = new SimulatedChannelStockClient(
            Monitor(new ChannelSimulationOptions()),
            new InMemoryChannelStockStore(),
            new Logger<SimulatedChannelStockClient>(new LoggerFactory([logs])));
        var update = Update("website", "EXT-SKU-1");

        await client.PushAvailableStockAsync(update, CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Simulated push to website", entry.Message);
        Assert.Contains("SKU SKU-1", entry.Message);
        Assert.Contains("external SKU EXT-SKU-1", entry.Message);
        Assert.Contains("available stock 7", entry.Message);
        Assert.Contains(update.TaskId.ToString(), entry.Message);
        Assert.Contains($"attempt {update.AttemptNo}", entry.Message);
        Assert.EndsWith("accepted", entry.Message);
    }

    [Theory]
    [InlineData("TransientError", ChannelApiErrorCodes.Unavailable, true)]
    [InlineData("transienterror", ChannelApiErrorCodes.Unavailable, true)]
    [InlineData("PermanentError", ChannelApiErrorCodes.Rejected, false)]
    public async Task Error_modes_fail_with_their_code(string mode, string errorCode, bool transient)
    {
        var client = Client(Simulation(("website", mode, 0)));

        var exception = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        Assert.Equal((errorCode, transient), (exception.ErrorCode, exception.IsTransient));
        Assert.Contains("website", exception.Message);
    }

    [Fact]
    public async Task Modes_are_per_channel()
    {
        var client = Client(Simulation(("website", "PermanentError", 0)));

        await client.PushAvailableStockAsync(Update("pos"), CancellationToken.None);
        await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_mode_never_answers_until_the_call_is_cancelled()
    {
        var client = Client(Simulation(("website", "Timeout", 0)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var call = client.PushAvailableStockAsync(Update("website"), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Latency_delays_the_answer_and_honours_cancellation()
    {
        var client = Client(Simulation(("website", "Succeed", 300)));

        var watch = Stopwatch.StartNew();
        await client.PushAvailableStockAsync(Update("website"), CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds >= 250, $"answered after {watch.ElapsedMilliseconds} ms");

        var slow = Client(Simulation(("website", "Succeed", 60_000)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.PushAvailableStockAsync(Update("website"), cancellation.Token));
        Assert.True(watch.ElapsedMilliseconds < 5_000, $"cancelled after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_settings_change_applies_to_the_next_push()
    {
        var monitor = Monitor(Simulation(("website", "TransientError", 0)));
        var client = new SimulatedChannelStockClient(monitor, new InMemoryChannelStockStore(), NullLogger<SimulatedChannelStockClient>.Instance);
        await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        monitor.CurrentValue = Simulation(("website", "Succeed", 0));

        await client.PushAvailableStockAsync(Update("website"), CancellationToken.None);
    }

    [Fact]
    public async Task A_change_of_the_settings_file_reaches_the_registered_client_without_a_restart()
    {
        // The real wiring: JSON file with reload on change -> IOptionsMonitor -> the keyed adapter behind the pipeline.
        var directory = Directory.CreateTempSubdirectory("channel-simulation-");
        var file = Path.Combine(directory.FullName, "appsettings.json");
        static string Settings(string websiteMode) =>
            $$"""
            {
              "ChannelSimulation": {
                "SkuRules": true,
                "Channels": {
                  "website": { "Mode": "{{websiteMode}}", "LatencyMs": 0 },
                  "digital-platform": { "Mode": "PermanentError" }
                }
              }
            }
            """;
        try
        {
            await File.WriteAllTextAsync(file, Settings("TransientError"));
            var configuration = new ConfigurationBuilder().AddJsonFile(file, optional: false, reloadOnChange: true).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSimulatedChannelStockClient(configuration);
            // The simulator keeps what it accepted in PostgreSQL; there is no database here.
            Assert.Equal(
                typeof(SimulatedChannelStockStore),
                Assert.Single(services, d => d.ServiceType == typeof(ISimulatedChannelStockStore)).ImplementationType);
            services.AddSingleton<ISimulatedChannelStockStore>(new InMemoryChannelStockStore());
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IChannelStockClient>();
            Assert.IsType<ResilientChannelStockClient>(client);
            Assert.IsType<SimulatedChannelStockClient>(provider.GetRequiredKeyedService<IChannelStockClient>(ResilientChannelStockClient.AdapterKey));

            var unavailable = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
            Assert.Equal(ChannelApiErrorCodes.Unavailable, unavailable.ErrorCode);
            var rejected = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("digital-platform"), CancellationToken.None));
            Assert.Equal(ChannelApiErrorCodes.Rejected, rejected.ErrorCode);
            // SkuRules from the file; pos has no entry and succeeds otherwise.
            await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("pos", "SIM-PERM-1"), CancellationToken.None));
            await client.PushAvailableStockAsync(Update("pos"), CancellationToken.None);

            await File.WriteAllTextAsync(file, Settings("Succeed"));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                try
                {
                    await client.PushAvailableStockAsync(Update("website"), CancellationToken.None);
                    break;
                }
                catch (ChannelApiException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_unknown_mode_fails_with_a_message_that_names_the_setting()
    {
        var client = Client(Simulation(("website", "Sometimes", 0)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        Assert.Contains("ChannelSimulation:Channels:website:Mode 'Sometimes'", exception.Message);
        // Not a channel answer: retried like any unexpected error, visible in the attempt history.
        Assert.False(ProcessingErrorClassifier.Classify(exception).IsPermanent);
    }

    [Fact]
    public async Task Sku_rules_are_off_by_default()
    {
        var client = Client(new ChannelSimulationOptions());

        await client.PushAvailableStockAsync(Update("website", "SIM-PERM-1"), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "SIM-TEMP-1", attemptNo: 1), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "SIM-DOWN-1", attemptNo: 1), CancellationToken.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(100)]
    public async Task Sku_rules_fail_the_down_prefix_as_unavailable_on_every_attempt(int attemptNo)
    {
        var client = Client(new ChannelSimulationOptions { SkuRules = true });

        var unavailable = await Assert.ThrowsAsync<ChannelApiException>(() =>
            client.PushAvailableStockAsync(Update("pos", "SIM-DOWN-1", attemptNo: attemptNo), CancellationToken.None));

        // Temporary: retried with delay until the limit, then the task fails and the message goes to the DLQ.
        Assert.Equal((ChannelApiErrorCodes.Unavailable, true), (unavailable.ErrorCode, unavailable.IsTransient));
        // Case-sensitive, and only as a prefix.
        await client.PushAvailableStockAsync(Update("pos", "sim-down-1", attemptNo: attemptNo), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("pos", "X-SIM-DOWN-1", attemptNo: attemptNo), CancellationToken.None);
    }

    [Fact]
    public async Task Sku_rules_reject_the_permanent_prefix_and_fail_the_transient_prefix_on_attempt_one_only()
    {
        var client = Client(new ChannelSimulationOptions { SkuRules = true });

        var rejected = await Assert.ThrowsAsync<ChannelApiException>(() =>
            client.PushAvailableStockAsync(Update("website", "SIM-PERM-1", attemptNo: 3), CancellationToken.None));
        Assert.Equal((ChannelApiErrorCodes.Rejected, false), (rejected.ErrorCode, rejected.IsTransient));

        var unavailable = await Assert.ThrowsAsync<ChannelApiException>(() =>
            client.PushAvailableStockAsync(Update("website", "SIM-TEMP-1", attemptNo: 1), CancellationToken.None));
        Assert.Equal((ChannelApiErrorCodes.Unavailable, true), (unavailable.ErrorCode, unavailable.IsTransient));

        await client.PushAvailableStockAsync(Update("website", "SIM-TEMP-1", attemptNo: 2), CancellationToken.None);
        // Other SKUs, and a mapping without an external SKU, are not affected; the prefix is case-sensitive.
        await client.PushAvailableStockAsync(Update("website", "SKU-SIM-PERM-1"), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "sim-perm-1"), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", externalSku: null), CancellationToken.None);
    }

    // ---- the channel-side stock (task 022) ----------------------------------------------------------------------

    [Fact]
    public void A_product_is_known_by_its_external_product_id_or_else_by_its_external_sku()
    {
        Assert.Equal("id:EXT-1", SimulatedChannelStock.ExternalKeyOf("EXT-1", "SKU-A"));
        Assert.Equal("id:EXT-1", SimulatedChannelStock.ExternalKeyOf("EXT-1", null));
        Assert.Equal("sku:SKU-A", SimulatedChannelStock.ExternalKeyOf(null, "SKU-A"));
        // The same text as an id and as a SKU are two products.
        Assert.NotEqual(SimulatedChannelStock.ExternalKeyOf("X", null), SimulatedChannelStock.ExternalKeyOf(null, "X"));
        Assert.Throws<ArgumentException>(() => SimulatedChannelStock.ExternalKeyOf(null, null));

        // The longest external id of a mapping fits the key column.
        var longest = new string('x', Omnichannel.Domain.Entities.ChannelProduct.MaxExternalIdLength);
        Assert.True(SimulatedChannelStock.ExternalKeyOf(longest, null).Length <= SimulatedChannelStock.MaxExternalKeyLength);
        Assert.True(SimulatedChannelStock.ExternalKeyOf(null, longest).Length <= SimulatedChannelStock.MaxExternalKeyLength);
    }

    [Fact]
    public async Task An_accepted_push_is_kept_under_the_product_key_and_a_read_answers_from_it()
    {
        var store = new InMemoryChannelStockStore();
        var client = Client(new ChannelSimulationOptions(), store);
        var (byId, bySku, unknown) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await client.PushAvailableStockAsync(Update("website", "SKU-A", productId: byId, externalProductId: "P-1", stock: 7), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "SKU-B", productId: bySku, externalProductId: null, stock: 0), CancellationToken.None);
        // The same key on another channel is another product.
        await client.PushAvailableStockAsync(Update("pos", "SKU-B", externalProductId: null, stock: 99), CancellationToken.None);

        Assert.Equal(7, store.Stocks[("website", "id:P-1")].AvailableStock);
        Assert.Equal(0, store.Stocks[("website", "sku:SKU-B")].AvailableStock);

        var readings = await client.ReadAvailableStockAsync(
            new ChannelStockQuery("website",
            [
                new ChannelStockItem(byId, "S-1", "P-1", "SKU-A"),
                new ChannelStockItem(bySku, "S-2", null, "SKU-B"),
                new ChannelStockItem(unknown, "S-3", "P-404", null)
            ]),
            CancellationToken.None);

        Assert.Equal(
            [(byId, (int?)7), (bySku, (int?)0), (unknown, (int?)null)],
            readings.Select(r => (r.ProductId, r.AvailableStock)));
    }

    [Fact]
    public async Task The_lost_update_prefix_is_accepted_and_keeps_nothing_only_while_the_sku_rules_are_on()
    {
        var store = new InMemoryChannelStockStore();
        var logs = new CapturingLoggerProvider();
        var client = new SimulatedChannelStockClient(
            Monitor(new ChannelSimulationOptions { SkuRules = true }),
            store,
            new Logger<SimulatedChannelStockClient>(new LoggerFactory([logs])));

        await client.PushAvailableStockAsync(Update("website", "SIM-LOST-1", externalProductId: null), CancellationToken.None);

        Assert.Empty(store.Stocks);
        Assert.EndsWith("accepted, not stored", Assert.Single(logs.Entries).Message);

        // Without the rules it is a SKU like any other; the prefix is case-sensitive and only looked for in the SKU.
        await Client(new ChannelSimulationOptions(), store)
            .PushAvailableStockAsync(Update("website", "SIM-LOST-1", externalProductId: null), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "sim-lost-2", externalProductId: null), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website", "X", externalProductId: "SIM-LOST-3"), CancellationToken.None);
        Assert.Equal(
            ["id:SIM-LOST-3", "sku:SIM-LOST-1", "sku:sim-lost-2"],
            store.Stocks.Keys.Select(k => k.ExternalKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_failure_of_the_simulators_own_store_is_a_channel_that_is_unavailable_and_its_text_is_not_passed_on()
    {
        var store = new InMemoryChannelStockStore { Failure = new InvalidOperationException("Failed to connect to 10.1.2.3:5432 as secret-user") };
        var logs = new CapturingLoggerProvider();
        var client = new SimulatedChannelStockClient(
            Monitor(new ChannelSimulationOptions()), store, new Logger<SimulatedChannelStockClient>(new LoggerFactory([logs])));

        var push = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
        var read = await Assert.ThrowsAsync<ChannelApiException>(() => client.ReadAvailableStockAsync(
            new ChannelStockQuery("website", [new ChannelStockItem(Guid.NewGuid(), "S", "P", null)]), CancellationToken.None));

        Assert.All(new[] { push, read }, failure =>
        {
            Assert.Equal((ChannelApiErrorCodes.Unavailable, true), (failure.ErrorCode, failure.IsTransient));
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain("10.1.2.3", failure.Message);
        });
        var warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w =>
        {
            Assert.Contains("InvalidOperationException", w.Message);
            Assert.DoesNotContain("10.1.2.3", w.Message);
            Assert.DoesNotContain("secret-user", w.Message);
            Assert.Null(w.Exception);
        });
        // A push that was not kept is not reported as accepted.
        Assert.DoesNotContain(logs.Entries, e => e.Message.EndsWith("accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_store_call_is_a_cancellation_not_a_channel_failure()
    {
        var store = new InMemoryChannelStockStore { Failure = new OperationCanceledException() };
        var client = Client(new ChannelSimulationOptions(), store);

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
    }

    [Fact]
    public void A_read_asks_for_at_most_the_documented_number_of_products()
    {
        static ChannelStockItem[] Items(int count) =>
            Enumerable.Range(0, count).Select(n => new ChannelStockItem(Guid.NewGuid(), $"S-{n}", $"P-{n}", null)).ToArray();

        Assert.Equal(100, ChannelStockQuery.MaxItems);
        Assert.Equal(100, new ChannelStockQuery("website", Items(100)).Items.Count);
        Assert.Empty(new ChannelStockQuery("website", []).Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelStockQuery("website", Items(101)));
        Assert.Throws<ArgumentException>(() => new ChannelStockQuery(" ", Items(1)));
        Assert.Throws<ArgumentNullException>(() => new ChannelStockQuery("website", null!));
    }

    internal static ChannelStockUpdate Update(
        string channel,
        string? externalSku = "EXT-SKU-1",
        int attemptNo = 1,
        Guid? productId = null,
        string? externalProductId = "EXT-1",
        int stock = 7)
    {
        var taskId = Guid.NewGuid();
        return new ChannelStockUpdate(
            channel, productId ?? Guid.NewGuid(), "SKU-1", externalProductId, externalSku, stock, taskId, attemptNo,
            ChannelStockUpdate.RequestIdOf(taskId, attemptNo, 1));
    }

    private static ChannelSimulationOptions Simulation(params (string Channel, string Mode, int LatencyMs)[] channels)
    {
        var options = new ChannelSimulationOptions();
        foreach (var (channel, mode, latencyMs) in channels)
        {
            options.Channels[channel] = new SimulatedChannelSettings { Mode = mode, LatencyMs = latencyMs };
        }

        return options;
    }

    private static SimulatedChannelStockClient Client(ChannelSimulationOptions options, ISimulatedChannelStockStore? store = null) =>
        new(Monitor(options), store ?? new InMemoryChannelStockStore(), NullLogger<SimulatedChannelStockClient>.Instance);

    private static MutableOptionsMonitor Monitor(ChannelSimulationOptions options) => new() { CurrentValue = options };

    private sealed class MutableOptionsMonitor : IOptionsMonitor<ChannelSimulationOptions>
    {
        public required ChannelSimulationOptions CurrentValue { get; set; }

        public ChannelSimulationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ChannelSimulationOptions, string?> listener) => null;
    }
}

/// <summary>Task 014: the per-channel Polly pipeline (attempt timeout + circuit breaker, no retry).</summary>
public sealed class ResilientChannelStockClientTests
{
    [Fact]
    public async Task A_call_that_does_not_answer_in_time_fails_with_channel_timeout()
    {
        var adapter = new ScriptedAdapter((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var client = Client(adapter, callTimeout: TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        Assert.Equal((ChannelApiErrorCodes.Timeout, true), (exception.ErrorCode, exception.IsTransient));
        Assert.Contains("website", exception.Message);
        Assert.True(watch.ElapsedMilliseconds < 5_000, $"timed out after {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task There_is_no_retry_inside_a_call()
    {
        var adapter = new ScriptedAdapter((_, _) => throw Unavailable());
        var client = Client(adapter, minimumThroughput: 100);

        var exception = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        Assert.Equal(ChannelApiErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Breaker_opens_after_the_configured_transient_failures_and_the_channel_is_not_called()
    {
        var adapter = new ScriptedAdapter((_, _) => throw Unavailable());
        var client = Client(adapter, minimumThroughput: 3);

        for (var n = 0; n < 3; n++)
        {
            var failure = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
            Assert.Equal(ChannelApiErrorCodes.Unavailable, failure.ErrorCode);
        }

        var open = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        Assert.Equal((ChannelApiErrorCodes.CircuitOpen, true), (open.ErrorCode, open.IsTransient));
        Assert.Equal(3, adapter.Calls); // the fourth push never reached the channel
        Assert.False(ProcessingErrorClassifier.Classify(open).IsPermanent);
    }

    [Fact]
    public async Task Timeouts_count_toward_the_breaker()
    {
        var adapter = new ScriptedAdapter((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var client = Client(adapter, callTimeout: TimeSpan.FromMilliseconds(100), minimumThroughput: 2);

        for (var n = 0; n < 2; n++)
        {
            var failure = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
            Assert.Equal(ChannelApiErrorCodes.Timeout, failure.ErrorCode);
        }

        var open = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
        Assert.Equal(ChannelApiErrorCodes.CircuitOpen, open.ErrorCode);
        Assert.Equal(2, adapter.Calls);
    }

    [Fact]
    public async Task Permanent_errors_do_not_open_the_breaker()
    {
        var adapter = new ScriptedAdapter((_, _) => throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "rejected", isTransient: false));
        var client = Client(adapter, minimumThroughput: 2);

        for (var n = 0; n < 10; n++)
        {
            var failure = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
            Assert.Equal((ChannelApiErrorCodes.Rejected, false), (failure.ErrorCode, failure.IsTransient));
        }

        Assert.Equal(10, adapter.Calls);
    }

    [Fact]
    public async Task Each_channel_has_its_own_breaker()
    {
        var adapter = new ScriptedAdapter((update, _) => update.ChannelSlug == "website" ? throw Unavailable() : Task.CompletedTask);
        var client = Client(adapter, minimumThroughput: 2);

        for (var n = 0; n < 2; n++)
        {
            await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
        }

        Assert.Equal(
            ChannelApiErrorCodes.CircuitOpen,
            (await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None))).ErrorCode);
        await client.PushAvailableStockAsync(Update("pos"), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("pos"), CancellationToken.None);
    }

    [Fact]
    public async Task Breaker_closes_again_after_the_break_when_the_channel_answers()
    {
        var healthy = false;
        var adapter = new ScriptedAdapter((_, _) => healthy ? Task.CompletedTask : throw Unavailable());
        var client = Client(adapter, minimumThroughput: 2, breakDuration: TimeSpan.FromMilliseconds(500));

        for (var n = 0; n < 2; n++)
        {
            await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
        }

        await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));

        healthy = true;
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        await client.PushAvailableStockAsync(Update("website"), CancellationToken.None);
        await client.PushAvailableStockAsync(Update("website"), CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_a_channel_failure()
    {
        var adapter = new ScriptedAdapter((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var client = Client(adapter, callTimeout: TimeSpan.FromSeconds(30), minimumThroughput: 2);

        for (var n = 0; n < 3; n++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PushAvailableStockAsync(Update("website"), cancellation.Token));
        }

        // Host shutdown must not open the breaker: the channel is still called.
        Assert.Equal(3, adapter.Calls);
    }

    [Fact]
    public async Task A_read_goes_through_the_same_timeout_and_breaker_as_a_push()
    {
        var slow = new ScriptedAdapter((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var timedOut = await Assert.ThrowsAsync<ChannelApiException>(() =>
            Client(slow, callTimeout: TimeSpan.FromMilliseconds(200)).ReadAvailableStockAsync(Query("website"), CancellationToken.None));
        Assert.Equal((ChannelApiErrorCodes.Timeout, true), (timedOut.ErrorCode, timedOut.IsTransient));
        Assert.Contains("website", timedOut.Message);

        // Failed reads open the breaker, and the open breaker stops pushes too (and the other way round): one health per channel.
        var down = new ScriptedAdapter((update, _) => update.ChannelSlug == "website" ? throw Unavailable() : Task.CompletedTask);
        var client = Client(down, minimumThroughput: 2);
        for (var n = 0; n < 2; n++)
        {
            var failure = await Assert.ThrowsAsync<ChannelApiException>(() => client.ReadAvailableStockAsync(Query("website"), CancellationToken.None));
            Assert.Equal(ChannelApiErrorCodes.Unavailable, failure.ErrorCode);
        }

        var openRead = await Assert.ThrowsAsync<ChannelApiException>(() => client.ReadAvailableStockAsync(Query("website"), CancellationToken.None));
        var openPush = await Assert.ThrowsAsync<ChannelApiException>(() => client.PushAvailableStockAsync(Update("website"), CancellationToken.None));
        Assert.Equal((ChannelApiErrorCodes.CircuitOpen, ChannelApiErrorCodes.CircuitOpen), (openRead.ErrorCode, openPush.ErrorCode));
        Assert.Equal(2, down.Calls); // no retry inside a read either, and nothing reached the channel while it was open

        // Another channel is read; the readings are the adapter's.
        var query = Query("pos");
        var readings = await client.ReadAvailableStockAsync(query, CancellationToken.None);
        Assert.Equal(query.Items.Select(i => i.ProductId), readings.Select(r => r.ProductId));
    }

    private static ChannelStockQuery Query(string channel) =>
        new(channel, [new ChannelStockItem(Guid.NewGuid(), "SKU-1", "EXT-1", null), new ChannelStockItem(Guid.NewGuid(), "SKU-2", null, "EXT-SKU-2")]);

    private static ChannelStockUpdate Update(string channel) => SimulatedChannelStockClientTests.Update(channel);

    private static ChannelApiException Unavailable() => new(ChannelApiErrorCodes.Unavailable, "unavailable", isTransient: true);

    private static ResilientChannelStockClient Client(
        IChannelStockClient adapter,
        TimeSpan? callTimeout = null,
        int minimumThroughput = 5,
        TimeSpan? breakDuration = null) =>
        new(
            adapter,
            Options.Create(new ChannelResilienceOptions
            {
                CallTimeout = callTimeout ?? TimeSpan.FromSeconds(10),
                CircuitBreaker = new ChannelCircuitBreakerOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = minimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = breakDuration ?? TimeSpan.FromSeconds(30)
                }
            }),
            TimeProvider.System,
            NullLogger<ResilientChannelStockClient>.Instance);

    private sealed class ScriptedAdapter(Func<ChannelStockUpdate, CancellationToken, Task> behaviour) : IChannelStockClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return behaviour(update, cancellationToken);
        }

        /// <summary>A read is answered like a push of the same channel: the script decides, an accepted read finds nothing.</summary>
        public async Task<IReadOnlyList<ChannelStockReading>> ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await behaviour(SimulatedChannelStockClientTests.Update(query.ChannelSlug), cancellationToken);
            return query.Items.Select(item => new ChannelStockReading(item.ProductId, null)).ToList();
        }
    }
}

/// <summary>The simulated channels' state without a database (unit tests); <see cref="Failure"/> makes every call fail.</summary>
public sealed class InMemoryChannelStockStore : ISimulatedChannelStockStore
{
    public Dictionary<(string ChannelSlug, string ExternalKey), (int AvailableStock, string RequestId)> Stocks { get; } = [];

    public Exception? Failure { get; set; }

    public Task SaveAsync(string channelSlug, string externalKey, int availableStock, string requestId, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Stocks[(channelSlug, externalKey)] = (availableStock, requestId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, int>> ReadAsync(
        string channelSlug,
        IReadOnlyCollection<string> externalKeys,
        CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        IReadOnlyDictionary<string, int> found = Stocks
            .Where(entry => entry.Key.ChannelSlug == channelSlug && externalKeys.Contains(entry.Key.ExternalKey))
            .ToDictionary(entry => entry.Key.ExternalKey, entry => entry.Value.AvailableStock);
        return Task.FromResult(found);
    }
}

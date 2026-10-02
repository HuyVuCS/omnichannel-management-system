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
        var client = new SimulatedChannelStockClient(monitor, NullLogger<SimulatedChannelStockClient>.Instance);
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

    internal static ChannelStockUpdate Update(string channel, string? externalSku = "EXT-SKU-1", int attemptNo = 1)
    {
        var taskId = Guid.NewGuid();
        return new ChannelStockUpdate(
            channel, Guid.NewGuid(), "SKU-1", "EXT-1", externalSku, 7, taskId, attemptNo, ChannelStockUpdate.RequestIdOf(taskId, attemptNo, 1));
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

    private static SimulatedChannelStockClient Client(ChannelSimulationOptions options) =>
        new(Monitor(options), NullLogger<SimulatedChannelStockClient>.Instance);

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
    }
}

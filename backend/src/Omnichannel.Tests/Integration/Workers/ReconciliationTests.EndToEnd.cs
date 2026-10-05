using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Channels;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.ReconciliationWorker;
using Omnichannel.Workers.StockSyncWorker;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 022: reconciliation with the Workers running - Outbox Publisher, RabbitMQ, Stock Sync Worker and the simulated
/// channels with their state in PostgreSQL (no test double).
/// </summary>
public sealed partial class ReconciliationTests
{
    [Fact]
    public async Task Drift_becomes_a_task_that_pushes_the_central_value_and_the_next_run_finds_nothing()
    {
        var product = await ProductAsync(stock: 7);
        var mapping = await MapAsync(Website, product);
        var key = SimulatedChannelStock.ExternalKeyOf(mapping.ExternalProductId, mapping.ExternalSku);
        var host = await StartWorkersAsync("reconciliation-e2e");
        try
        {
            // The channel never got a value for the product.
            Assert.Empty(await SimulatedRowsAsync());

            var first = await RunOnAsync(host);

            Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(first));
            var task = await WaitForTaskAsync(Assert.Single(await ReconciliationTasksAsync()).TaskId, ProcessingTaskStatus.Succeeded);
            var attempt = Assert.Single(task.Attempts);
            Assert.Equal(
                (1, ProcessingAttemptStatus.Succeeded, StockSyncMessageHandler.WorkerName),
                (attempt.AttemptNo, attempt.Status, attempt.WorkerName));
            Assert.Equal(ReconciliationIds.CorrelationIdOf(first.RunId), task.CorrelationId);

            // The simulated channel holds the central value now, written by that task's push.
            var row = Assert.Single(await SimulatedRowsAsync());
            Assert.Equal((Website, key, 7, $"{task.TaskId}:1:1"), (row.ChannelSlug, row.ExternalKey, row.AvailableStock, row.LastRequestId));

            var second = await RunOnAsync(host);

            Assert.Equal((true, 1, 1, 0, 0, 0, 0, 0), Counts(second));
            Assert.Single(await ReconciliationTasksAsync());
            Assert.Single(await EventsAsync());

            // Drift made on the channel's side (somebody changed the value there): found and pushed over again.
            await host.Services.GetRequiredService<ISimulatedChannelStockStore>()
                .SaveAsync(Website, key, 99, "changed-on-the-channel", CancellationToken.None);

            var third = await RunOnAsync(host);

            Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(third));
            var repair = (await ReconciliationTasksAsync()).Single(t => t.TaskId != task.TaskId);
            await WaitForTaskAsync(repair.TaskId, ProcessingTaskStatus.Succeeded);
            Assert.Equal(7, Assert.Single(await SimulatedRowsAsync()).AvailableStock);
            Assert.Equal(2, (await EventsAsync()).Count);

            // The central stock was only ever read.
            var stock = await DbAsync(db => db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == product.ProductId));
            Assert.Equal((7, 0, 7), (stock.TotalStock, stock.ReservedStock, stock.AvailableStock));
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task The_stock_sync_alert_of_a_long_outage_is_resolved_by_the_reconciliation_task()
    {
        var product = await ProductAsync(stock: 6);
        var posId = await ChannelIdAsync(Pos);
        var posName = await DbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == Pos).Select(c => c.Name).SingleAsync());

        // The outage: pos is down, and the initial sync of the mapping runs out of attempts.
        var mapping = await ServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            posId, new CreateChannelProductRequest(product.ProductId, null, $"POS-{product.Sku}"), CancellationToken.None));
        var down = await StartWorkersAsync(
            "reconciliation-outage",
            new Dictionary<string, string?>
            {
                [$"ChannelSimulation:Channels:{Pos}:Mode"] = "TransientError",
                ["StockSyncWorker:MaxAttempts"] = "1"
            });
        try
        {
            var failed = await WaitForTaskAsync(mapping.SyncTaskId!.Value, ProcessingTaskStatus.Failed);
            Assert.Equal(ChannelApiErrorCodes.Unavailable, Assert.Single(failed.Attempts).ErrorCode);
            var open = await AlertAsync(posId, product.ProductId);
            Assert.Equal((AlertStatus.Open, failed.TaskId, ChannelApiErrorCodes.Unavailable), (open.Status, open.TaskId!.Value, open.ErrorCode));

            // While the channel is down a run cannot read it: no task, and the event says which channel and why.
            var during = await RunOnAsync(down);

            Assert.Equal((true, 0, 0, 0, 0, 0, 0, 0), Counts(during));
            Assert.Equal([new UnreadableChannel(posName, ChannelApiErrorCodes.Unavailable, 1)], during.Unreadable);
            Assert.Empty(await ReconciliationTasksAsync());
            Assert.Single(await EventsAsync());
            Assert.Equal(AlertStatus.Open, (await AlertAsync(posId, product.ProductId)).Status);
        }
        finally
        {
            await StopAsync(down);
        }

        // The channel is back. Nothing retries the failed task by itself; reconciliation is the recovery path.
        var up = await StartWorkersAsync("reconciliation-recovery");
        try
        {
            var after = await RunOnAsync(up);

            Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(after));
            var recovery = Assert.Single(await ReconciliationTasksAsync());
            await WaitForTaskAsync(recovery.TaskId, ProcessingTaskStatus.Succeeded);

            // The alert of the pair ends with the success of the pair's task, as for every stock sync - not by the run.
            var resolved = await Eventually.WaitAsync(
                () => AlertAsync(posId, product.ProductId), alert => alert.Status == AlertStatus.Resolved, Timeout, "stock-sync alert resolved");
            Assert.NotNull(resolved.ResolvedAt);
            Assert.Equal(6, Assert.Single(await SimulatedRowsAsync()).AvailableStock);
            // The task that failed stays Failed: its history is untouched.
            Assert.Equal(ProcessingTaskStatus.Failed, (await WaitForTaskAsync(mapping.SyncTaskId!.Value, ProcessingTaskStatus.Failed)).Status);
        }
        finally
        {
            await StopAsync(up);
        }
    }

    [Fact]
    public async Task The_alert_of_a_failed_sync_whose_push_landed_is_resolved_by_the_task_the_next_run_repeats()
    {
        // Task 023 A2. The pair's sync runs out of attempts on the open circuit of pos; afterwards the channel shows the
        // central value all the same (the push landed, or the stock came back): no drift, and an alert nothing would end.
        var product = await ProductAsync(stock: 6);
        var posId = await ChannelIdAsync(Pos);
        var mapping = await ServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            posId, new CreateChannelProductRequest(product.ProductId, null, $"POS-{product.Sku}"), CancellationToken.None));
        var key = SimulatedChannelStock.ExternalKeyOf(null, $"POS-{product.Sku}");

        var down = await StartWorkersAsync(
            "reconciliation-repeat-outage",
            new Dictionary<string, string?>
            {
                [$"ChannelSimulation:Channels:{Pos}:Mode"] = "TransientError",
                // Two failed calls open the breaker; the third attempt is refused by it and is the last one.
                ["StockSyncWorker:MaxAttempts"] = "3",
                ["StockSyncWorker:CircuitBreaker:MinimumThroughput"] = "2",
                ["StockSyncWorker:CircuitBreaker:BreakDuration"] = "00:05:00"
            });
        try
        {
            var failed = await WaitForTaskAsync(mapping.SyncTaskId!.Value, ProcessingTaskStatus.Failed);
            Assert.Equal(
                [ChannelApiErrorCodes.Unavailable, ChannelApiErrorCodes.Unavailable, ChannelApiErrorCodes.CircuitOpen],
                failed.Attempts.OrderBy(a => a.AttemptNo).Select(a => a.ErrorCode));
            var open = await AlertAsync(posId, product.ProductId);
            Assert.Equal((AlertStatus.Open, ChannelApiErrorCodes.CircuitOpen), (open.Status, open.ErrorCode));
        }
        finally
        {
            await StopAsync(down);
        }

        var up = await StartWorkersAsync("reconciliation-repeat-recovery");
        try
        {
            await up.Services.GetRequiredService<ISimulatedChannelStockStore>().SaveAsync(Pos, key, 6, "landed-before-the-failure", CancellationToken.None);

            var first = await RunOnAsync(up);

            // In sync, and one task all the same.
            Assert.Equal((true, 1, 1, 0, 1, 0, 0, 0), Counts(first));
            Assert.Equal(1, first.FailedRetried);
            var repeat = Assert.Single(await ReconciliationTasksAsync());
            await WaitForTaskAsync(repeat.TaskId, ProcessingTaskStatus.Succeeded);

            // Its success ends the alert, as the success of every stock sync of the pair does - the run wrote none.
            var resolved = await Eventually.WaitAsync(
                () => AlertAsync(posId, product.ProductId), alert => alert.Status == AlertStatus.Resolved, Timeout, "stock-sync alert resolved");
            Assert.NotNull(resolved.ResolvedAt);
            var row = Assert.Single(await SimulatedRowsAsync());
            Assert.Equal((6, $"{repeat.TaskId}:1:1"), (row.AvailableStock, row.LastRequestId));

            // Nothing is left to repeat: the newest finished sync of the pair succeeded.
            var second = await RunOnAsync(up);

            Assert.Equal((true, 1, 1, 0, 0, 0, 0, 0), Counts(second));
            Assert.Equal(0, second.FailedRetried);
            Assert.False(second.IsNoteworthy);
            Assert.Single(await ReconciliationTasksAsync());
            Assert.Single(await EventsAsync());
            Assert.Equal(ProcessingTaskStatus.Failed, (await WaitForTaskAsync(mapping.SyncTaskId!.Value, ProcessingTaskStatus.Failed)).Status);
        }
        finally
        {
            await StopAsync(up);
        }
    }

    [Fact]
    public async Task A_channel_that_loses_its_updates_gets_a_task_in_every_run()
    {
        var product = await ProductAsync(stock: 4);
        await DbAsync(async db =>
        {
            db.ChannelProducts.Add(new ChannelProduct(await db.Channels.Where(c => c.Slug == Website).Select(c => c.ChannelId).SingleAsync(),
                product.ProductId, null, $"{ChannelSimulationOptions.LostUpdateSkuPrefix}{product.Sku}"));
            return await db.SaveChangesAsync();
        });
        var host = await StartWorkersAsync("reconciliation-lost", new Dictionary<string, string?> { ["ChannelSimulation:SkuRules"] = "true" });
        try
        {
            for (var run = 1; run <= 3; run++)
            {
                var result = await RunOnAsync(host);

                Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(result));
                var tasks = await ReconciliationTasksAsync();
                Assert.Equal(run, tasks.Count);
                // Each push is accepted - the task succeeds - and nothing is kept: permanent drift.
                await WaitForTaskAsync(tasks.Single(t => t.CorrelationId == ReconciliationIds.CorrelationIdOf(result.RunId)).TaskId, ProcessingTaskStatus.Succeeded);
                Assert.Empty(await SimulatedRowsAsync());
            }

            Assert.Equal(3, (await EventsAsync()).Count);
            Assert.Equal(3, (await ReconciliationTasksAsync()).Select(t => t.CorrelationId).Distinct().Count());
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task The_service_runs_a_round_after_its_interval_and_logs_it_with_the_run_s_correlation_id()
    {
        var product = await ProductAsync(stock: 2);
        await MapAsync(Pos, product);
        // A second pair that is in sync, with a failed sync the run repeats (task 023 A2): a number of its own in the line.
        var repeated = await ProductAsync(stock: 5);
        var repeatedMapping = await MapAsync(Pos, repeated);
        await TaskAsync(Pos, repeated, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Timeout);
        await _tools.Services.GetRequiredService<ISimulatedChannelStockStore>().SaveAsync(
            Pos, SimulatedChannelStock.ExternalKeyOf(repeatedMapping.ExternalProductId, repeatedMapping.ExternalSku), 5, "seeded", CancellationToken.None);
        var logs = new CapturingLoggerProvider();
        var host = await StartWorkersAsync(
            "reconciliation-service",
            new Dictionary<string, string?> { ["Reconciliation:Enabled"] = "true", ["Reconciliation:Interval"] = "00:00:10" },
            logs);
        try
        {
            // No test code starts the run: the background service does, ten seconds after start-up.
            var tasks = await Eventually.WaitAsync(
                ReconciliationTasksAsync,
                found => found.Count == 2 && found.All(t => t.Status == ProcessingTaskStatus.Succeeded),
                Timeout,
                "two reconciliation tasks created by the background service and pushed");
            var posId = await ChannelIdAsync(Pos);
            Assert.All(tasks, t => Assert.Equal(posId, t.ChannelId));
            var task = tasks.Single(t => t.ProductId == product.ProductId);
            Assert.Equal(task.CorrelationId, tasks.Single(t => t.ProductId == repeated.ProductId).CorrelationId);

            var line = await Eventually.WaitAsync(
                () => Task.FromResult(logs.Entries.FirstOrDefault(e =>
                    e.Category == typeof(ReconciliationService).FullName
                    && e.Level == LogLevel.Information
                    && e.Message.StartsWith("Reconciliation run", StringComparison.Ordinal))),
                found => found is not null,
                Timeout,
                "the log line of the run");
            Assert.Contains("2 checked, 1 in sync, 1 drifted, 2 sync tasks created, 0 deferred, 1 failed syncs repeated, 0 skipped", line!.Message);
            Assert.Equal(ReconciliationService.WorkerName, line.Scope["WorkerName"]);
            Assert.Equal("reconciliation-service", line.Scope["WorkerInstanceId"]);
            Assert.Equal(task.CorrelationId, line.Scope["CorrelationId"]);

            var written = Assert.Single(await EventsAsync());
            Assert.Equal((ReconciliationService.WorkerName, task.CorrelationId), (written.WorkerName, written.CorrelationId));
            Assert.Equal([2, 5], (await SimulatedRowsAsync()).Select(row => row.AvailableStock).Order());
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task A_host_with_reconciliation_switched_off_never_runs_one()
    {
        var product = await ProductAsync(stock: 2);
        await MapAsync(Pos, product);
        var logs = new CapturingLoggerProvider();
        var host = await StartWorkersAsync(
            "reconciliation-off",
            new Dictionary<string, string?> { ["Reconciliation:Enabled"] = "false", ["Reconciliation:Interval"] = "00:00:10" },
            logs);
        try
        {
            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(ReconciliationService).FullName!, LogLevel.Information, "Reconciliation is switched off")),
                count => count == 1,
                Timeout,
                "the service says that it does not run");

            // Longer than the interval: an enabled service would have run by now.
            await Task.Delay(TimeSpan.FromSeconds(12));

            Assert.Empty(await ReconciliationTasksAsync());
            Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Reconciliation run", StringComparison.Ordinal));
        }
        finally
        {
            await StopAsync(host);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>The Workers against this test's database, with the simulated channels (no double); orders are not part of it.</summary>
    private async Task<IHost> StartWorkersAsync(string instanceId, IDictionary<string, string?>? settings = null, CapturingLoggerProvider? logs = null)
    {
        // The breaker has its own unit tests; here it must not interfere with a channel that is down on purpose -
        // unless a test asks for it.
        var overrides = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>());
        overrides.TryAdd("StockSyncWorker:CircuitBreaker:MinimumThroughput", "1000");
        var host = fixture.CreateHost(
            instanceId,
            overrides,
            services =>
            {
                services.RemoveHostedService<OrderWorkerService>();
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            _database);
        await host.StartAsync();
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private Task<ProcessingTask> WaitForTaskAsync(Guid taskId, ProcessingTaskStatus status) =>
        Eventually.WaitAsync(
            () => DbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId)),
            task => task.Status == status,
            Timeout,
            $"task {taskId} {status}");

    private Task<List<SimulatedChannelStock>> SimulatedRowsAsync() =>
        DbAsync(db => db.Set<SimulatedChannelStock>().AsNoTracking().OrderBy(s => s.ChannelSlug).ThenBy(s => s.ExternalKey).ToListAsync());

    private Task<Alert> AlertAsync(Guid channelId, Guid productId)
    {
        var key = AlertDedupKey.StockSyncFailed(channelId, productId);
        return DbAsync(db => db.Alerts.AsNoTracking().SingleAsync(a => a.DedupKey == key));
    }
}

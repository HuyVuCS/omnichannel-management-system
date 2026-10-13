using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.StaleTaskWorker;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 026b Part C: the stale-task sweep against real PostgreSQL. Every test has a database of its own (a run looks at
/// every task there is). Tasks are seeded as a worker leaves them and aged by setting their timestamps.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StaleTaskSweepTests(WorkersFixture fixture) : IAsyncLifetime
{
    private const string StaleCode = "stale_no_progress";
    private const long AdvisoryLockKey = 0x4F4D4E495F53544C;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _database = null!;
    private IHost _tools = null!;

    public async Task InitializeAsync()
    {
        await fixture.PurgeQueuesAsync();
        _database = await fixture.CreateIsolatedDatabaseAsync();
        // Not started: only its services are used.
        _tools = fixture.CreateHost("stale-tools", databaseConnectionString: _database);
    }

    public async Task DisposeAsync()
    {
        _tools.Dispose();
        await fixture.PurgeQueuesAsync();
    }

    // ---- what is stale, and what failing it writes -----------------------------------------------------------------

    [Theory]
    [InlineData(ProcessingTaskStatus.Pending)]
    [InlineData(ProcessingTaskStatus.InProgress)]
    [InlineData(ProcessingTaskStatus.RetryScheduled)]
    public async Task A_task_without_progress_is_failed_like_a_failure_that_cannot_be_retried(ProcessingTaskStatus status)
    {
        var task = await SeedAsync(status, age: StaleAfter + TimeSpan.FromMinutes(1));
        var attemptsBefore = task.Attempts.Count;

        var result = await RunAsync();

        Assert.Equal((true, 1, 0, 0, false), (result.Ran, result.Stale, result.Skipped, result.Errors, result.MoreExpected));
        Assert.Equal([task.TaskId], result.Failed);

        var failed = await LoadAsync(task.TaskId);
        Assert.Equal(ProcessingTaskStatus.Failed, failed.Status);
        // One new attempt, by the sweep; the attempt a worker left Running is closed as abandoned.
        Assert.Equal(attemptsBefore + 1, failed.Attempts.Count);
        var last = failed.Attempts.OrderBy(a => a.AttemptNo).Last();
        Assert.Equal(
            (StaleTaskSweepService.WorkerName, "stale-run", ProcessingAttemptStatus.Failed, StaleCode),
            (last.WorkerName, last.WorkerInstanceId, last.Status, last.ErrorCode));
        Assert.Contains("No progress for more than 00:30:00", last.ErrorMessage);
        Assert.DoesNotContain(failed.Attempts, a => a.Status == ProcessingAttemptStatus.Running);
        if (status == ProcessingTaskStatus.InProgress)
        {
            Assert.True(failed.Attempts.OrderBy(a => a.AttemptNo).First().IsAbandoned);
        }

        // The event of every final failure, with the task's own correlation id, and the alert of it.
        var failure = Assert.Single(await EventsAsync(ProcessingEvents.Failed));
        Assert.Equal(
            (SystemEventLevel.Error, task.CorrelationId, (Guid?)task.TaskId, StaleTaskSweepService.WorkerName),
            (failure.Level, failure.CorrelationId, failure.TaskId, failure.WorkerName));
        using (var payload = JsonDocument.Parse(failure.Payload!))
        {
            Assert.Equal(StaleCode, payload.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(last.AttemptNo, payload.RootElement.GetProperty("attemptNo").GetInt32());
        }

        var alert = Assert.Single(await DbAsync(db => db.Alerts.AsNoTracking().ToListAsync()));
        Assert.Equal(
            (AlertType.ProcessingFailed, AlertStatus.Open, (Guid?)task.TaskId, StaleCode),
            (alert.Type, alert.Status, alert.TaskId, alert.ErrorCode));

        // One event of the run, with its own correlation id.
        var run = Assert.Single(await EventsAsync(ProcessingEvents.StaleTasksFailed));
        Assert.Equal(
            (SystemEventLevel.Warning, StaleTaskIds.CorrelationIdOf(result.RunId), (Guid?)null, StaleTaskSweepService.WorkerName),
            (run.Level, run.CorrelationId, run.TaskId, run.WorkerName));
        using var runPayload = JsonDocument.Parse(run.Payload!);
        Assert.Equal(
            (result.RunId, 1800L, 1, 0),
            (runPayload.RootElement.GetProperty("runId").GetGuid(),
                runPayload.RootElement.GetProperty("staleAfterSeconds").GetInt64(),
                runPayload.RootElement.GetProperty("failed").GetInt32(),
                runPayload.RootElement.GetProperty("notListed").GetInt32()));
        Assert.Equal([task.TaskId], runPayload.RootElement.GetProperty("taskIds").EnumerateArray().Select(id => id.GetGuid()));

        // A second run finds nothing and writes nothing.
        var again = await RunAsync();
        Assert.Equal((true, 0), (again.Ran, again.Stale));
        Assert.Single(await EventsAsync(ProcessingEvents.StaleTasksFailed));
    }

    [Fact]
    public async Task A_stale_stock_sync_task_opens_the_alert_of_its_channel_and_product()
    {
        var (channelId, productId) = await ChannelAndProductAsync();
        var task = await SeedAsync(ProcessingTaskStatus.RetryScheduled, age: TimeSpan.FromHours(2), stockSync: (channelId, productId));

        var result = await RunAsync();

        Assert.Equal([task.TaskId], result.Failed);
        var alert = Assert.Single(await DbAsync(db => db.Alerts.AsNoTracking().ToListAsync()));
        Assert.Equal(
            (AlertType.StockSyncFailed, AlertStatus.Open, (Guid?)channelId, (Guid?)productId, (Guid?)task.TaskId, StaleCode),
            (alert.Type, alert.Status, alert.ChannelId, alert.ProductId, alert.TaskId, alert.ErrorCode));
    }

    [Fact]
    public async Task Finished_tasks_and_tasks_that_changed_recently_are_left_alone_and_a_clean_run_writes_nothing()
    {
        var old = TimeSpan.FromDays(3);
        var succeeded = await SeedAsync(ProcessingTaskStatus.Succeeded, age: old);
        var failedBefore = await SeedAsync(ProcessingTaskStatus.Failed, age: old);
        var pending = await SeedAsync(ProcessingTaskStatus.Pending, age: StaleAfter - TimeSpan.FromMinutes(1));
        // Created long ago, touched by a worker a minute ago: the last change counts, not the creation.
        var inProgress = await SeedAsync(ProcessingTaskStatus.InProgress, age: TimeSpan.FromMinutes(1), createdBefore: old);
        var written = await WrittenAsync();

        var result = await RunAsync();

        Assert.Equal((true, 0, 0), (result.Ran, result.Stale, result.Failed.Count));
        Assert.Equal(written, await WrittenAsync());
        var statuses = new List<ProcessingTaskStatus>();
        foreach (var task in new[] { succeeded, failedBefore, pending, inProgress })
        {
            statuses.Add((await LoadAsync(task.TaskId)).Status);
        }

        Assert.Equal(
            [ProcessingTaskStatus.Succeeded, ProcessingTaskStatus.Failed, ProcessingTaskStatus.Pending, ProcessingTaskStatus.InProgress],
            statuses);
    }

    [Fact]
    public async Task The_age_is_measured_from_the_last_change_and_a_task_exactly_as_old_as_the_limit_is_not_stale()
    {
        var now = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        var limit = now - StaleAfter;
        // PostgreSQL keeps microseconds: one of them decides.
        var microsecond = TimeSpan.FromTicks(10);
        var atTheLimit = await SeedAtAsync(ProcessingTaskStatus.Pending, createdAt: limit, updatedAt: null);
        var justOlder = await SeedAtAsync(ProcessingTaskStatus.Pending, createdAt: limit - microsecond, updatedAt: null);
        var changedAtTheLimit = await SeedAtAsync(ProcessingTaskStatus.RetryScheduled, createdAt: limit - TimeSpan.FromDays(1), updatedAt: limit);
        var changedJustBefore = await SeedAtAsync(
            ProcessingTaskStatus.RetryScheduled, createdAt: limit - TimeSpan.FromDays(1), updatedAt: limit - microsecond);

        var result = await RunAsync(clock: new FixedClock(now));

        Assert.Equal(new[] { justOlder.TaskId, changedJustBefore.TaskId }.Order(), result.Failed.Order());
        // Not even selected: the statement and the check under the row lock draw the same line.
        Assert.Equal((2, 0), (result.Stale, result.Skipped));
        Assert.Equal(ProcessingTaskStatus.Pending, (await LoadAsync(atTheLimit.TaskId)).Status);
        Assert.Equal(ProcessingTaskStatus.RetryScheduled, (await LoadAsync(changedAtTheLimit.TaskId)).Status);
    }

    // ---- a worker and the sweep at the same task -------------------------------------------------------------------

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    public async Task A_task_a_worker_touched_between_the_selection_and_the_lock_is_not_failed(string change)
    {
        var touched = await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(1));
        var other = await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(2));
        var done = false;
        // Runs once, right before the sweep asks for the first row lock (the selection is over): a worker starts - or
        // starts and completes - the task that will be locked second.
        var worker = new BeforeFirstRowLock(async () =>
        {
            done = true;
            await using var scope = _tools.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
            var task = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == touched.TaskId);
            var attempt = task.StartAttempt(OrderMessageHandler.WorkerName, "a-worker", DateTimeOffset.UtcNow);
            db.ProcessingAttempts.Add(attempt);
            if (change == "completed")
            {
                task.Succeed(null, attempt.AttemptId, DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync();
        });

        var result = await RunAsync(configureServices: s => s.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(worker)));

        Assert.True(done);
        Assert.Equal((2, 1), (result.Stale, result.Skipped));
        Assert.Equal([other.TaskId], result.Failed);
        var left = await LoadAsync(touched.TaskId);
        Assert.Equal(change == "completed" ? ProcessingTaskStatus.Succeeded : ProcessingTaskStatus.InProgress, left.Status);
        // Nothing of the sweep on it: the worker's attempt only, no event, no alert.
        Assert.Equal([OrderMessageHandler.WorkerName], left.Attempts.Select(a => a.WorkerName));
        Assert.DoesNotContain(await EventsAsync(ProcessingEvents.Failed), e => e.TaskId == touched.TaskId);
        Assert.DoesNotContain(await DbAsync(db => db.Alerts.AsNoTracking().ToListAsync()), a => a.TaskId == touched.TaskId);
    }

    // ---- limits, the lock, the service ------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_fails_at_most_MaxPerRun_tasks_the_longest_without_a_change_first()
    {
        var tasks = new List<ProcessingTask>();
        for (var n = 0; n < 5; n++)
        {
            tasks.Add(await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(1 + n)));
        }

        var settings = new Dictionary<string, string?> { ["StaleTasks:MaxPerRun"] = "2" };
        var first = await RunAsync(settings);
        var second = await RunAsync(settings);
        var third = await RunAsync(settings);

        Assert.Equal((2, true), (first.Stale, first.MoreExpected));
        Assert.Equal([tasks[4].TaskId, tasks[3].TaskId], first.Failed);
        Assert.Equal((2, true), (second.Stale, second.MoreExpected));
        Assert.Equal([tasks[2].TaskId, tasks[1].TaskId], second.Failed);
        Assert.Equal((1, false), (third.Stale, third.MoreExpected));
        Assert.Equal([tasks[0].TaskId], third.Failed);
        Assert.Equal(3, (await EventsAsync(ProcessingEvents.StaleTasksFailed)).Count);
    }

    [Fact]
    public async Task The_event_of_a_run_lists_at_most_fifty_task_ids_and_counts_the_rest()
    {
        await DbAsync(async db =>
        {
            db.ProcessingTasks.AddRange(Enumerable.Range(0, 53).Select(_ => NewOrderTask()));
            return await db.SaveChangesAsync();
        });
        await DbAsync(db => db.Database.ExecuteSqlAsync($"""UPDATE "ProcessingTasks" SET "CreatedAt" = now() - interval '1 day'"""));

        var result = await RunAsync();

        Assert.Equal(53, result.Failed.Count);
        using var payload = JsonDocument.Parse(Assert.Single(await EventsAsync(ProcessingEvents.StaleTasksFailed)).Payload!);
        Assert.Equal(
            (53, 50, 3),
            (payload.RootElement.GetProperty("failed").GetInt32(),
                payload.RootElement.GetProperty("taskIds").GetArrayLength(),
                payload.RootElement.GetProperty("notListed").GetInt32()));
    }

    [Fact]
    public async Task While_another_instance_runs_a_sweep_a_run_does_nothing()
    {
        var task = await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(1));
        var written = await WrittenAsync();

        // "Another instance": a session that holds the lock of the sweep (not pooled: closing it ends the session).
        await using (var other = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_database) { Pooling = false }.ConnectionString))
        {
            await other.OpenAsync();
            await using var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", other) { Parameters = { new() { Value = AdvisoryLockKey } } };
            await take.ExecuteScalarAsync();

            var skipped = await RunAsync();

            Assert.Equal((false, 0, 0), (skipped.Ran, skipped.Stale, skipped.Failed.Count));
            Assert.Equal(written, await WrittenAsync());
            Assert.Equal(ProcessingTaskStatus.Pending, (await LoadAsync(task.TaskId)).Status);
        }

        // The session is gone and its lock with it.
        Assert.Equal([task.TaskId], (await RunAsync()).Failed);
    }

    [Fact]
    public async Task Two_instances_at_once_fail_every_stale_task_exactly_once()
    {
        var tasks = new List<ProcessingTask>();
        for (var n = 0; n < 12; n++)
        {
            tasks.Add(await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(1)));
        }

        using var first = SweepHost("stale-a");
        using var second = SweepHost("stale-b");
        var results = await Task.WhenAll(RunOnAsync(first, "stale-a"), RunOnAsync(second, "stale-b"));
        // The instance that found the other one at work comes back at its next round.
        var later = await RunOnAsync(second, "stale-b");

        Assert.Equal(tasks.Select(t => t.TaskId).Order(), results.Append(later).SelectMany(r => r.Failed).Order());
        foreach (var task in tasks)
        {
            var failed = await LoadAsync(task.TaskId);
            Assert.Equal(ProcessingTaskStatus.Failed, failed.Status);
            Assert.Single(failed.Attempts);
        }

        Assert.Equal(12, (await EventsAsync(ProcessingEvents.Failed)).Count);
        Assert.Equal(12, await DbAsync(db => db.Alerts.CountAsync()));
    }

    [Fact]
    public async Task The_service_runs_the_sweep_every_interval_and_not_at_all_when_it_is_switched_off()
    {
        var task = await SeedAsync(ProcessingTaskStatus.Pending, age: TimeSpan.FromHours(1));
        var logs = new CapturingLoggerProvider();
        var settings = new Dictionary<string, string?>
        {
            ["StaleTasks:Enabled"] = "false",
            ["StaleTasks:Interval"] = "00:00:01",
            ["StaleTasks:StaleAfter"] = "00:01:00"
        };

        using (var off = fixture.CreateHost("stale-off", settings, s => s.AddSingleton<ILoggerProvider>(logs), _database))
        {
            await off.StartAsync();
            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(StaleTaskSweepService).FullName!, LogLevel.Information, "switched off")),
                count => count == 1, Timeout, "the service says that it is switched off");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            await off.StopAsync();
        }

        Assert.Equal(ProcessingTaskStatus.Pending, (await LoadAsync(task.TaskId)).Status);

        settings["StaleTasks:Enabled"] = "true";
        using var on = fixture.CreateHost("stale-on", settings, databaseConnectionString: _database);
        await on.StartAsync();
        try
        {
            await Eventually.WaitAsync(
                async () => (await LoadAsync(task.TaskId)).Status, status => status == ProcessingTaskStatus.Failed, Timeout,
                "the task is failed by the service");
        }
        finally
        {
            await on.StopAsync();
        }

        var attempt = Assert.Single((await LoadAsync(task.TaskId)).Attempts);
        Assert.Equal((StaleTaskSweepService.WorkerName, "stale-on", StaleCode), (attempt.WorkerName, attempt.WorkerInstanceId, attempt.ErrorCode));
    }

    [Theory]
    // Shorter than twice the longest retry delay of this host (4 s) or than twice the publisher's longest wait.
    [InlineData("StaleTasks:StaleAfter", "00:00:07", "StaleTasks:StaleAfter")]
    [InlineData("StaleTasks:StaleAfter", "00:00:00", "StaleTasks:StaleAfter")]
    [InlineData("StaleTasks:Interval", "00:00:00", "StaleTasks:Interval")]
    [InlineData("StaleTasks:Interval", "2.00:00:00", "StaleTasks:Interval")]
    [InlineData("StaleTasks:MaxPerRun", "0", "StaleTasks:MaxPerRun")]
    [InlineData("StaleTasks:MaxPerRun", "1001", "StaleTasks:MaxPerRun")]
    public async Task A_host_with_invalid_settings_of_the_sweep_does_not_start(string key, string value, string named)
    {
        // The publisher of this host gives up after about a second, so only the retry delays (4 s) matter here.
        var settings = new Dictionary<string, string?> { ["OutboxPublisher:MaxAttempts"] = "3", [key] = value };
        using var host = fixture.CreateHost("stale-invalid", settings, databaseConnectionString: _database);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(named, Assert.Single(exception.Failures));
    }

    // ---- the message of before a manual retry ------------------------------------------------------------------------

    [Fact]
    public async Task After_a_manual_retry_the_message_of_before_starts_no_attempt_and_the_new_one_runs_exactly_once()
    {
        var (customerId, productId) = await CustomerAndProductAsync(stock: 5);
        var submitted = await ServicesAsync(services => services.GetRequiredService<IOrderIntakeService>().SubmitAsync(
            "website",
            new SubmitOrderRequest($"STALE-{Guid.NewGuid():N}", customerId, [new SubmitOrderItem(productId, 2)]),
            CancellationToken.None));
        var taskId = submitted.TaskId;
        var before = await MessageOfAsync(taskId);

        // The message is lost before any worker saw it: the task stands still and is failed.
        await AgeAsync(taskId, DateTimeOffset.UtcNow - TimeSpan.FromHours(1), updatedAt: null);
        Assert.Equal([taskId], (await RunAsync()).Failed);

        // While the task is Failed the message of before is a duplicate, as ever.
        Assert.Same(HandlerResult.Ack, await DeliverAsync(before));
        Assert.Equal(ProcessingTaskStatus.Failed, (await LoadAsync(taskId)).Status);

        var retried = await ServicesAsync(services => services.GetRequiredService<IProcessingTaskService>().RetryAsync(taskId, CancellationToken.None));
        var after = await MessageOfAsync(taskId);
        Assert.Equal(retried.MessageId, after.OutboxEventId.ToString());
        Assert.NotEqual(before.OutboxEventId, after.OutboxEventId);

        // Both are delivered - the one of before first, as it would come out of a retry queue.
        var logs = new CapturingLoggerProvider();
        Assert.Same(HandlerResult.Ack, await DeliverAsync(before, logs));
        var waiting = await LoadAsync(taskId);
        Assert.Equal(ProcessingTaskStatus.Pending, waiting.Status);
        Assert.Equal([StaleTaskSweepService.WorkerName], waiting.Attempts.Select(a => a.WorkerName));
        Assert.Contains(logs.Entries, e => e.Message.Contains("is not the message task", StringComparison.Ordinal)
            && e.Message.Contains(before.OutboxEventId.ToString(), StringComparison.Ordinal));

        Assert.Same(HandlerResult.Ack, await DeliverAsync(after));
        // ... and once more afterwards.
        Assert.Same(HandlerResult.Ack, await DeliverAsync(before));

        var done = await LoadAsync(taskId);
        Assert.Equal(ProcessingTaskStatus.Succeeded, done.Status);
        Assert.Equal(
            [(StaleTaskSweepService.WorkerName, ProcessingAttemptStatus.Failed), (OrderMessageHandler.WorkerName, ProcessingAttemptStatus.Succeeded)],
            done.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.WorkerName, a.Status)));
        // One order, the stock reserved once.
        Assert.Equal(1, await DbAsync(db => db.Orders.CountAsync()));
        var stock = await DbAsync(db => db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId));
        Assert.Equal((5, 2, 3), (stock.TotalStock, stock.ReservedStock, stock.AvailableStock));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static ProcessingTask NewOrderTask() =>
        new(ProcessingTaskType.OrderProcessing, orderId: null, $"stale-test-{Guid.NewGuid():N}", Guid.NewGuid().ToString());

    /// <summary>
    /// A task in <paramref name="status"/> as a worker leaves it, last changed <paramref name="age"/> ago (a Pending
    /// task was never changed: its creation counts).
    /// </summary>
    private Task<ProcessingTask> SeedAsync(
        ProcessingTaskStatus status,
        TimeSpan age,
        TimeSpan? createdBefore = null,
        (Guid ChannelId, Guid ProductId)? stockSync = null)
    {
        var changedAt = DateTimeOffset.UtcNow - age;
        return status == ProcessingTaskStatus.Pending
            ? SeedAtAsync(status, createdAt: changedAt, updatedAt: null, stockSync)
            : SeedAtAsync(status, createdAt: changedAt - (createdBefore ?? TimeSpan.FromMinutes(5)), updatedAt: changedAt, stockSync);
    }

    private async Task<ProcessingTask> SeedAtAsync(
        ProcessingTaskStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        (Guid ChannelId, Guid ProductId)? stockSync = null)
    {
        var task = stockSync is { } pair
            ? ProcessingTask.ForStockSync(pair.ChannelId, pair.ProductId, $"stale-test-{Guid.NewGuid():N}", Guid.NewGuid().ToString())
            : NewOrderTask();
        var worker = stockSync is null ? OrderMessageHandler.WorkerName : "StockSyncWorker";
        ProcessingAttempt Start() => task.StartAttempt(worker, "seed", createdAt);

        switch (status)
        {
            case ProcessingTaskStatus.Pending:
                break;
            case ProcessingTaskStatus.InProgress:
                Start();
                break;
            case ProcessingTaskStatus.RetryScheduled:
                task.ScheduleRetry(Start().AttemptId, "database_unavailable", "seeded", createdAt);
                break;
            case ProcessingTaskStatus.Succeeded:
                task.Succeed(null, Start().AttemptId, createdAt);
                break;
            case ProcessingTaskStatus.Failed:
                task.Fail(Start().AttemptId, "unexpected", "seeded", createdAt);
                break;
        }

        await DbAsync(async db =>
        {
            db.ProcessingTasks.Add(task);
            return await db.SaveChangesAsync();
        });
        await AgeAsync(task.TaskId, createdAt, updatedAt);
        return task;
    }

    private Task<int> AgeAsync(Guid taskId, DateTimeOffset createdAt, DateTimeOffset? updatedAt) =>
        DbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "ProcessingTasks" SET "CreatedAt" = {createdAt}, "UpdatedAt" = {updatedAt} WHERE "TaskId" = {taskId}"""));

    private IHost SweepHost(
        string instanceId,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null) =>
        fixture.CreateHost(instanceId, settings, configureServices, _database);

    /// <summary>One run, as the service starts it, on a host with these settings (not started).</summary>
    private async Task<StaleTaskSweepResult> RunAsync(
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        TimeProvider? clock = null)
    {
        using var host = SweepHost("stale-run", settings, services =>
        {
            configureServices?.Invoke(services);
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });
        return await RunOnAsync(host, "stale-run");
    }

    private static async Task<StaleTaskSweepResult> RunOnAsync(IHost host, string instanceId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStaleTaskSweep>()
            .RunAsync(Guid.CreateVersion7(), StaleTaskSweepService.WorkerName, instanceId, CancellationToken.None);
    }

    private async Task<T> DbAsync<T>(Func<OmnichannelDbContext, Task<T>> action)
    {
        await using var scope = _tools.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>());
    }

    private async Task<T> ServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _tools.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<ProcessingTask> LoadAsync(Guid taskId) =>
        DbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    private Task<List<SystemEventLog>> EventsAsync(string eventType) =>
        DbAsync(db => db.SystemEventLogs.AsNoTracking().Where(e => e.EventType == eventType).OrderBy(e => e.LogId).ToListAsync());

    /// <summary>Rows in the tables a run may write.</summary>
    private Task<(int Attempts, int Events, int Alerts)> WrittenAsync() =>
        DbAsync(async db => (await db.ProcessingAttempts.CountAsync(), await db.SystemEventLogs.CountAsync(), await db.Alerts.CountAsync()));

    private async Task<(Guid ChannelId, Guid ProductId)> ChannelAndProductAsync()
    {
        var product = new Product($"ST-{Guid.NewGuid():N}"[..20], "Stale product", 3m, 0, null);
        await DbAsync(async db =>
        {
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 4));
            return await db.SaveChangesAsync();
        });
        var channelId = await DbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == "website").Select(c => c.ChannelId).SingleAsync());
        return (channelId, product.ProductId);
    }

    private async Task<(Guid CustomerId, Guid ProductId)> CustomerAndProductAsync(int stock)
    {
        var customer = new Customer($"Stale customer {Guid.NewGuid():N}", null, null, null);
        var product = new Product($"ST-{Guid.NewGuid():N}"[..20], "Stale product", 3m, 0, null);
        await DbAsync(async db =>
        {
            db.Customers.Add(customer);
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, stock));
            return await db.SaveChangesAsync();
        });
        return (customer.CustomerId, product.ProductId);
    }

    /// <summary>The outbox row the task waits for.</summary>
    private Task<OutboxEvent> MessageOfAsync(Guid taskId) =>
        DbAsync(async db =>
        {
            var messageId = Guid.Parse((await db.ProcessingTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId)).MessageId!);
            return await db.OutboxEvents.AsNoTracking().SingleAsync(e => e.OutboxEventId == messageId);
        });

    /// <summary>One delivery of the message to the Order Worker's handler, as its consumer makes it (a scope per delivery).</summary>
    private async Task<HandlerResult> DeliverAsync(OutboxEvent message, CapturingLoggerProvider? logs = null)
    {
        using var host = SweepHost("stale-order-worker", configureServices: services =>
        {
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
        await using var scope = host.Services.CreateAsyncScope();
        var envelope = MessageEnvelope.FromOutboxEvent(message);
        var delivery = new ReceivedMessage(
            MessageJson.SerializeToUtf8Bytes(envelope),
            envelope.MessageId,
            envelope.CorrelationId,
            Redelivered: false,
            EventTypes.OrderCreated,
            DeliveryCount: 0,
            new BasicProperties { MessageId = envelope.MessageId, CorrelationId = envelope.CorrelationId });
        return await scope.ServiceProvider.GetRequiredService<OrderMessageHandler>().HandleAsync(delivery, CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Runs <paramref name="action"/> once, before the first statement that locks a task row.</summary>
    private sealed class BeforeFirstRowLock(Func<Task> action) : DbCommandInterceptor
    {
        private int _done;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) && Interlocked.Exchange(ref _done, 1) == 0)
            {
                await action();
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

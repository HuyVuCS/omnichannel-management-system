using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.StockSyncWorker;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 014: the Stock Sync Worker against real PostgreSQL and RabbitMQ (test delay tiers 1 s / 2 s / 3 s / 4 s). The channel
/// adapter is a recording double; everything else is the production host.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class StockSyncWorkerTests(WorkersFixture fixture) : IAsyncLifetime
{
    private const string Website = "website";
    private const string Pos = "pos";
    private const string Marketplace = "digital-platform";
    private const string WebsiteQueue = RabbitMqTopology.StockSyncWebsiteQueue;
    private const string PosQueue = RabbitMqTopology.StockSyncPosQueue;
    private const string MarketplaceQueue = RabbitMqTopology.StockSyncMarketplaceQueue;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- end to end -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_change_is_pushed_to_every_channel_that_sells_the_product_and_to_no_other()
    {
        var product = await SeedProductAsync();
        var onWebsite = await MapAsync(Website, product.ProductId, externalProductId: "WEB-100", externalSku: "WEB-SKU");
        var onPos = await MapAsync(Pos, product.ProductId, externalSku: "POS-SKU");
        var import = await ImportAsync(product.ProductId, 7);
        var change = await OutboxOfMovementAsync(import.Items[0].MovementId);
        var channel = new RecordingChannelClient();

        await RunWorkersAsync(channel, async () =>
        {
            await WaitForTasksAsync(product.ProductId, count: 4, ProcessingTaskStatus.Succeeded);
            await WaitForEmptyAsync(WebsiteQueue, PosQueue, MarketplaceQueue);
        });

        var tasks = await TasksOfAsync(product.ProductId);
        var (websiteId, posId, marketplaceId) = (await ChannelIdAsync(Website), await ChannelIdAsync(Pos), await ChannelIdAsync(Marketplace));
        Assert.Equal(4, tasks.Count);
        Assert.Equal([websiteId, websiteId, posId, posId], tasks.Select(t => t.ChannelId!.Value).OrderBy(id => id == websiteId ? 0 : 1));
        Assert.DoesNotContain(tasks, t => t.ChannelId == marketplaceId);
        Assert.All(tasks, t =>
        {
            Assert.Equal((ProcessingTaskType.StockSync, ProcessingTaskStatus.Succeeded, (Guid?)product.ProductId, (Guid?)null),
                (t.TaskType, t.Status, t.ProductId, t.OrderId));
            var attempt = Assert.Single(t.Attempts);
            Assert.Equal((1, ProcessingAttemptStatus.Succeeded, StockSyncMessageHandler.WorkerName, (string?)null),
                (attempt.AttemptNo, attempt.Status, attempt.WorkerName, attempt.ErrorCode));
            Assert.StartsWith("stock-sync-", attempt.WorkerInstanceId);
            Assert.NotNull(attempt.FinishedAt);
        });

        // One task per channel for the stock change, found by the idempotency key of that message; it carries the
        // correlation id of the import. The two others are the initial syncs of the mappings.
        foreach (var channelId in new[] { websiteId, posId })
        {
            var forChange = Assert.Single(tasks, t => t.IdempotencyKey == StockSyncIdempotencyKey.For(channelId, change.OutboxEventId.ToString()));
            Assert.Equal((channelId, change.OutboxEventId.ToString(), change.CorrelationId), (forChange.ChannelId!.Value, forChange.MessageId, forChange.CorrelationId));
        }

        Assert.Contains(tasks, t => t.TaskId == onWebsite.SyncTaskId && t.IdempotencyKey is null);
        Assert.Contains(tasks, t => t.TaskId == onPos.SyncTaskId && t.IdempotencyKey is null);

        // Every push carries the stock as it is now (7), the mapping's external ids and a request id.
        var pushes = channel.PushesOf(product.ProductId);
        Assert.Equal(4, pushes.Count);
        Assert.All(pushes, p =>
        {
            Assert.Equal((7, product.Sku, 1), (p.AvailableStock, p.Sku, p.AttemptNo));
            Assert.Equal($"{p.TaskId}:1:1", p.RequestId);
        });
        Assert.All(pushes.Where(p => p.ChannelSlug == Website), p => Assert.Equal(("WEB-100", "WEB-SKU"), (p.ExternalProductId, p.ExternalSku)));
        Assert.All(pushes.Where(p => p.ChannelSlug == Pos), p => Assert.Equal(((string?)null, "POS-SKU"), (p.ExternalProductId, p.ExternalSku)));
        Assert.Equal([Pos, Pos, Website, Website], pushes.Select(p => p.ChannelSlug).Order());
        Assert.Equal(tasks.Select(t => t.TaskId).Order(), pushes.Select(p => p.TaskId).Order());

        // The stock rows were only read.
        Assert.Equal((7, 0, 7), await StockAsync(product.ProductId));
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task Product_without_an_active_mapping_creates_no_task_and_the_backlog_is_just_acknowledged()
    {
        var unmapped = await SeedProductAsync();
        var deactivated = await SeedProductAsync();
        var mapping = await MapAsync(Website, deactivated.ProductId, externalSku: "OFF");
        await UpdateMappingAsync(Website, mapping.ChannelProductId, new UpdateChannelProductRequest(null, "OFF", ChannelProductStatus.Inactive));
        await ImportAsync(unmapped.ProductId, 3);
        await ImportAsync(deactivated.ProductId, 3);
        var channel = new RecordingChannelClient();

        await RunWorkersAsync(channel, async () =>
        {
            // The initial sync of the mapping finds it deactivated: done, nothing pushed.
            await WaitForTasksAsync(deactivated.ProductId, count: 1, ProcessingTaskStatus.Succeeded);
            await WaitForEmptyAsync(WebsiteQueue, PosQueue, MarketplaceQueue);
        });

        Assert.Empty(await TasksOfAsync(unmapped.ProductId));
        var initial = Assert.Single(await TasksOfAsync(deactivated.ProductId));
        Assert.Equal((mapping.SyncTaskId, ProcessingAttemptStatus.Succeeded), (initial.TaskId, Assert.Single(initial.Attempts).Status));
        Assert.Empty(channel.PushesOf(unmapped.ProductId));
        Assert.Empty(channel.PushesOf(deactivated.ProductId));
        await AssertNoDeadLettersAsync();
    }

    // ---- idempotency and the pushed value ---------------------------------------------------------------------

    [Fact]
    public async Task Same_inventory_change_delivered_twice_creates_one_task_and_one_push()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var import = await ImportAsync(product.ProductId, 5);
        var change = await OutboxOfMovementAsync(import.Items[0].MovementId);
        var channel = new RecordingChannelClient();
        var logs = new CapturingLoggerProvider();

        await RunWorkersAsync(channel, async () =>
        {
            await WaitForTasksAsync(product.ProductId, count: 2, ProcessingTaskStatus.Succeeded);

            // The very same message again (publisher crash between confirm and commit, or a late retry copy).
            await fixture.PublishAsync(
                EventTypes.InventoryChanged,
                MessageJson.SerializeToUtf8Bytes(MessageEnvelope.FromOutboxEvent(change)),
                change.OutboxEventId.ToString());

            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Entries.Count(e =>
                    e.Category == typeof(StockSyncMessageHandler).FullName
                    && e.Message.Contains("Duplicate delivery", StringComparison.Ordinal)
                    && Equals(e.Scope.GetValueOrDefault("ProductId"), product.ProductId))),
                count => count == 1,
                Timeout,
                "duplicate delivery acknowledged");
            await WaitForEmptyAsync(WebsiteQueue, PosQueue, MarketplaceQueue);
        }, logs: logs);

        var tasks = await TasksOfAsync(product.ProductId);
        Assert.Equal(2, tasks.Count); // the initial sync and the one change
        var forChange = Assert.Single(tasks, t => t.TaskId != mapping.SyncTaskId);
        Assert.Single(forChange.Attempts);
        Assert.Single(channel.PushesOfTask(forChange.TaskId));
        Assert.Equal(2, channel.PushesOf(product.ProductId).Count);
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_change_resolve_to_one_task()
    {
        var product = await SeedProductAsync();
        await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var websiteId = await ChannelIdAsync(Website);
        using var host = fixture.CreateHost("stock-sync-resolve");

        for (var round = 0; round < 5; round++)
        {
            var messageId = Guid.CreateVersion7().ToString();

            var resolutions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IStockSyncService>()
                    .ResolveInventoryChangeAsync(Website, product.ProductId, messageId, $"corr-{messageId}", CancellationToken.None);
            }));

            Assert.All(resolutions, r => Assert.Equal((StockSyncResolutionKind.Task, (Guid?)websiteId), (r.Kind, r.ChannelId)));
            Assert.Single(resolutions.Select(r => r.TaskId).Distinct());
            var key = StockSyncIdempotencyKey.For(websiteId, messageId);
            Assert.Equal(1, await fixture.WithDbAsync(db => db.ProcessingTasks.CountAsync(t => t.IdempotencyKey == key)));
        }
    }

    [Fact]
    public async Task Expected_unique_violation_is_not_logged_by_ef_core_at_warning_or_above_in_the_workers_host()
    {
        // Part A1 in the Workers host: a duplicate order submission hits UX_ProcessingTasks_IdempotencyKey on every call.
        var customer = new Customer($"Log customer {Guid.NewGuid():N}", null, null, null);
        var product = await SeedProductAsync();
        await fixture.WithDbAsync(async db =>
        {
            db.Customers.Add(customer);
            return await db.SaveChangesAsync();
        });
        var logs = new CapturingLoggerProvider();
        using var host = fixture.CreateHost("stock-sync-logs", configureServices: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            // Debug included, to see where EF Core's own entries went.
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        });
        var order = new SubmitOrderRequest($"LOG-{Guid.NewGuid():N}", customer.CustomerId, [new SubmitOrderItem(product.ProductId, 1)]);

        async Task<SubmitOrderResponse> SubmitAsync()
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOrderIntakeService>().SubmitAsync(Website, order, CancellationToken.None);
        }

        var first = await SubmitAsync();
        var second = await SubmitAsync();

        Assert.Equal((false, true, first.TaskId), (first.Duplicate, second.Duplicate, second.TaskId));
        var efCore = logs.Entries.Where(e => e.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(efCore, e => e.Level >= LogLevel.Warning);
        // EF Core's own entries for the conflict were written - at Debug.
        Assert.Contains(efCore, e => e.Level == LogLevel.Debug && e.Message.StartsWith("Failed executing DbCommand", StringComparison.Ordinal));
        Assert.Contains(efCore, e => e.Level == LogLevel.Debug && e.Message.StartsWith("An exception occurred in the database while saving changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Current_stock_is_pushed_not_the_snapshot_of_the_message()
    {
        var product = await SeedProductAsync();
        await MapAsync(Website, product.ProductId, externalSku: "WEB");
        await ImportAsync(product.ProductId, 5);
        var websiteId = await ChannelIdAsync(Website);

        // A message whose snapshot is long out of date (e.g. a retry copy that waited in a delay queue).
        var staleMessageId = Guid.CreateVersion7().ToString();
        var stale = new MessageEnvelope(
            staleMessageId,
            $"corr-{staleMessageId}",
            TaskId: null,
            EventTypes.InventoryChanged,
            DateTimeOffset.UtcNow.AddHours(-1),
            JsonSerializer.SerializeToElement(
                new InventoryChangedPayload(product.ProductId, Guid.NewGuid(), InventoryMovementType.Import, 999, 0, 999, DateTimeOffset.UtcNow.AddHours(-1)),
                MessageJson.Options));
        await fixture.PublishAsync(EventTypes.InventoryChanged, MessageJson.SerializeToUtf8Bytes(stale), staleMessageId);
        var channel = new RecordingChannelClient();

        await RunWorkersAsync(channel, () => WaitForTasksAsync(product.ProductId, count: 3, ProcessingTaskStatus.Succeeded));

        var staleTask = Assert.Single(await TasksOfAsync(product.ProductId), t => t.IdempotencyKey == StockSyncIdempotencyKey.For(websiteId, staleMessageId));
        Assert.Equal(5, Assert.Single(channel.PushesOfTask(staleTask.TaskId)).AvailableStock);
        Assert.All(channel.PushesOf(product.ProductId), p => Assert.Equal(5, p.AvailableStock));
    }

    [Fact]
    public async Task Change_committed_during_a_push_is_pushed_again_in_the_same_attempt()
    {
        var product = await SeedProductAsync();
        await ImportAsync(product.ProductId, 5);
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var imported = 0;
        var channel = new RecordingChannelClient();
        channel.Behaviour = async (update, _) =>
        {
            // While the channel is being called for the initial sync, three more units are received.
            if (update.TaskId == mapping.SyncTaskId && Interlocked.Exchange(ref imported, 1) == 0)
            {
                await ImportAsync(product.ProductId, 3);
            }
        };

        await RunWorkersAsync(channel, () => WaitForTasksAsync(product.ProductId, count: 3, ProcessingTaskStatus.Succeeded));

        var pushes = channel.PushesOfTask(mapping.SyncTaskId!.Value);
        Assert.Equal([5, 8], pushes.Select(p => p.AvailableStock));
        Assert.Equal([$"{mapping.SyncTaskId}:1:1", $"{mapping.SyncTaskId}:1:2"], pushes.Select(p => p.RequestId));
        var task = (await TasksOfAsync(product.ProductId)).Single(t => t.TaskId == mapping.SyncTaskId);
        Assert.Equal(ProcessingAttemptStatus.Succeeded, Assert.Single(task.Attempts).Status);
        // The last value any task pushed is the central stock.
        Assert.Equal(8, channel.PushesOf(product.ProductId)[^1].AvailableStock);
        Assert.Equal((8, 0, 8), await StockAsync(product.ProductId));
    }

    [Fact]
    public async Task Pushes_within_one_attempt_are_bounded()
    {
        var product = await SeedProductAsync();
        await ImportAsync(product.ProductId, 5);
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var channel = new RecordingChannelClient();
        // The stock changes during every push of the initial sync.
        channel.Behaviour = async (update, _) =>
        {
            if (update.TaskId == mapping.SyncTaskId)
            {
                await ImportAsync(product.ProductId, 1);
            }
        };

        await RunWorkersAsync(
            channel,
            // The initial sync, the first import and the three imports made during the pushes.
            () => WaitForTasksAsync(product.ProductId, count: 5, ProcessingTaskStatus.Succeeded),
            settings: new Dictionary<string, string?> { ["StockSyncWorker:MaxPushesPerAttempt"] = "3" });

        Assert.Equal([5, 6, 7], channel.PushesOfTask(mapping.SyncTaskId!.Value).Select(p => p.AvailableStock));
        var task = (await TasksOfAsync(product.ProductId)).Single(t => t.TaskId == mapping.SyncTaskId);
        Assert.Equal((ProcessingTaskStatus.Succeeded, 1), (task.Status, task.Attempts.Count));
        // The value the initial sync did not push any more (8) reaches the channel through the event of that change.
        Assert.Equal((8, 0, 8), await StockAsync(product.ProductId));
        Assert.Equal(8, channel.PushesOf(product.ProductId)[^1].AvailableStock);
    }

    // ---- retry, final failure, DLQ ----------------------------------------------------------------------------

    [Fact]
    public async Task Transient_channel_failure_is_retried_with_delay_and_then_succeeds()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var taskId = mapping.SyncTaskId!.Value;
        var calls = 0;
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.TaskId == taskId && Interlocked.Increment(ref calls) == 1
                ? throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "channel down (test)", isTransient: true)
                : Task.CompletedTask
        };

        await RunWorkersAsync(channel, async () =>
        {
            // Between the failed attempt and the delayed copy (1 s tier) the task waits as RetryScheduled.
            var waiting = await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.RetryScheduled, Timeout, "task RetryScheduled");
            var failed = Assert.Single(waiting.Attempts);
            Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Unavailable, "channel down (test)"),
                (failed.Status, failed.ErrorCode, failed.ErrorMessage));

            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Succeeded, Timeout, "task Succeeded");
            await WaitForEmptyAsync(WebsiteQueue);
        });

        var task = await TaskAsync(taskId);
        var attempts = task.Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal((1, 2), (task.RetryCount, attempts.Count));
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingAttemptStatus.Succeeded), (attempts[0].Status, attempts[1].Status));
        Assert.True(attempts[1].StartedAt - attempts[0].FinishedAt!.Value >= TimeSpan.FromMilliseconds(900),
            $"gap was {attempts[1].StartedAt - attempts[0].FinishedAt}");
        Assert.Equal([$"{taskId}:1:1", $"{taskId}:2:1"], channel.PushesOfTask(taskId).Select(p => p.RequestId));
        await AssertNoDeadLettersAsync();
        await AssertDelayQueuesEmptyAsync();
    }

    [Fact]
    public async Task Rejection_by_one_channel_fails_its_task_at_once_and_leaves_the_other_channel_alone()
    {
        var product = await SeedProductAsync();
        var onWebsite = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var onPos = await MapAsync(Pos, product.ProductId, externalSku: "POS");
        var import = await ImportAsync(product.ProductId, 4);
        var change = await OutboxOfMovementAsync(import.Items[0].MovementId);
        var websiteId = await ChannelIdAsync(Website);
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.ChannelSlug == Website && update.ProductId == product.ProductId
                ? throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "unknown external product (test)", isTransient: false)
                : Task.CompletedTask
        };

        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(
                () => TasksOfAsync(product.ProductId),
                tasks => tasks.Count == 4 && tasks.All(t => t.IsTerminal),
                Timeout,
                "four stock-sync tasks finished");
            await WaitForEmptyAsync(WebsiteQueue, PosQueue);
        });

        var all = await TasksOfAsync(product.ProductId);
        var website = all.Where(t => t.ChannelId == websiteId).ToList();
        var pos = all.Where(t => t.ChannelId != websiteId).ToList();

        // Website: failed for good at the first attempt, no retry.
        Assert.Equal(2, website.Count);
        Assert.All(website, t =>
        {
            Assert.Equal((ProcessingTaskStatus.Failed, 0), (t.Status, t.RetryCount));
            var attempt = Assert.Single(t.Attempts);
            Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Rejected, "unknown external product (test)"),
                (attempt.Status, attempt.ErrorCode, attempt.ErrorMessage));
        });

        // POS: the same change was pushed - one failed task never fails another (doc §13).
        Assert.Equal(2, pos.Count);
        Assert.All(pos, t => Assert.Equal(ProcessingTaskStatus.Succeeded, t.Status));
        Assert.Equal(2, channel.PushesOf(product.ProductId).Count(p => p.ChannelSlug == Pos));
        Assert.Contains(pos, t => t.TaskId == onPos.SyncTaskId);

        // Visible: processing.failed per failed task ...
        foreach (var task in website)
        {
            var failed = Assert.Single(await ProcessingFailedLogsAsync(task.TaskId));
            var payload = JsonNode.Parse(failed.Payload!)!;
            Assert.Equal((SystemEventLevel.Error, task.CorrelationId, StockSyncMessageHandler.WorkerName, (Guid?)null),
                (failed.Level, failed.CorrelationId, failed.WorkerName, failed.OrderId));
            Assert.Equal((1, ChannelApiErrorCodes.Rejected, WebsiteQueue, "StockSync"),
                (payload["attemptNo"]!.GetValue<int>(), payload["errorCode"]!.GetValue<string>(),
                    payload["queue"]!.GetValue<string>(), payload["taskType"]!.GetValue<string>()));
        }

        // ... and both messages in the DLQ of that channel only.
        var dead = await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(WebsiteQueue), expected: 2);
        Assert.Equal(
            new[] { change.OutboxEventId.ToString(), website.Single(t => t.TaskId == onWebsite.SyncTaskId).MessageId }.Order(),
            dead.Select(m => m.BasicProperties.MessageId).Order());
        Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(PosQueue)));
        await AssertDelayQueuesEmptyAsync();
    }

    [Fact]
    public async Task Channel_that_stays_unavailable_fails_the_task_after_max_attempts_and_the_message_goes_to_the_dlq()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Pos, product.ProductId, externalSku: "POS");
        var taskId = mapping.SyncTaskId!.Value;
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.TaskId == taskId
                ? throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "still down (test)", isTransient: true)
                : Task.CompletedTask
        };

        await RunWorkersAsync(
            channel,
            async () =>
            {
                await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Failed, Timeout, "task Failed");
                await WaitForEmptyAsync(PosQueue);
            },
            settings: new Dictionary<string, string?> { ["StockSyncWorker:MaxAttempts"] = "3" });

        var task = await TaskAsync(taskId);
        Assert.Equal(3, task.Attempts.Count);
        Assert.All(task.Attempts, a => Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Unavailable), (a.Status, a.ErrorCode)));
        Assert.Equal(3, channel.PushesOfTask(taskId).Count);

        var failed = Assert.Single(await ProcessingFailedLogsAsync(taskId));
        var payload = JsonNode.Parse(failed.Payload!)!;
        Assert.Equal((3, ChannelApiErrorCodes.Unavailable, PosQueue),
            (payload["attemptNo"]!.GetValue<int>(), payload["errorCode"]!.GetValue<string>(), payload["queue"]!.GetValue<string>()));

        var dead = Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(PosQueue), expected: 1));
        Assert.Equal((task.MessageId, task.CorrelationId), (dead.BasicProperties.MessageId, dead.BasicProperties.CorrelationId));
        Assert.Equal(2, Convert.ToInt32(dead.BasicProperties.Headers![RabbitMqRetryPublisher.RetryAttemptHeader]));
        Assert.Equal(ChannelApiErrorCodes.Unavailable, Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers[RabbitMqRetryPublisher.LastErrorCodeHeader]!));
        await AssertDelayQueuesEmptyAsync();
    }

    [Fact]
    public async Task Channel_that_does_not_answer_times_out_and_is_retried()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var taskId = mapping.SyncTaskId!.Value;
        var calls = 0;
        var channel = new RecordingChannelClient
        {
            // The first call never answers; the pipeline's timeout ends it.
            Behaviour = (update, token) => update.TaskId == taskId && Interlocked.Increment(ref calls) == 1
                ? Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token)
                : Task.CompletedTask
        };

        await RunWorkersAsync(
            channel,
            () => Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Succeeded, Timeout, "task Succeeded"),
            settings: new Dictionary<string, string?> { ["StockSyncWorker:ChannelCallTimeout"] = "00:00:00.500" });

        var attempts = (await TaskAsync(taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Timeout), (attempts[0].Status, attempts[0].ErrorCode));
        Assert.Contains("did not answer within 00:00:00.5", attempts[0].ErrorMessage);
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[1].Status);
    }

    // ---- manual retry -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Manual_retry_reaches_only_the_queue_of_the_task_channel_and_gets_a_fresh_attempt_budget()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var taskId = mapping.SyncTaskId!.Value;
        var rejecting = true;
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.TaskId == taskId && Volatile.Read(ref rejecting)
                ? throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "unknown external product (test)", isTransient: false)
                : Task.CompletedTask
        };

        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Failed, Timeout, "task Failed");
            await WaitForEmptyAsync(WebsiteQueue);
        });
        var failed = await TaskAsync(taskId);
        Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(WebsiteQueue), expected: 1));

        // An operator fixes the mapping on the channel side and retries the task.
        var retry = await fixture.WithServicesAsync(services =>
            services.GetRequiredService<IProcessingTaskService>().RetryAsync(taskId, CancellationToken.None));
        Assert.Equal((taskId, ProcessingTaskStatus.Pending), (retry.TaskId, retry.Status));
        Assert.NotEqual(failed.MessageId, retry.MessageId);

        // Published without any consumer: exactly one message, in the queue of the task's channel only.
        using (var publisher = fixture.CreateHost("stock-sync-publisher", configureServices: services =>
               {
                   services.RemoveHostedService<OrderWorkerService>();
                   services.RemoveHostedService<StockSyncWorkerService>();
               }))
        {
            await publisher.StartAsync();
            try
            {
                await Eventually.WaitAsync(
                    () => fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.OutboxEventId == Guid.Parse(retry.MessageId))),
                    row => row.Status == OutboxEventStatus.Published,
                    Timeout,
                    "retry message published");
            }
            finally
            {
                await publisher.StopAsync();
            }
        }

        Assert.Equal((1u, 0u, 0u), (await MessageCountAsync(WebsiteQueue), await MessageCountAsync(PosQueue), await MessageCountAsync(MarketplaceQueue)));
        await using (var peek = await fixture.OpenChannelAsync())
        {
            var message = await peek.BasicGetAsync(WebsiteQueue, autoAck: false);
            Assert.Equal((retry.MessageId, EventTypes.StockSyncRequested, WebsiteQueue),
                (message!.BasicProperties.MessageId, message.BasicProperties.Type, message.RoutingKey));
            await peek.BasicNackAsync(message.DeliveryTag, multiple: false, requeue: true);
        }

        Volatile.Write(ref rejecting, false);
        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Succeeded, Timeout, "task Succeeded");
            await WaitForEmptyAsync(WebsiteQueue);
        });

        var done = await TaskAsync(taskId);
        var attempts = done.Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal([1, 2], attempts.Select(a => a.AttemptNo));
        Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Rejected, ProcessingAttemptStatus.Succeeded),
            (attempts[0].Status, attempts[0].ErrorCode, attempts[1].Status));
        Assert.Equal((retry.MessageId, 1), (done.MessageId, done.AttemptBudgetStart));
        Assert.Equal([$"{taskId}:1:1", $"{taskId}:2:1"], channel.PushesOfTask(taskId).Select(p => p.RequestId));

        // Only this task: the retry created no task on any channel.
        Assert.Single(await TasksOfAsync(product.ProductId));
        var audit = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(l => l.EventType == ProcessingEvents.RetryRequested && l.TaskId == taskId));
        Assert.Equal((retry.MessageId, failed.MessageId),
            (JsonNode.Parse(audit.Payload!)!["newMessageId"]!.GetValue<string>(), JsonNode.Parse(audit.Payload!)!["previousMessageId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Manual_retry_gives_max_attempts_new_attempts()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var taskId = mapping.SyncTaskId!.Value;
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.TaskId == taskId
                ? throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "still down (test)", isTransient: true)
                : Task.CompletedTask
        };
        var settings = new Dictionary<string, string?> { ["StockSyncWorker:MaxAttempts"] = "2" };

        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Failed, Timeout, "task Failed");

            await fixture.WithServicesAsync(services =>
                services.GetRequiredService<IProcessingTaskService>().RetryAsync(taskId, CancellationToken.None));

            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Failed && t.Attempts.Count > 2, Timeout, "task Failed again");
            await WaitForEmptyAsync(WebsiteQueue);
        }, settings: settings);

        var task = await TaskAsync(taskId);
        Assert.Equal([1, 2, 3, 4], task.Attempts.Select(a => a.AttemptNo).Order());
        Assert.Equal(2, task.AttemptBudgetStart);
        Assert.Equal([2, 4], (await ProcessingFailedLogsAsync(taskId)).OrderBy(l => l.LogId).Select(l => JsonNode.Parse(l.Payload!)!["attemptNo"]!.GetValue<int>()));
    }

    // ---- poison -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Poison_messages_go_to_the_dlq_of_their_queue_and_nothing_is_pushed()
    {
        var product = await SeedProductAsync();
        var onPos = await MapAsync(Pos, product.ProductId, externalSku: "POS");
        var (posId, websiteId) = (await ChannelIdAsync(Pos), await ChannelIdAsync(Website));
        var orderTask = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, $"corr-{Guid.NewGuid():N}", null);
        await fixture.WithDbAsync(async db =>
        {
            db.ProcessingTasks.Add(orderTask);
            return await db.SaveChangesAsync();
        });

        var unknownTask = Guid.CreateVersion7();
        var (json, type, unknown, order, otherChannel) = (
            $"poison-json-{Guid.NewGuid():N}", $"poison-type-{Guid.NewGuid():N}", $"poison-unknown-{Guid.NewGuid():N}",
            $"poison-order-{Guid.NewGuid():N}", $"poison-channel-{Guid.NewGuid():N}");
        var poison = new Dictionary<string, byte[]>
        {
            // Not JSON at all.
            [json] = Encoding.UTF8.GetBytes("{ not json"),
            // An event this queue does not handle.
            [type] = Directed(EventTypes.OrderCreated, unknownTask, websiteId, product.ProductId),
            // A task that does not exist.
            [unknown] = Directed(EventTypes.StockSyncRequested, unknownTask, websiteId, product.ProductId),
            // A task of another type.
            [order] = Directed(EventTypes.StockSyncRequested, orderTask.TaskId, websiteId, product.ProductId),
            // The POS task, sent to the website queue.
            [otherChannel] = Directed(EventTypes.StockSyncRequested, onPos.SyncTaskId!.Value, posId, product.ProductId)
        };
        foreach (var (messageId, body) in poison)
        {
            await fixture.PublishAsync(WebsiteQueue, Rewrite(body, messageId), messageId);
        }

        var channel = new RecordingChannelClient();
        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(
                () => MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(WebsiteQueue)), count => count == (uint)poison.Count, Timeout, "poison in stock.sync.website.dlq");
            // The POS task itself is fine and runs on its own queue.
            await Eventually.WaitAsync(() => TaskAsync(onPos.SyncTaskId.Value), t => t.Status == ProcessingTaskStatus.Succeeded, Timeout, "pos task Succeeded");
            await WaitForEmptyAsync(WebsiteQueue, PosQueue);
        });

        var dead = await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(WebsiteQueue), expected: poison.Count);
        Assert.Equal(poison.Keys.Order(), dead.Select(m => m.BasicProperties.MessageId).Order());
        Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(PosQueue)));

        var rejected = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == StockSyncEvents.MessageRejected)
            .ToListAsync());
        var mine = rejected.Where(l => poison.ContainsKey(JsonNode.Parse(l.Payload!)!["messageId"]?.GetValue<string>() ?? string.Empty)).ToList();
        Assert.Equal(poison.Count, mine.Count);
        Assert.All(mine, l =>
        {
            Assert.Equal((SystemEventLevel.Error, StockSyncMessageHandler.WorkerName), (l.Level, l.WorkerName));
            Assert.Equal(WebsiteQueue, JsonNode.Parse(l.Payload!)!["queue"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(JsonNode.Parse(l.Payload!)!["reason"]?.GetValue<string>()));
        });
        // Stored: the code of each rejection next to its text (task 019 A2).
        Assert.Equal(
            new Dictionary<string, string>
            {
                [json] = MessageRejectionCodes.InvalidJson,
                [type] = MessageRejectionCodes.UnexpectedEventType,
                [unknown] = MessageRejectionCodes.UnknownTask,
                [order] = MessageRejectionCodes.TaskMismatch,
                [otherChannel] = MessageRejectionCodes.TaskOfOtherChannel
            },
            mine.Select(l => JsonNode.Parse(l.Payload!)!).ToDictionary(p => p["messageId"]!.GetValue<string>(), p => p["reasonCode"]!.GetValue<string>()));

        // Nothing was pushed for a poison message: the only push is the POS task's own, on POS.
        var push = Assert.Single(channel.PushesOf(product.ProductId));
        Assert.Equal((Pos, onPos.SyncTaskId.Value), (push.ChannelSlug, push.TaskId));
        Assert.Single((await TaskAsync(onPos.SyncTaskId.Value)).Attempts);
        Assert.Empty((await TaskAsync(orderTask.TaskId)).Attempts);
        Assert.False(await fixture.WithDbAsync(db => db.ProcessingAttempts.AnyAsync(a => a.TaskId == unknownTask)));
    }

    // ---- shutdown ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Host_stopping_during_a_push_cancels_it_and_the_message_is_redelivered()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: "WEB");
        var taskId = mapping.SyncTaskId!.Value;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new RecordingChannelClient
        {
            Behaviour = async (update, token) =>
            {
                if (update.TaskId != taskId)
                {
                    return;
                }

                started.TrySetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
        };

        // A long call timeout: only the host stopping can end this push.
        var host = StartWorkers(slow, new Dictionary<string, string?> { ["StockSyncWorker:ChannelCallTimeout"] = "00:02:00" });
        try
        {
            await started.Task.WaitAsync(Timeout);
        }
        finally
        {
            await StopAsync(host);
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Not acknowledged, not failed: the attempt is still Running and the message is back in the queue.
        var interrupted = await TaskAsync(taskId);
        Assert.Equal(ProcessingTaskStatus.InProgress, interrupted.Status);
        Assert.Equal(ProcessingAttemptStatus.Running, Assert.Single(interrupted.Attempts).Status);
        await Eventually.WaitAsync(() => MessageCountAsync(WebsiteQueue), count => count == 1, Timeout, "message back in stock.sync.website");
        Assert.Empty(await ProcessingFailedLogsAsync(taskId));

        var channel = new RecordingChannelClient();
        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Succeeded, Timeout, "task Succeeded");
            await WaitForEmptyAsync(WebsiteQueue);
        });

        var attempts = (await TaskAsync(taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingAttempt.AbandonedErrorCode), (attempts[0].Status, attempts[0].ErrorCode));
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[1].Status);
        Assert.Equal($"{taskId}:2:1", Assert.Single(channel.PushesOfTask(taskId)).RequestId);
        await AssertNoDeadLettersAsync();
    }

    // ---- topology ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Topology_of_tasks_005_and_007_upgrades_to_the_directed_bindings_without_mismatch()
    {
        var virtualHost = $"sync-{Guid.NewGuid():N}"[..20];
        await PutManagementAsync($"api/vhosts/{virtualHost}", new { });
        await PutManagementAsync($"api/permissions/{virtualHost}/{WorkersFixture.RabbitUser}", new { configure = ".*", write = ".*", read = ".*" });
        var vhost = Uri.EscapeDataString(virtualHost);

        await using var connection = await fixture.ConnectAsync(virtualHost);
        await using (var channel = await connection.CreateChannelAsync())
        {
            // What tasks 005 and 007 declared, written out literally (not through the current definitions).
            await channel.ExchangeDeclareAsync("omnichannel.topic", ExchangeType.Topic, durable: true);
            await channel.ExchangeDeclareAsync("omnichannel.dlx", ExchangeType.Direct, durable: true);
            await channel.ExchangeDeclareAsync("omnichannel.retry", ExchangeType.Direct, durable: true);
            foreach (var (queue, routingKey) in new[]
                     {
                         ("order.processing", "order.created"),
                         ("stock.sync.website", "inventory.changed"),
                         ("stock.sync.pos", "inventory.changed"),
                         ("stock.sync.marketplace", "inventory.changed"),
                         ("invoice.processing", "invoice.requested"),
                         ("notification.email", "email.requested")
                     })
            {
                await channel.QueueDeclareAsync($"{queue}.dlq", true, false, false, new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
                await channel.QueueBindAsync($"{queue}.dlq", "omnichannel.dlx", $"{queue}.dlq");
                await channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = "omnichannel.dlx",
                    ["x-dead-letter-routing-key"] = $"{queue}.dlq",
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                    ["x-delivery-limit"] = 10
                });
                await channel.QueueBindAsync(queue, "omnichannel.topic", routingKey);
                await channel.QueueBindAsync(queue, "omnichannel.retry", queue);
            }

            foreach (var (name, ttl) in new[] { ("omnichannel.delay.5s", 5_000), ("omnichannel.delay.30s", 30_000), ("omnichannel.delay.2m", 120_000), ("omnichannel.delay.10m", 600_000) })
            {
                await channel.ExchangeDeclareAsync(name, ExchangeType.Fanout, durable: true);
                await channel.QueueDeclareAsync(name, true, false, false, new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = ttl,
                    ["x-dead-letter-exchange"] = "omnichannel.retry",
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish"
                });
                await channel.QueueBindAsync(name, name, string.Empty);
            }

            // A message waiting from before the upgrade (the development broker holds such a backlog).
            await channel.BasicPublishAsync("omnichannel.topic", "inventory.changed", Encoding.UTF8.GetBytes("{}"));

            // The current topology, twice: no PRECONDITION_FAILED on any existing queue.
            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);

            // A directed message reaches the queue of its channel only; inventory.changed still reaches all three.
            await channel.BasicPublishAsync("omnichannel.topic", "stock.sync.pos", Encoding.UTF8.GetBytes("{}"));
            await Eventually.WaitAsync(
                async () => ((await channel.QueueDeclarePassiveAsync("stock.sync.website")).MessageCount,
                    (await channel.QueueDeclarePassiveAsync("stock.sync.pos")).MessageCount,
                    (await channel.QueueDeclarePassiveAsync("stock.sync.marketplace")).MessageCount),
                counts => counts == (1u, 2u, 1u),
                Timeout,
                "backlog kept, directed message in stock.sync.pos only");
        }

        foreach (var (slug, queue) in RabbitMqTopology.StockSyncQueues)
        {
            var bindings = (await GetManagementAsync($"api/queues/{vhost}/{queue}/bindings")).AsArray();
            Assert.Contains(bindings, b => b!["source"]!.GetValue<string>() == "omnichannel.topic" && b["routing_key"]!.GetValue<string>() == "inventory.changed");
            Assert.Contains(bindings, b => b!["source"]!.GetValue<string>() == "omnichannel.topic" && b["routing_key"]!.GetValue<string>() == queue);
            Assert.Contains(bindings, b => b!["source"]!.GetValue<string>() == "omnichannel.retry" && b["routing_key"]!.GetValue<string>() == queue);
            Assert.False(string.IsNullOrEmpty(slug));
        }

        // Queues without stock sync got no extra binding.
        var orderBindings = (await GetManagementAsync($"api/queues/{vhost}/order.processing/bindings")).AsArray();
        Assert.Equal(1, orderBindings.Count(b => b!["source"]!.GetValue<string>() == "omnichannel.topic"));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<Product> SeedProductAsync()
    {
        var product = new Product($"SS-{Guid.NewGuid():N}"[..20], "Stock sync product", 3m, 0, null);
        await fixture.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 0));
            return await db.SaveChangesAsync();
        });
        return product;
    }

    private Task<ImportInventoryResponse> ImportAsync(Guid productId, int stock) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IInventoryImportService>().ImportAsync(
            new ImportInventoryRequest($"GRN-{Guid.NewGuid():N}", null, [new ImportInventoryItem(productId, stock)]),
            CancellationToken.None));

    private Task<Guid> ChannelIdAsync(string slug) =>
        fixture.WithDbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == slug).Select(c => c.ChannelId).SingleAsync());

    /// <summary>A mapping exactly as the API creates it (mapping + initial sync task + directed outbox row).</summary>
    private async Task<ChannelProductSavedResponse> MapAsync(string slug, Guid productId, string? externalProductId = null, string? externalSku = null)
    {
        var channelId = await ChannelIdAsync(slug);
        var mapping = await fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            channelId, new CreateChannelProductRequest(productId, externalProductId, externalSku), CancellationToken.None));
        Assert.NotNull(mapping.SyncTaskId);
        return mapping;
    }

    private async Task UpdateMappingAsync(string slug, Guid channelProductId, UpdateChannelProductRequest request)
    {
        var channelId = await ChannelIdAsync(slug);
        await fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().UpdateProductAsync(
            channelId, channelProductId, request, CancellationToken.None));
    }

    private IHost StartWorkers(RecordingChannelClient channel, IDictionary<string, string?>? settings = null, CapturingLoggerProvider? logs = null)
    {
        // The breaker has its own unit tests; here it must not interfere with scripted failures.
        var overrides = new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
        {
            ["StockSyncWorker:CircuitBreaker:MinimumThroughput"] = "1000"
        };
        var host = fixture.CreateHost($"stock-sync-{Guid.NewGuid():N}"[..24], overrides, services =>
        {
            // Orders are not part of these tests; leftovers of other tests in the shared database stay untouched.
            services.RemoveHostedService<OrderWorkerService>();
            services.UseChannelAdapter(channel);
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private async Task RunWorkersAsync(
        RecordingChannelClient channel,
        Func<Task> until,
        IDictionary<string, string?>? settings = null,
        CapturingLoggerProvider? logs = null)
    {
        var host = StartWorkers(channel, settings, logs);
        try
        {
            await until();
        }
        finally
        {
            await StopAsync(host);
        }
    }

    private Task<List<ProcessingTask>> TasksOfAsync(Guid productId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).Where(t => t.ProductId == productId).ToListAsync());

    private Task<ProcessingTask> TaskAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    private async Task WaitForTasksAsync(Guid productId, int count, ProcessingTaskStatus status)
    {
        try
        {
            await Eventually.WaitAsync(
                () => TasksOfAsync(productId),
                tasks => tasks.Count == count && tasks.All(t => t.Status == status),
                Timeout,
                $"{count} stock-sync tasks {status}");
        }
        catch (TimeoutException timeout)
        {
            var tasks = await TasksOfAsync(productId);
            var seen = tasks.Select(t => $"{t.Status}[{string.Join(",", t.Attempts.OrderBy(a => a.AttemptNo).Select(a => a.ErrorCode ?? a.Status.ToString()))}]");
            throw new TimeoutException($"{timeout.Message} Tasks: {tasks.Count}: {string.Join(" ", seen)}", timeout);
        }
    }

    private Task<OutboxEvent> OutboxOfMovementAsync(Guid movementId) =>
        fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.InventoryMovementId == movementId));

    private Task<(int Total, int Reserved, int Available)> StockAsync(Guid productId) =>
        fixture.WithDbAsync(async db =>
        {
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            return (inventory.TotalStock, inventory.ReservedStock, inventory.AvailableStock);
        });

    private Task<List<SystemEventLog>> ProcessingFailedLogsAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == ProcessingEvents.Failed && l.TaskId == taskId)
            .ToListAsync());

    private async Task<uint> MessageCountAsync(string queue)
    {
        await using var channel = await fixture.OpenChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    /// <summary>The consumers settle a message right after their database commit; wait until the queues are drained.</summary>
    private async Task WaitForEmptyAsync(params string[] queues)
    {
        foreach (var queue in queues)
        {
            await Eventually.WaitAsync(() => MessageCountAsync(queue), count => count == 0, Timeout, $"{queue} drained");
        }
    }

    private async Task<List<BasicGetResult>> DrainAsync(string queue, int expected)
    {
        await Eventually.WaitAsync(() => MessageCountAsync(queue), count => count >= (uint)expected, Timeout, $"{expected} messages in {queue}");

        await using var channel = await fixture.OpenChannelAsync();
        var messages = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            messages.Add(message);
        }

        return messages;
    }

    private async Task AssertNoDeadLettersAsync()
    {
        foreach (var queue in RabbitMqTopology.StockSyncQueues.Values)
        {
            Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(queue)));
        }
    }

    private async Task AssertDelayQueuesEmptyAsync()
    {
        foreach (var delayQueue in WorkersFixture.DelayQueues)
        {
            Assert.Equal(0u, await MessageCountAsync(delayQueue));
        }
    }

    private static byte[] Directed(string eventType, Guid taskId, Guid channelId, Guid productId) =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            "placeholder",
            $"corr-{Guid.NewGuid():N}",
            taskId,
            eventType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new StockSyncRequestedPayload(taskId, channelId, productId), MessageJson.Options)));

    /// <summary>Gives a JSON envelope the message id it is published with; other bodies are returned unchanged.</summary>
    private static byte[] Rewrite(byte[] body, string messageId)
    {
        try
        {
            var node = JsonNode.Parse(body)!;
            node["messageId"] = messageId;
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private async Task<JsonNode> GetManagementAsync(string path) =>
        await Eventually.WaitAsync(
            async () =>
            {
                using var response = await fixture.Management.GetAsync(path);
                return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
            },
            node => node is not null,
            Timeout,
            $"management API {path}") ?? throw new InvalidOperationException();

    private async Task PutManagementAsync(string path, object body)
    {
        using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(fixture.Management, path, body);
        Assert.True(response.IsSuccessStatusCode, $"PUT {path} returned {(int)response.StatusCode}");
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Channels;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.ReconciliationWorker;
using Omnichannel.Workers.StockSyncWorker;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 022 Part C: reconciliation runs against real PostgreSQL. Every test has a database of its own (a run looks at
/// every mapping there is): the three seeded channels - website, pos, digital-platform - and what the test adds. The
/// channel adapter is the recording double unless a test starts the Workers (<c>ReconciliationTests.EndToEnd.cs</c>).
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class ReconciliationTests(WorkersFixture fixture) : IAsyncLifetime
{
    private const string Website = "website";
    private const string Pos = "pos";
    private const string Marketplace = "digital-platform";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly RecordingChannelClient _channel = new();
    private string _database = null!;
    private IHost _tools = null!;

    public async Task InitializeAsync()
    {
        await fixture.PurgeQueuesAsync();
        _database = await fixture.CreateIsolatedDatabaseAsync();
        // Not started: only its services are used.
        _tools = fixture.CreateHost("reconciliation-tools", configureServices: s => s.UseChannelAdapter(_channel), databaseConnectionString: _database);
    }

    public async Task DisposeAsync()
    {
        _tools.Dispose();
        await fixture.PurgeQueuesAsync();
    }

    // ---- in sync, drift, no value ---------------------------------------------------------------------------------

    [Fact]
    public async Task Pairs_that_are_in_sync_get_no_task_and_a_clean_run_writes_nothing()
    {
        await MappedAsync(Website, centralStock: 7, channelStock: 7);
        await MappedAsync(Pos, centralStock: 0, channelStock: 0);
        await MappedAsync(Marketplace, centralStock: 12, channelStock: 12);

        var result = await RunAsync();

        Assert.Equal((true, 3, 3, 0, 0, 0, 0, 0), Counts(result));
        Assert.Empty(result.Unreadable);
        Assert.False(result.IsNoteworthy);
        Assert.Equal((0, 0, 0), await WrittenAsync());
        // Every synced channel was asked once, for its one product.
        Assert.Equal([Marketplace, Pos, Website], _channel.Reads.Select(r => r.ChannelSlug).Order());
        Assert.All(_channel.Reads, r => Assert.Single(r.Items));
        Assert.Empty(_channel.Pushes);
    }

    [Fact]
    public async Task A_differing_value_creates_exactly_one_pending_task_per_pair_with_a_directed_message_and_one_event()
    {
        var more = await MappedAsync(Website, centralStock: 7, channelStock: 9, externalProductId: "WEB-1");
        var less = await MappedAsync(Website, centralStock: 7, channelStock: 2);
        var onPos = await MappedAsync(Pos, centralStock: 4, channelStock: 5);
        var inSync = await MappedAsync(Marketplace, centralStock: 3, channelStock: 3);
        var (websiteId, posId) = (await ChannelIdAsync(Website), await ChannelIdAsync(Pos));

        var result = await RunAsync();

        Assert.Equal((true, 4, 1, 3, 3, 0, 0, 0), Counts(result));
        Assert.Equal(0, result.FailedRetried);
        var correlationId = ReconciliationIds.CorrelationIdOf(result.RunId);

        // One task per drifted pair - nothing for the pair in sync - as every other stock-sync task: Pending, no order,
        // channel and product set; traceable by the run's correlation id; one per pair and run by its key.
        var tasks = await DbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).ToListAsync());
        Assert.Equal(3, tasks.Count);
        Assert.Equal(
            new[] { (websiteId, more.ProductId), (websiteId, less.ProductId), (posId, onPos.ProductId) }.Order(),
            tasks.Select(t => (t.ChannelId!.Value, t.ProductId!.Value)).Order());
        Assert.DoesNotContain(tasks, t => t.ProductId == inSync.ProductId);
        Assert.All(tasks, t =>
        {
            Assert.Equal(
                (ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, correlationId, (Guid?)null, 0),
                (t.TaskType, t.Status, t.CorrelationId, t.OrderId, t.RetryCount));
            Assert.Equal(ReconciliationIds.IdempotencyKeyOf(result.RunId, t.ChannelId!.Value, t.ProductId!.Value), t.IdempotencyKey);
            Assert.Empty(t.Attempts);
        });

        // One directed message per task: stock.sync.requested to the queue of the task's channel only.
        var messages = await DbAsync(db => db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal(3, messages.Count);
        Assert.All(tasks, task =>
        {
            var message = Assert.Single(messages, m => m.OutboxEventId.ToString() == task.MessageId);
            var queue = task.ChannelId == websiteId ? RabbitMqTopology.StockSyncWebsiteQueue : RabbitMqTopology.StockSyncPosQueue;
            Assert.Equal(
                (EventTypes.StockSyncRequested, queue, queue, task.TaskId.ToString(), correlationId, OutboxEventStatus.Pending),
                (message.EventType, message.RoutingKey, message.EffectiveRoutingKey, message.AggregateId, message.CorrelationId, message.Status));
            Assert.Equal(
                new StockSyncRequestedPayload(task.TaskId, task.ChannelId!.Value, task.ProductId!.Value),
                MessageJson.Deserialize<StockSyncRequestedPayload>(message.Payload));
            Assert.Null(message.InventoryMovementId);
        });

        // Exactly one event, with the run's counts.
        var written = Assert.Single(await EventsAsync());
        Assert.Equal(
            (SystemEventLevel.Warning, ReconciliationEvents.Completed, correlationId, ReconciliationService.WorkerName, (Guid?)null, (Guid?)null),
            (written.Level, written.EventType, written.CorrelationId, written.WorkerName, written.TaskId, written.OrderId));
        using var payload = JsonDocument.Parse(written.Payload!);
        // Exactly the fields of the allowlist (jsonb keeps no key order).
        Assert.Equal(
            Omnichannel.Application.SystemEvents.SystemEventDetails.AllowedFields[ReconciliationEvents.Completed].Order(),
            payload.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(result.RunId, payload.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(
            (4, 1, 3, 3, 0, 0, 0, 0),
            (Number(payload, "checked"), Number(payload, "inSync"), Number(payload, "drifted"), Number(payload, "tasksCreated"),
                Number(payload, "deferred"), Number(payload, "failedRetried"), Number(payload, "skippedUnfinished"),
                Number(payload, "skippedRejected")));
        Assert.Equal(0, payload.RootElement.GetProperty("unreadable").GetArrayLength());

        // The channel was asked with the ids of the mapping, and nothing was pushed by the run itself.
        var asked = _channel.ReadsOf(Website).SelectMany(r => r.Items).Single(i => i.ProductId == more.ProductId);
        Assert.Equal((more.Sku, "WEB-1", $"EXT-{more.Sku}"), (asked.Sku, asked.ExternalProductId, asked.ExternalSku));
        Assert.Empty(_channel.Pushes);
    }

    [Fact]
    public async Task A_channel_that_holds_no_value_for_a_product_is_drift_also_when_the_central_stock_is_zero()
    {
        var never = await MappedAsync(Website, centralStock: 5, channelStock: null);
        var neverAndEmpty = await MappedAsync(Website, centralStock: 0, channelStock: null);
        var zeroBoth = await MappedAsync(Website, centralStock: 0, channelStock: 0);

        var result = await RunAsync();

        Assert.Equal((true, 3, 1, 2, 2, 0, 0, 0), Counts(result));
        Assert.Equal(
            new[] { never.ProductId, neverAndEmpty.ProductId }.Order(),
            (await ReconciliationTasksAsync()).Select(t => t.ProductId!.Value).Order());
        Assert.DoesNotContain(await ReconciliationTasksAsync(), t => t.ProductId == zeroBoth.ProductId);
    }

    [Fact]
    public async Task A_product_the_channel_does_not_answer_for_counts_as_no_value()
    {
        var answered = await MappedAsync(Website, centralStock: 5, channelStock: 5);
        var leftOut = await MappedAsync(Website, centralStock: 5, channelStock: 5);
        // An adapter that returns fewer readings than items (and one nobody asked for).
        _channel.ReadBehaviour = (query, _) => Task.FromResult<IReadOnlyList<ChannelStockReading>>(
            [new ChannelStockReading(answered.ProductId, 5), new ChannelStockReading(Guid.NewGuid(), 1)]);

        var result = await RunAsync();

        Assert.Equal((true, 2, 1, 1, 1, 0, 0, 0), Counts(result));
        Assert.Equal(leftOut.ProductId, Assert.Single(await ReconciliationTasksAsync()).ProductId);
    }

    // ---- skip rules -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProcessingTaskStatus.Pending)]
    [InlineData(ProcessingTaskStatus.InProgress)]
    [InlineData(ProcessingTaskStatus.RetryScheduled)]
    public async Task A_pair_with_an_unfinished_sync_gets_no_task_and_the_channel_is_not_asked_for_it(ProcessingTaskStatus unfinished)
    {
        var waiting = await MappedAsync(Website, centralStock: 7, channelStock: 1);
        var other = await MappedAsync(Website, centralStock: 7, channelStock: 1);
        var existing = await TaskAsync(Website, waiting, unfinished);
        // An unfinished task of the same product on another channel says nothing about this one.
        await MapAsync(Pos, other);
        _channel.SetStock(Pos, other.ProductId, 7);
        await TaskAsync(Pos, other, unfinished);

        var result = await RunAsync();

        Assert.Equal((true, 1, 0, 1, 1, 0, 2, 0), Counts(result));
        var created = Assert.Single(await ReconciliationTasksAsync());
        Assert.Equal((await ChannelIdAsync(Website), other.ProductId), (created.ChannelId!.Value, created.ProductId!.Value));
        Assert.DoesNotContain(_channel.Reads.SelectMany(r => r.Items), i => i.ProductId == waiting.ProductId);
        Assert.Empty(_channel.ReadsOf(Pos));
        Assert.Equal(unfinished, (await DbAsync(db => db.ProcessingTasks.AsNoTracking().SingleAsync(t => t.TaskId == existing.TaskId))).Status);
    }

    [Fact]
    public async Task A_pair_whose_newest_finished_sync_was_rejected_by_the_channel_gets_no_task()
    {
        var rejected = await MappedAsync(Website, centralStock: 7, channelStock: null);
        await TaskAsync(Website, rejected, ProcessingTaskStatus.Succeeded);
        await TaskAsync(Website, rejected, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected);
        // Rejected only after a transient failure of an earlier attempt: the last attempt decides.
        var rejectedLater = await MappedAsync(Website, centralStock: 7, channelStock: null);
        await TaskAsync(Website, rejectedLater, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected, earlierAttempt: ChannelApiErrorCodes.Unavailable);

        var result = await RunAsync();

        Assert.Equal((true, 0, 0, 0, 0, 0, 0, 2), Counts(result));
        Assert.Empty(await ReconciliationTasksAsync());
        Assert.Empty(_channel.Reads);
        // Nothing to report: the pairs have their alert, the run did nothing.
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task A_rejection_that_is_not_the_newest_finished_sync_does_not_hide_the_pair()
    {
        // The mapping was corrected: a later sync succeeded, then the channel lost the value.
        var corrected = await MappedAsync(Website, centralStock: 7, channelStock: null);
        await TaskAsync(Website, corrected, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected);
        await TaskAsync(Website, corrected, ProcessingTaskStatus.Succeeded);
        // Rejected on this channel only.
        var elsewhere = await MappedAsync(Website, centralStock: 7, channelStock: null);
        await MapAsync(Pos, elsewhere);
        await TaskAsync(Pos, elsewhere, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected);

        var result = await RunAsync();

        Assert.Equal((true, 2, 0, 2, 2, 0, 0, 1), Counts(result));
        Assert.Equal(
            new[] { corrected.ProductId, elsewhere.ProductId }.Order(),
            (await ReconciliationTasksAsync()).Select(t => t.ProductId!.Value).Order());
        var websiteId = await ChannelIdAsync(Website);
        Assert.All(await ReconciliationTasksAsync(), t => Assert.Equal(websiteId, t.ChannelId));
    }

    [Theory]
    [InlineData(ChannelApiErrorCodes.CircuitOpen)]
    [InlineData(ChannelApiErrorCodes.Timeout)]
    [InlineData(ChannelApiErrorCodes.Unavailable)]
    [InlineData(ChannelApiErrorCodes.RateLimited)]
    [InlineData("database_unavailable")]
    public async Task A_pair_whose_newest_sync_failed_for_good_with_a_transient_code_gets_a_task(string errorCode)
    {
        // The long outage of log 014: the task ran out of attempts while the channel was down.
        var product = await MappedAsync(Pos, centralStock: 7, channelStock: null);
        await TaskAsync(Pos, product, ProcessingTaskStatus.Succeeded);
        var failed = await TaskAsync(Pos, product, ProcessingTaskStatus.Failed, errorCode);
        // Rejected first, then retried by an operator and failed for good on the open circuit: again the last attempt decides.
        var retried = await MappedAsync(Pos, centralStock: 7, channelStock: null);
        await TaskAsync(Pos, retried, ProcessingTaskStatus.Failed, errorCode, earlierAttempt: ChannelApiErrorCodes.Rejected);

        var result = await RunAsync();

        Assert.Equal((true, 2, 0, 2, 2, 0, 0, 0), Counts(result));
        var created = await ReconciliationTasksAsync();
        Assert.Equal(new[] { product.ProductId, retried.ProductId }.Order(), created.Select(t => t.ProductId!.Value).Order());
        // A new task: the failed one stays as it is (its history, its DLQ copy).
        Assert.DoesNotContain(created, t => t.TaskId == failed.TaskId);
        Assert.Equal(ProcessingTaskStatus.Failed, (await DbAsync(db => db.ProcessingTasks.AsNoTracking().SingleAsync(t => t.TaskId == failed.TaskId))).Status);
        // They differ: tasks for drift, not repeated failures.
        Assert.Equal(0, result.FailedRetried);
    }

    // ---- failed syncs of pairs that are in sync (task 023 A2) -----------------------------------------------------

    [Theory]
    [InlineData(ChannelApiErrorCodes.CircuitOpen)]
    [InlineData(ChannelApiErrorCodes.Timeout)]
    [InlineData(ChannelApiErrorCodes.Unavailable)]
    [InlineData(ChannelApiErrorCodes.RateLimited)]
    [InlineData("db_unavailable")]
    public async Task A_pair_in_sync_whose_newest_sync_failed_with_a_transient_code_gets_one_task(string errorCode)
    {
        // The push landed before the failure was recorded (or the stock came back to the pushed value): the channel
        // shows the central value, and the pair's alert would stay open for good.
        var product = await MappedAsync(Pos, centralStock: 7, channelStock: 7);
        await TaskAsync(Pos, product, ProcessingTaskStatus.Succeeded);
        var failed = await TaskAsync(Pos, product, ProcessingTaskStatus.Failed, errorCode);
        // The control next to it: in sync and nothing failed.
        var fine = await MappedAsync(Pos, centralStock: 7, channelStock: 7);
        await TaskAsync(Pos, fine, ProcessingTaskStatus.Succeeded);

        var result = await RunAsync();

        // Both are in sync - no drift - and one task was created all the same.
        Assert.Equal((true, 2, 2, 0, 1, 0, 0, 0), Counts(result));
        Assert.Equal(1, result.FailedRetried);
        Assert.True(result.IsNoteworthy);
        var created = Assert.Single(await ReconciliationTasksAsync());
        Assert.Equal(
            (await ChannelIdAsync(Pos), product.ProductId, ProcessingTaskStatus.Pending, ReconciliationIds.CorrelationIdOf(result.RunId)),
            (created.ChannelId!.Value, created.ProductId!.Value, created.Status, created.CorrelationId));
        Assert.NotEqual(failed.TaskId, created.TaskId);
        Assert.Equal(RabbitMqTopology.StockSyncPosQueue, (await DbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync())).RoutingKey);

        // It is a created task: the run writes its event, with the counter.
        using var payload = JsonDocument.Parse(Assert.Single(await EventsAsync()).Payload!);
        Assert.Equal(
            (2, 2, 0, 1, 0, 1),
            (Number(payload, "checked"), Number(payload, "inSync"), Number(payload, "drifted"), Number(payload, "tasksCreated"),
                Number(payload, "deferred"), Number(payload, "failedRetried")));
        // The run opened or closed no alert, and pushed nothing itself.
        Assert.Equal(0, await DbAsync(db => db.Alerts.CountAsync()));
        Assert.Empty(_channel.Pushes);

        // The task is unfinished now: the next run leaves the pair to it.
        var next = await RunAsync();
        Assert.Equal((true, 1, 1, 0, 0, 0, 1, 0), Counts(next));
        Assert.Equal(0, next.FailedRetried);
        Assert.Single(await ReconciliationTasksAsync());
    }

    [Fact]
    public async Task A_pair_in_sync_whose_newest_sync_was_rejected_or_succeeded_gets_no_task()
    {
        // Rejected by the channel: skipped as before, in sync or not (the channel is not even asked).
        var rejected = await MappedAsync(Website, centralStock: 7, channelStock: 7);
        await TaskAsync(Website, rejected, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected);
        // A failure that is not the newest finished sync: a later task succeeded, nothing is open.
        var recovered = await MappedAsync(Website, centralStock: 7, channelStock: 7);
        await TaskAsync(Website, recovered, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.CircuitOpen);
        await TaskAsync(Website, recovered, ProcessingTaskStatus.Succeeded);
        // Failed on another channel only.
        var elsewhere = await MappedAsync(Website, centralStock: 7, channelStock: 7);
        await MapAsync(Pos, elsewhere);
        _channel.SetStock(Pos, elsewhere.ProductId, 7);
        await TaskAsync(Pos, elsewhere, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected);
        // No sync has ever finished.
        await MappedAsync(Website, centralStock: 7, channelStock: 7);

        var result = await RunAsync();

        Assert.Equal((true, 3, 3, 0, 0, 0, 0, 2), Counts(result));
        Assert.Equal(0, result.FailedRetried);
        Assert.False(result.IsNoteworthy);
        Assert.Empty(await ReconciliationTasksAsync());
        Assert.Empty(await EventsAsync());
        Assert.DoesNotContain(_channel.Reads.SelectMany(r => r.Items), i => i.ProductId == rejected.ProductId);
    }

    [Fact]
    public async Task Repeated_failed_syncs_count_toward_the_cap_after_the_drifted_pairs_of_their_batch()
    {
        // pos is looked at before website (by slug). On pos: two pairs in sync with a failed sync. On website, in one
        // batch: two of those and two drifted pairs.
        var repeatOnPos = new List<Product>();
        for (var n = 0; n < 2; n++)
        {
            repeatOnPos.Add(await MappedAsync(Pos, centralStock: 3, channelStock: 3));
            await TaskAsync(Pos, repeatOnPos[n], ProcessingTaskStatus.Failed, ChannelApiErrorCodes.CircuitOpen);
        }

        var repeatOnWebsite = new List<Product>();
        var driftedOnWebsite = new List<Product>();
        for (var n = 0; n < 2; n++)
        {
            repeatOnWebsite.Add(await MappedAsync(Website, centralStock: 3, channelStock: 3));
            await TaskAsync(Website, repeatOnWebsite[n], ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Timeout);
            driftedOnWebsite.Add(await MappedAsync(Website, centralStock: 3, channelStock: null));
        }

        // Room for five of the six: the two of pos, then the drifted pairs of website before its repeats.
        var first = await RunAsync(maxTasksPerRun: 5);

        Assert.Equal((true, 6, 4, 2, 5, 1, 0, 0), Counts(first));
        Assert.Equal(3, first.FailedRetried);
        var tasks = await ReconciliationTasksAsync();
        Assert.Equal(5, tasks.Count);
        Assert.All(repeatOnPos.Concat(driftedOnWebsite), p => Assert.Single(tasks, t => t.ProductId == p.ProductId));
        var left = Assert.Single(repeatOnWebsite, p => tasks.All(t => t.ProductId != p.ProductId));
        using (var payload = JsonDocument.Parse(Assert.Single(await EventsAsync()).Payload!))
        {
            Assert.Equal((5, 1, 3), (Number(payload, "tasksCreated"), Number(payload, "deferred"), Number(payload, "failedRetried")));
        }

        // The one that was left is found again by the next run.
        var capped = await RunAsync(maxTasksPerRun: 1);
        Assert.Equal((true, 1, 1, 0, 1, 0, 5, 0), Counts(capped));
        Assert.Equal(1, capped.FailedRetried);
        Assert.Equal(left.ProductId, (await ReconciliationTasksAsync()).Single(t => t.CorrelationId == ReconciliationIds.CorrelationIdOf(capped.RunId)).ProductId);
    }

    [Fact]
    public async Task A_cap_reached_by_repeated_failed_syncs_defers_the_drift_of_a_later_channel()
    {
        // They are tasks like any other: what the repeats of pos used up is not there for the website any more.
        for (var n = 0; n < 2; n++)
        {
            var repeat = await MappedAsync(Pos, centralStock: 3, channelStock: 3);
            await TaskAsync(Pos, repeat, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Unavailable);
        }

        var drifted = await MappedAsync(Website, centralStock: 3, channelStock: null);

        var result = await RunAsync(maxTasksPerRun: 2);

        Assert.Equal((true, 3, 2, 1, 2, 1, 0, 0), Counts(result));
        Assert.Equal(2, result.FailedRetried);
        Assert.DoesNotContain(await ReconciliationTasksAsync(), t => t.ProductId == drifted.ProductId);
    }

    // ---- which mappings are looked at -----------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_without_a_queue_an_inactive_channel_and_an_inactive_mapping_are_never_looked_at()
    {
        // All three would be drift.
        var noQueue = await DbAsync(async db =>
        {
            var channel = new Channel("Pop-up store", $"popup-{Guid.NewGuid():N}"[..20], ChannelType.Pos);
            db.Channels.Add(channel);
            await db.SaveChangesAsync();
            return channel;
        });
        var onNoQueue = await ProductAsync(5);
        await MapAsync(noQueue.Slug, onNoQueue);

        var onInactiveChannel = await MappedAsync(Marketplace, centralStock: 5, channelStock: null);
        await DbAsync(async db =>
        {
            var channel = await db.Channels.SingleAsync(c => c.Slug == Marketplace);
            channel.Update(channel.Name, ChannelStatus.Inactive);
            return await db.SaveChangesAsync();
        });

        var inactiveMapping = await ProductAsync(5);
        var mapping = await MapAsync(Website, inactiveMapping);
        await DbAsync(async db =>
        {
            var tracked = await db.ChannelProducts.SingleAsync(m => m.ChannelProductId == mapping.ChannelProductId);
            tracked.Update(tracked.ExternalProductId, tracked.ExternalSku, ChannelProductStatus.Inactive);
            return await db.SaveChangesAsync();
        });

        // The control: an Active mapping of an Active channel with a queue.
        var looked = await MappedAsync(Website, centralStock: 5, channelStock: null);

        var result = await RunAsync();

        Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(result));
        Assert.Equal(looked.ProductId, Assert.Single(await ReconciliationTasksAsync()).ProductId);
        Assert.Equal([Website], _channel.Reads.Select(r => r.ChannelSlug));
        Assert.Equal([looked.ProductId], _channel.Reads.SelectMany(r => r.Items).Select(i => i.ProductId));
        Assert.DoesNotContain(
            await DbAsync(db => db.ProcessingTasks.AsNoTracking().Select(t => t.ProductId).ToListAsync()),
            id => id == onNoQueue.ProductId || id == onInactiveChannel.ProductId || id == inactiveMapping.ProductId);
    }

    // ---- one channel that cannot be read --------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_that_cannot_be_read_ends_for_the_run_and_the_others_are_reconciled()
    {
        var onPos = new List<Product>();
        for (var n = 0; n < 5; n++)
        {
            onPos.Add(await MappedAsync(Pos, centralStock: 3, channelStock: null));
        }

        // Of the five on pos, one is skipped by its own rule: it is not counted as unreadable.
        await TaskAsync(Pos, onPos[0], ProcessingTaskStatus.Pending);
        var onWebsite = await MappedAsync(Website, centralStock: 3, channelStock: null);
        var onMarketplace = await MappedAsync(Marketplace, centralStock: 3, channelStock: 3);
        _channel.ReadBehaviour = (query, _) => query.ChannelSlug == Pos
            ? throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "pos is down (test)", isTransient: true)
            : Task.FromResult<IReadOnlyList<ChannelStockReading>>(
                query.Items.Select(i => new ChannelStockReading(i.ProductId, i.ProductId == onMarketplace.ProductId ? 3 : null)).ToList());

        // Batches of two: pos has three batches, only the first is read.
        var result = await RunAsync(batchSize: 2);

        Assert.Equal((true, 2, 1, 1, 1, 0, 1, 0), Counts(result));
        var posName = await DbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == Pos).Select(c => c.Name).SingleAsync());
        Assert.Equal([new UnreadableChannel(posName, ChannelApiErrorCodes.Unavailable, 4)], result.Unreadable);
        Assert.Single(_channel.ReadsOf(Pos));
        Assert.Single(_channel.ReadsOf(Website));
        Assert.Single(_channel.ReadsOf(Marketplace));

        var created = Assert.Single(await ReconciliationTasksAsync());
        Assert.Equal(onWebsite.ProductId, created.ProductId);

        // No task was created for pos, and the event names the channel, why, and how many pairs were left.
        using var payload = JsonDocument.Parse(Assert.Single(await EventsAsync()).Payload!);
        var unreadable = Assert.Single(payload.RootElement.GetProperty("unreadable").EnumerateArray());
        Assert.Equal(["channel", "errorCode", "pairs"], unreadable.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(
            (posName, ChannelApiErrorCodes.Unavailable, 4),
            (unreadable.GetProperty("channel").GetString(), unreadable.GetProperty("errorCode").GetString(), unreadable.GetProperty("pairs").GetInt32()));
        Assert.Equal((2, 1), (Number(payload, "checked"), Number(payload, "tasksCreated")));
    }

    [Fact]
    public async Task A_read_that_fails_with_something_that_is_no_channel_answer_ends_only_its_channel()
    {
        // Task 023 A1: a defect of one channel's adapter (or a typo in its configuration) is not the other channels' problem.
        for (var n = 0; n < 3; n++)
        {
            await MappedAsync(Pos, centralStock: 3, channelStock: null);
        }

        var onWebsite = await MappedAsync(Website, centralStock: 3, channelStock: null);
        var onMarketplace = await MappedAsync(Marketplace, centralStock: 3, channelStock: 3);
        var bug = new InvalidOperationException("adapter bug (test)");
        _channel.ReadBehaviour = (query, _) => query.ChannelSlug == Pos
            ? throw bug
            : Task.FromResult<IReadOnlyList<ChannelStockReading>>(
                query.Items.Select(i => new ChannelStockReading(i.ProductId, i.ProductId == onMarketplace.ProductId ? 3 : null)).ToList());
        var logs = new CapturingLoggerProvider();

        var result = await RunAsync(batchSize: 2, logs: logs);

        // pos (looked at between digital-platform and website) is left after its first read; the others are reconciled.
        Assert.Equal((true, 2, 1, 1, 1, 0, 0, 0), Counts(result));
        var posName = await DbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == Pos).Select(c => c.Name).SingleAsync());
        Assert.Equal([new UnreadableChannel(posName, AttemptErrorCodes.Unexpected, 3)], result.Unreadable);
        Assert.Equal("unexpected", result.Unreadable[0].ErrorCode);
        Assert.Single(_channel.ReadsOf(Pos));
        Assert.Equal(onWebsite.ProductId, Assert.Single(await ReconciliationTasksAsync()).ProductId);

        using var payload = JsonDocument.Parse(Assert.Single(await EventsAsync()).Payload!);
        var unreadable = Assert.Single(payload.RootElement.GetProperty("unreadable").EnumerateArray());
        Assert.Equal(
            (posName, "unexpected", 3),
            (unreadable.GetProperty("channel").GetString(), unreadable.GetProperty("errorCode").GetString(), unreadable.GetProperty("pairs").GetInt32()));

        // One Error that carries the exception - it is a defect to fix, unlike an answer of a channel.
        var error = Assert.Single(logs.Entries, e => e.Category == typeof(StockReconciliation).FullName && e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(bug, error.Exception);
        Assert.Contains(Pos, error.Message);
    }

    [Fact]
    public async Task A_channel_answer_that_fails_a_read_is_a_warning_without_the_exception()
    {
        await MappedAsync(Pos, centralStock: 3, channelStock: null);
        _channel.ReadBehaviour = (_, _) => throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "pos is down (test)", isTransient: true);
        var logs = new CapturingLoggerProvider();

        var result = await RunAsync(logs: logs);

        Assert.Equal(ChannelApiErrorCodes.Unavailable, Assert.Single(result.Unreadable).ErrorCode);
        var warning = Assert.Single(logs.Entries, e => e.Category == typeof(StockReconciliation).FullName && e.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Warning, (Exception?)null), (warning.Level, warning.Exception));
    }

    [Fact]
    public async Task A_cancelled_read_while_the_host_keeps_running_ends_only_its_channel()
    {
        // An adapter that gives up by itself (its own timeout, a cancelled inner call) is a failed read like any other:
        // only the host's shutdown ends a run.
        await MappedAsync(Pos, centralStock: 3, channelStock: null);
        var onWebsite = await MappedAsync(Website, centralStock: 3, channelStock: null);
        _channel.ReadBehaviour = (query, _) => query.ChannelSlug == Pos
            ? throw new OperationCanceledException("the adapter gave up (test)")
            : Task.FromResult<IReadOnlyList<ChannelStockReading>>(query.Items.Select(i => new ChannelStockReading(i.ProductId, null)).ToList());

        var result = await RunAsync();

        Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(result));
        Assert.Equal(AttemptErrorCodes.Unexpected, Assert.Single(result.Unreadable).ErrorCode);
        Assert.Equal(onWebsite.ProductId, Assert.Single(await ReconciliationTasksAsync()).ProductId);
    }

    [Fact]
    public async Task A_host_that_stops_during_a_read_ends_the_run_without_an_event()
    {
        // pos is read first of the two; the website must not be asked any more.
        await MappedAsync(Pos, centralStock: 3, channelStock: null);
        await MappedAsync(Website, centralStock: 3, channelStock: null);
        using var stopping = new CancellationTokenSource();
        _channel.ReadBehaviour = async (_, token) =>
        {
            await stopping.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the read was not cancelled (test)");
        };
        var logs = new CapturingLoggerProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(logs: logs, cancellationToken: stopping.Token));

        // Not an unreadable channel: nothing was written, nothing was logged as an error, the other channel was not asked.
        Assert.Equal((0, 0, 0), await WrittenAsync());
        Assert.Equal([Pos], _channel.Reads.Select(r => r.ChannelSlug));
        Assert.DoesNotContain(logs.Entries, e => e.Category == typeof(StockReconciliation).FullName && e.Level >= LogLevel.Warning);

        // The lock went with the run.
        _channel.ReadBehaviour = null;
        Assert.Equal((true, 2, 0, 2, 2, 0, 0, 0), Counts(await RunAsync()));
    }

    [Fact]
    public async Task A_run_that_only_failed_to_read_a_channel_still_writes_its_event()
    {
        await MappedAsync(Pos, centralStock: 3, channelStock: 3);
        _channel.ReadBehaviour = (_, _) =>
            throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "credentials refused (test)", isTransient: false);

        var result = await RunAsync();

        // Any code ends the channel, a permanent one too.
        Assert.Equal((true, 0, 0, 0, 0, 0, 0, 0), Counts(result));
        Assert.Equal(ChannelApiErrorCodes.Rejected, Assert.Single(result.Unreadable).ErrorCode);
        Assert.True(result.IsNoteworthy);
        Assert.Equal((0, 0, 1), await WrittenAsync());
    }

    // ---- cap and batches ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_creates_at_most_its_cap_of_tasks_and_counts_the_rest_as_deferred()
    {
        for (var n = 0; n < 4; n++)
        {
            await MappedAsync(Website, centralStock: 3, channelStock: null);
        }

        for (var n = 0; n < 3; n++)
        {
            await MappedAsync(Pos, centralStock: 3, channelStock: null);
        }

        // The cap is reached inside a batch (batches of two: 2 + 1 of the second pos batch... pos comes before website).
        var first = await RunAsync(batchSize: 2, maxTasksPerRun: 5);

        Assert.Equal((true, 7, 0, 7, 5, 2, 0, 0), Counts(first));
        Assert.Equal(5, (await ReconciliationTasksAsync()).Count);
        Assert.Equal((5, 5, 1), await WrittenAsync());
        using (var payload = JsonDocument.Parse(Assert.Single(await EventsAsync()).Payload!))
        {
            Assert.Equal((7, 5, 2), (Number(payload, "drifted"), Number(payload, "tasksCreated"), Number(payload, "deferred")));
        }

        // Every channel was still read to the end: the deferred pairs are counted, not guessed.
        Assert.Equal(7, _channel.Reads.Sum(r => r.Items.Count));

        // The next run finds the rest; the five of the first run have their task now.
        var second = await RunAsync(batchSize: 2, maxTasksPerRun: 5);

        Assert.Equal((true, 2, 0, 2, 2, 0, 5, 0), Counts(second));
        var tasks = await ReconciliationTasksAsync();
        Assert.Equal(7, tasks.Count);
        Assert.Equal(7, tasks.Select(t => (t.ChannelId, t.ProductId)).Distinct().Count());
        Assert.Equal([2, 5], tasks.GroupBy(t => t.CorrelationId).Select(g => g.Count()).Order());
    }

    [Fact]
    public async Task A_cap_of_one_creates_one_task()
    {
        await MappedAsync(Website, centralStock: 3, channelStock: null);
        await MappedAsync(Website, centralStock: 3, channelStock: null);

        var result = await RunAsync(maxTasksPerRun: 1);

        Assert.Equal((true, 2, 0, 2, 1, 1, 0, 0), Counts(result));
        Assert.Single(await ReconciliationTasksAsync());
    }

    [Fact]
    public async Task The_batch_size_decides_the_number_of_reads_and_every_mapping_is_looked_at_once()
    {
        var products = new List<Product>();
        for (var n = 0; n < 5; n++)
        {
            products.Add(await MappedAsync(Website, centralStock: n, channelStock: n));
        }

        var result = await RunAsync(batchSize: 2);

        Assert.Equal((true, 5, 5, 0, 0, 0, 0, 0), Counts(result));
        var reads = _channel.ReadsOf(Website);
        Assert.Equal([2, 2, 1], reads.Select(r => r.Items.Count));
        var asked = reads.SelectMany(r => r.Items).Select(i => i.ProductId).ToList();
        Assert.Equal(products.Select(p => p.ProductId).Order(), asked.Order());
        // Keyset order: by product id, the same in every run.
        Assert.Equal(asked.Order(), asked);

        // A batch that is exactly full needs no further read; one bigger than the table reads once.
        _channel.ReadBehaviour = null;
        var before = _channel.Reads.Count;
        await RunAsync(batchSize: 5);
        Assert.Equal(1, _channel.Reads.Count - before);
        await RunAsync(batchSize: 100);
        Assert.Equal([5, 5], _channel.Reads.Skip(before).Select(r => r.Items.Count));
    }

    // ---- the source of truth --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_writes_tasks_messages_and_one_event_and_leaves_every_other_table_as_it_is()
    {
        // Rows in every table a run must not touch: a product with a low-stock alert, an import (movement), an order.
        var created = await ServicesAsync(services => services.GetRequiredService<IProductService>().CreateAsync(
            new CreateProductRequest($"TRUTH-{Guid.NewGuid():N}"[..20], "Source of truth", 10m, 5, null), CancellationToken.None));
        await ServicesAsync(services => services.GetRequiredService<IInventoryImportService>().ImportAsync(
            new ImportInventoryRequest($"GRN-{Guid.NewGuid():N}", null, [new ImportInventoryItem(created.ProductId, 3)]), CancellationToken.None));
        await DbAsync(async db =>
        {
            var customer = new Customer($"Truth customer {Guid.NewGuid():N}", null, null, null);
            var order = new Order(await db.Channels.Where(c => c.Slug == Website).Select(c => c.ChannelId).SingleAsync(),
                customer.CustomerId, $"TRUTH-{Guid.NewGuid():N}", OrderStatus.Reserved);
            order.AddItem(created.ProductId, 1, 10m);
            db.Customers.Add(customer);
            db.Orders.Add(order);
            db.ChannelProducts.Add(new ChannelProduct(order.ChannelId, created.ProductId, "TRUTH-1", null));
            return await db.SaveChangesAsync();
        });
        _channel.SetStock(Website, created.ProductId, 99);
        await MappedAsync(Pos, centralStock: 4, channelStock: null);
        await MappedAsync(Marketplace, centralStock: 4, channelStock: 4);

        string[] untouched =
        [
            "Inventories", "Products", "ChannelProducts", "Orders", "OrderItems", "InventoryMovements", "Alerts",
            "Channels", "Customers", "ProcessingAttempts", "ReportExports", "SimulatedChannelStocks"
        ];
        string[] neverEmpty = ["Inventories", "Products", "ChannelProducts", "Orders", "OrderItems", "InventoryMovements", "Alerts"];
        var before = await SnapshotAsync(untouched);
        Assert.All(neverEmpty, table => Assert.NotEmpty(before[table]));
        var (tasksBefore, messagesBefore, eventsBefore) = await WrittenAsync();
        var existingTasks = await SnapshotAsync(["ProcessingTasks", "OutboxEvents", "SystemEventLogs"]);

        var result = await RunAsync();

        Assert.Equal((true, 3, 1, 2, 2, 0, 0, 0), Counts(result));
        // Row for row, the row version (xmin) included: not even rewritten with the same values.
        var after = await SnapshotAsync(untouched);
        Assert.All(untouched, table => Assert.Equal(before[table], after[table]));
        // What it did write: two tasks, two messages, one event - added, nothing that existed was changed.
        Assert.Equal((tasksBefore + 2, messagesBefore + 2, eventsBefore + 1), await WrittenAsync());
        var written = await SnapshotAsync(["ProcessingTasks", "OutboxEvents", "SystemEventLogs"]);
        Assert.All(existingTasks, table => Assert.Subset(written[table.Key].ToHashSet(), table.Value.ToHashSet()));
    }

    // ---- one run at a time ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Of_two_runs_at_the_same_time_one_works_and_the_other_is_skipped()
    {
        await MappedAsync(Website, centralStock: 3, channelStock: null);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.ReadBehaviour = async (query, token) =>
        {
            reading.TrySetResult();
            await release.Task.WaitAsync(token);
            return query.Items.Select(i => new ChannelStockReading(i.ProductId, null)).ToList();
        };

        // The first run holds the lock while it waits for the channel.
        var first = RunAsync(channelCallTimeout: TimeSpan.FromMinutes(2));
        await reading.Task.WaitAsync(Timeout);

        var second = await RunAsync();

        Assert.False(second.Ran);
        Assert.Equal((false, 0, 0, 0, 0, 0, 0, 0), Counts(second));
        Assert.Single(_channel.Reads); // the skipped run asked nobody
        Assert.Equal((0, 0, 0), await WrittenAsync());

        release.SetResult();
        var worked = await first;

        Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(worked));
        Assert.NotEqual(worked.RunId, second.RunId);

        // The lock went with the run: the next one works (and finds the first run's task unfinished).
        var third = await RunAsync();
        Assert.Equal((true, 0, 0, 0, 0, 0, 1, 0), Counts(third));
        Assert.Single(await ReconciliationTasksAsync());
    }

    [Fact]
    public async Task A_run_whose_own_database_work_fails_ends_releases_its_lock_and_keeps_the_batches_it_committed()
    {
        for (var n = 0; n < 4; n++)
        {
            await MappedAsync(Website, centralStock: 3, channelStock: null);
        }

        // The second batch cannot be written: while it is read, a task takes the key the run is about to use for the
        // batch's first pair. That is a failure of the run's own statement - not of a read - and ends the run.
        var runId = Guid.CreateVersion7();
        var websiteId = await ChannelIdAsync(Website);
        var reads = 0;
        _channel.ReadBehaviour = async (query, _) =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                var productId = query.Items[0].ProductId;
                await DbAsync(async db =>
                {
                    db.ProcessingTasks.Add(ProcessingTask.ForStockSync(
                        websiteId, productId, "seed", null, ReconciliationIds.IdempotencyKeyOf(runId, websiteId, productId)));
                    return await db.SaveChangesAsync();
                });
            }

            return query.Items.Select(i => new ChannelStockReading(i.ProductId, null)).ToList();
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(batchSize: 2, runId: runId));

        // The first batch is whole (two tasks, two messages) next to the task of this test; the run has no event, and
        // nothing of the second batch is half written.
        Assert.Equal((3, 2, 0), await WrittenAsync());

        _channel.ReadBehaviour = null;
        var next = await RunAsync(batchSize: 2);
        Assert.Equal((true, 1, 0, 1, 1, 0, 3, 0), Counts(next));
    }

    [Fact]
    public async Task A_cancelled_run_stops_between_batches_and_releases_its_lock()
    {
        for (var n = 0; n < 4; n++)
        {
            await MappedAsync(Website, centralStock: 3, channelStock: null);
        }

        using var stopping = new CancellationTokenSource();
        _channel.ReadBehaviour = async (query, _) =>
        {
            // The host is asked to stop while the first batch is being read: that batch is still completed.
            await stopping.CancelAsync();
            return query.Items.Select(i => new ChannelStockReading(i.ProductId, null)).ToList();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(batchSize: 2, cancellationToken: stopping.Token));

        var (tasks, messages, events) = await WrittenAsync();
        Assert.True(tasks is 0 or 2, $"a batch is all or nothing, found {tasks} tasks");
        Assert.Equal((tasks, 0), (messages, events));
        Assert.Single(_channel.Reads);

        _channel.ReadBehaviour = null;
        Assert.True((await RunAsync()).Ran);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static (bool Ran, int Checked, int InSync, int Drifted, int TasksCreated, int Deferred, int SkippedUnfinished, int SkippedRejected) Counts(
        ReconciliationRunResult result) =>
        (result.Ran, result.Checked, result.InSync, result.Drifted, result.TasksCreated, result.Deferred, result.SkippedUnfinished, result.SkippedRejected);

    private static int Number(JsonDocument payload, string field) => payload.RootElement.GetProperty(field).GetInt32();

    /// <summary>One run, as the service starts it, on a host with these settings (not started) and the recording channel.</summary>
    private async Task<ReconciliationRunResult> RunAsync(
        int? batchSize = null,
        int? maxTasksPerRun = null,
        TimeSpan? channelCallTimeout = null,
        Guid? runId = null,
        CapturingLoggerProvider? logs = null,
        CancellationToken cancellationToken = default)
    {
        var settings = new Dictionary<string, string?>();
        if (batchSize is not null) settings["Reconciliation:BatchSize"] = batchSize.ToString();
        if (maxTasksPerRun is not null) settings["Reconciliation:MaxTasksPerRun"] = maxTasksPerRun.ToString();
        if (channelCallTimeout is not null) settings["StockSyncWorker:ChannelCallTimeout"] = channelCallTimeout.Value.ToString("c");

        using var host = fixture.CreateHost(
            "reconciliation-run",
            settings,
            services =>
            {
                services.UseChannelAdapter(_channel);
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            databaseConnectionString: _database);
        return await RunOnAsync(host, runId, cancellationToken);
    }

    private static async Task<ReconciliationRunResult> RunOnAsync(IHost host, Guid? runId = null, CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StockReconciliation>()
            .RunAsync(runId ?? Guid.CreateVersion7(), ReconciliationService.WorkerName, cancellationToken);
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

    private Task<Guid> ChannelIdAsync(string slug) =>
        DbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == slug).Select(c => c.ChannelId).SingleAsync());

    private async Task<Product> ProductAsync(int stock)
    {
        var product = new Product($"RC-{Guid.NewGuid():N}"[..20], "Reconciliation product", 3m, 0, null);
        await DbAsync(async db =>
        {
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, stock));
            return await db.SaveChangesAsync();
        });
        return product;
    }

    /// <summary>A mapping without the initial sync the API would start: reconciliation is what is under test.</summary>
    private async Task<ChannelProduct> MapAsync(string slug, Product product, string? externalProductId = null)
    {
        var mapping = new ChannelProduct(await ChannelIdAsync(slug), product.ProductId, externalProductId, $"EXT-{product.Sku}");
        await DbAsync(async db =>
        {
            db.ChannelProducts.Add(mapping);
            return await db.SaveChangesAsync();
        });
        return mapping;
    }

    /// <summary>A product with <paramref name="centralStock"/>, mapped on the channel, which shows <paramref name="channelStock"/> (null = nothing).</summary>
    private async Task<Product> MappedAsync(string slug, int centralStock, int? channelStock, string? externalProductId = null)
    {
        var product = await ProductAsync(centralStock);
        await MapAsync(slug, product, externalProductId);
        _channel.SetStock(slug, product.ProductId, channelStock);
        return product;
    }

    /// <summary>
    /// A stock-sync task of the pair in <paramref name="status"/>, as the worker leaves it; a failed one ends with
    /// <paramref name="errorCode"/> on its last attempt, after an attempt that failed with <paramref name="earlierAttempt"/>
    /// when given (a transient failure before it, or a rejection followed by a manual retry).
    /// </summary>
    private async Task<ProcessingTask> TaskAsync(
        string slug,
        Product product,
        ProcessingTaskStatus status,
        string? errorCode = null,
        string? earlierAttempt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var task = ProcessingTask.ForStockSync(await ChannelIdAsync(slug), product.ProductId, $"seed-{Guid.NewGuid():N}", Guid.NewGuid().ToString());
        ProcessingAttempt Start() => task.StartAttempt(StockSyncMessageHandler.WorkerName, "seed", now);

        if (earlierAttempt == ChannelApiErrorCodes.Rejected)
        {
            task.Fail(Start().AttemptId, earlierAttempt, "seeded", now);
            task.RequeueForManualRetry(Guid.NewGuid().ToString());
        }
        else if (earlierAttempt is not null)
        {
            task.ScheduleRetry(Start().AttemptId, earlierAttempt, "seeded", now);
        }

        switch (status)
        {
            case ProcessingTaskStatus.Pending:
                break;
            case ProcessingTaskStatus.InProgress:
                Start();
                break;
            case ProcessingTaskStatus.RetryScheduled:
                task.ScheduleRetry(Start().AttemptId, ChannelApiErrorCodes.Unavailable, "seeded", now);
                break;
            case ProcessingTaskStatus.Succeeded:
                task.Succeed(null, Start().AttemptId, now);
                break;
            case ProcessingTaskStatus.Failed:
                task.Fail(Start().AttemptId, errorCode ?? throw new ArgumentNullException(nameof(errorCode)), "seeded", now);
                break;
        }

        await DbAsync(async db =>
        {
            db.ProcessingTasks.Add(task);
            return await db.SaveChangesAsync();
        });
        return task;
    }

    private Task<List<ProcessingTask>> ReconciliationTasksAsync() =>
        DbAsync(db => db.ProcessingTasks
            .AsNoTracking()
            .Include(t => t.Attempts)
            .Where(t => t.CorrelationId.StartsWith(ReconciliationIds.CorrelationIdPrefix))
            .OrderBy(t => t.TaskId)
            .ToListAsync());

    private Task<List<SystemEventLog>> EventsAsync() =>
        DbAsync(db => db.SystemEventLogs.AsNoTracking().Where(e => e.EventType == ReconciliationEvents.Completed).OrderBy(e => e.LogId).ToListAsync());

    /// <summary>Rows in the three tables a run may write: tasks, messages, events.</summary>
    private Task<(int Tasks, int Messages, int Events)> WrittenAsync() =>
        DbAsync(async db => (await db.ProcessingTasks.CountAsync(), await db.OutboxEvents.CountAsync(), await db.SystemEventLogs.CountAsync()));

    /// <summary>Every row of each table as text, with its row version (<c>xmin</c> changes whenever a row is written).</summary>
    private async Task<Dictionary<string, List<string>>> SnapshotAsync(string[] tables)
    {
        var snapshot = new Dictionary<string, List<string>>();
        foreach (var table in tables)
        {
            // Table names of this test only, never input.
            var sql = "SELECT t.xmin::text || ' ' || t::text AS \"Value\" FROM \"" + table + "\" t ORDER BY 1";
            snapshot[table] = await DbAsync(db => db.Database.SqlQueryRaw<string>(sql).ToListAsync());
        }

        return snapshot;
    }
}

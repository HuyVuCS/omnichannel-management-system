using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Omnichannel.Api.Hubs;
using Omnichannel.Api.Middleware;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Realtime;
using Serilog;
using Serilog.Events;

namespace Omnichannel.Tests.Realtime;

/// <summary>Task 015: which hints a unit of work produces (read from the change tracker; no database needed).</summary>
public sealed class RealtimeHintCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Added_order_gives_order_created()
    {
        using var context = NewContext();
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), "EXT-1", OrderStatus.InsufficientStock);
        context.Orders.Add(order);

        var hint = Assert.Single(RealtimeHintInterceptor.Capture(context));

        Assert.Equal(RealtimeHint.OrderCreated(order.OrderId, order.ChannelId, OrderStatus.InsufficientStock), hint);
        Assert.Equal((RealtimeEventTypes.OrderCreated, "InsufficientStock"), (hint.Type, hint.Status));
    }

    [Fact]
    public void Added_movements_give_one_inventory_changed_with_distinct_products()
    {
        using var context = NewContext();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        context.InventoryMovements.AddRange(Movement(a), Movement(b), Movement(a));

        var hint = Assert.Single(RealtimeHintInterceptor.Capture(context));

        Assert.Equal(RealtimeEventTypes.InventoryChanged, hint.Type);
        Assert.Equal(new[] { a, b }.Order(), hint.ProductIds!.Order());
        Assert.Null(hint.All);
    }

    [Fact]
    public void More_products_than_the_cap_give_all_instead_of_a_list()
    {
        using var context = NewContext();
        context.InventoryMovements.AddRange(Enumerable.Range(0, RealtimeHintBatch.MaxProductIds + 1).Select(_ => Movement(Guid.NewGuid())));

        var hint = Assert.Single(RealtimeHintInterceptor.Capture(context));

        Assert.Equal((RealtimeEventTypes.InventoryChanged, (bool?)true), (hint.Type, hint.All));
        Assert.Null(hint.ProductIds);
    }

    [Fact]
    public void Exactly_the_cap_is_still_listed()
    {
        using var context = NewContext();
        context.InventoryMovements.AddRange(Enumerable.Range(0, RealtimeHintBatch.MaxProductIds).Select(_ => Movement(Guid.NewGuid())));

        var hint = Assert.Single(RealtimeHintInterceptor.Capture(context));

        Assert.Equal(RealtimeHintBatch.MaxProductIds, hint.ProductIds!.Count);
        Assert.True(Encoding.UTF8.GetByteCount(new RealtimeHintBatch([hint]).ToPayload()) < 8000);
    }

    [Fact]
    public void Added_task_gives_processing_task_changed_with_its_ids()
    {
        using var context = NewContext();
        var (channelId, productId) = (Guid.NewGuid(), Guid.NewGuid());
        var task = ProcessingTask.ForStockSync(channelId, productId, "corr", "msg");
        context.ProcessingTasks.Add(task);

        var hint = Assert.Single(RealtimeHintInterceptor.Capture(context));

        Assert.Equal(
            RealtimeHint.TaskChanged(task.TaskId, ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, null, channelId, productId),
            hint);
        Assert.Equal((RealtimeEventTypes.ProcessingTaskChanged, "StockSync", "Pending"), (hint.Type, hint.TaskType, hint.Status));
    }

    [Fact]
    public void Changed_task_status_gives_a_hint_and_an_unchanged_status_does_not()
    {
        using var context = NewContext();
        var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "corr", "msg");
        context.ProcessingTasks.Attach(task); // as loaded from the database: Pending, unchanged

        Assert.Empty(RealtimeHintInterceptor.Capture(context));

        // Pending -> InProgress.
        var first = task.StartAttempt("OrderWorker", "i-1", Now);
        context.ProcessingAttempts.Add(first);
        var started = Assert.Single(RealtimeHintInterceptor.Capture(context));
        Assert.Equal((task.TaskId, "InProgress"), (started.TaskId, started.Status));

        // Saved; the next delivery abandons the running attempt and starts another: the task stays InProgress.
        context.ChangeTracker.AcceptAllChanges();
        context.ProcessingAttempts.Add(task.StartAttempt("OrderWorker", "i-2", Now));
        Assert.Empty(RealtimeHintInterceptor.Capture(context));

        // InProgress -> Succeeded with the order it created.
        var orderId = Guid.NewGuid();
        task.Succeed(orderId, task.Attempts.Last().AttemptId, Now);
        var done = Assert.Single(RealtimeHintInterceptor.Capture(context));
        Assert.Equal(((Guid?)orderId, "Succeeded", "OrderProcessing"), (done.OrderId, done.Status, done.TaskType));
    }

    [Fact]
    public void One_unit_of_work_lists_orders_then_inventory_then_tasks_and_ignores_everything_else()
    {
        using var context = NewContext();
        var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "corr", "msg");
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), "EXT-1", OrderStatus.Reserved);
        context.ProcessingTasks.Add(task);
        context.InventoryMovements.Add(Movement(Guid.NewGuid()));
        context.Orders.Add(order);
        // No hints for catalog data, outbox rows, attempts or audit events.
        context.Customers.Add(new Customer("Name", null, null, null));
        context.OutboxEvents.Add(new OutboxEvent("inventory.changed", "p", "{}", "corr", null));
        context.SystemEventLogs.Add(new SystemEventLog(SystemEventLevel.Information, "x", null, null, null, null, null, Now));

        var hints = RealtimeHintInterceptor.Capture(context);

        Assert.Equal(
            [RealtimeEventTypes.OrderCreated, RealtimeEventTypes.InventoryChanged, RealtimeEventTypes.ProcessingTaskChanged],
            hints.Select(h => h.Type));
    }

    [Fact]
    public void Nothing_of_interest_gives_no_hints()
    {
        using var context = NewContext();
        context.Customers.Add(new Customer("Name", null, null, null));

        Assert.Empty(RealtimeHintInterceptor.Capture(context));
    }

    private static InventoryMovement Movement(Guid productId) =>
        new(productId, InventoryMovementType.Import, 1, InventoryReferenceType.StockImport, Guid.NewGuid().ToString(), null, null);

    /// <summary>A context that never connects: the change tracker is all these tests need.</summary>
    private static OmnichannelDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OmnichannelDbContext>().UseNpgsql("Host=unused").Options);
}

public sealed class RealtimeHintPayloadTests
{
    [Fact]
    public void Payload_is_camel_case_json_without_empty_members()
    {
        var (orderId, channelId, taskId, productId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var payload = new RealtimeHintBatch(
        [
            RealtimeHint.OrderCreated(orderId, channelId, OrderStatus.Reserved),
            RealtimeHint.InventoryChanged([productId]),
            RealtimeHint.TaskChanged(taskId, ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.Succeeded, orderId, null, null)
        ]).ToPayload();

        var events = JsonNode.Parse(payload)!["events"]!.AsArray();
        Assert.Equal(
            $$"""{"type":"order.created","orderId":"{{orderId}}","channelId":"{{channelId}}","status":"Reserved"}""",
            events[0]!.ToJsonString());
        Assert.Equal($$"""{"type":"inventory.changed","productIds":["{{productId}}"]}""", events[1]!.ToJsonString());
        Assert.Equal(
            $$"""{"type":"processing.task.changed","orderId":"{{orderId}}","taskId":"{{taskId}}","taskType":"OrderProcessing","status":"Succeeded"}""",
            events[2]!.ToJsonString());
    }

    [Fact]
    public void Payload_round_trips()
    {
        var batch = new RealtimeHintBatch(
        [
            RealtimeHint.InventoryChanged([Guid.NewGuid(), Guid.NewGuid()]),
            RealtimeHint.AllOf(RealtimeEventTypes.InventoryChanged),
            RealtimeHint.TaskChanged(Guid.NewGuid(), ProcessingTaskType.StockSync, ProcessingTaskStatus.Failed, null, Guid.NewGuid(), Guid.NewGuid())
        ]);

        var parsed = RealtimeHintBatch.TryParse(batch.ToPayload());

        Assert.NotNull(parsed);
        Assert.Equal(batch.Events.Select(e => e with { ProductIds = null }), parsed.Events.Select(e => e with { ProductIds = null }));
        Assert.Equal(batch.Events[0].ProductIds, parsed.Events[0].ProductIds);
    }

    [Fact]
    public void A_unit_of_work_whose_hints_do_not_fit_is_reduced_to_one_all_hint_per_type()
    {
        // 200 task hints of about 270 bytes each do not fit into one notification (PostgreSQL: below 8000 bytes).
        var hints = Enumerable.Range(0, 200)
            .Select(_ => RealtimeHint.TaskChanged(
                Guid.NewGuid(), ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .Prepend(RealtimeHint.InventoryChanged([Guid.NewGuid()]))
            .ToList();

        var payload = new RealtimeHintBatch(hints).ToPayload();

        Assert.True(Encoding.UTF8.GetByteCount(payload) <= RealtimeChannel.MaxPayloadBytes);
        Assert.Equal(
            """{"events":[{"type":"inventory.changed","all":true},{"type":"processing.task.changed","all":true}]}""",
            payload);
    }

    [Fact]
    public void Twenty_five_task_hints_fit_as_they_are()
    {
        var hints = Enumerable.Range(0, 25)
            .Select(_ => RealtimeHint.TaskChanged(
                Guid.NewGuid(), ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.RetryScheduled, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var payload = new RealtimeHintBatch(hints).ToPayload();

        Assert.True(Encoding.UTF8.GetByteCount(payload) <= RealtimeChannel.MaxPayloadBytes);
        Assert.Equal(25, RealtimeHintBatch.TryParse(payload)!.Events.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"events":[]}""")]
    [InlineData("""{"events":[{"orderId":"0199a1b0-0000-7000-8000-000000000001"}]}""")]
    [InlineData("""{"events":[null]}""")]
    [InlineData("""{"events":"x"}""")]
    public void Anything_that_is_not_a_hint_batch_is_refused(string payload)
    {
        Assert.Null(RealtimeHintBatch.TryParse(payload));
    }
}

public sealed class RealtimeHintBatcherTests
{
    private static readonly RealtimeHint A = RealtimeHint.AllOf("a");
    private static readonly RealtimeHint B = RealtimeHint.AllOf("b");
    private static readonly RealtimeHint C = RealtimeHint.AllOf("c");

    [Fact]
    public async Task Hints_within_the_window_go_out_as_one_message_in_arrival_order()
    {
        await using var run = new BatcherRun(TimeSpan.FromMilliseconds(300));

        run.Batcher.Add([A, B]);
        await Task.Delay(50);
        run.Batcher.Add([C]);

        var message = await run.NextAsync();
        Assert.Equal([A, B, C], message.Events);
        Assert.False(message.Resync);

        // A hint after the window opens the next one.
        run.Batcher.Add([B]);
        Assert.Equal([B], (await run.NextAsync()).Events);
        Assert.Equal(2, run.Sent);
    }

    [Fact]
    public async Task Resync_request_is_part_of_the_next_message()
    {
        await using var run = new BatcherRun(TimeSpan.FromMilliseconds(100));

        run.Batcher.RequestResync();
        run.Batcher.Add([A]);

        var message = await run.NextAsync();
        Assert.True(message.Resync);
        Assert.Equal([A], message.Events);

        run.Batcher.RequestResync();
        var alone = await run.NextAsync();
        Assert.True(alone.Resync);
        Assert.Empty(alone.Events);
    }

    [Fact]
    public async Task A_batch_larger_than_the_limit_becomes_resync_without_a_list()
    {
        await using var run = new BatcherRun(TimeSpan.FromMilliseconds(100));

        run.Batcher.Add(Enumerable.Repeat(A, RealtimeHintBatcher.MaxEventsPerMessage + 1).ToList());

        var message = await run.NextAsync();
        Assert.True(message.Resync);
        Assert.Empty(message.Events);

        run.Batcher.Add(Enumerable.Repeat(A, RealtimeHintBatcher.MaxEventsPerMessage).ToList());
        var full = await run.NextAsync();
        Assert.Equal((RealtimeHintBatcher.MaxEventsPerMessage, false), (full.Events.Count, full.Resync));
    }

    [Fact]
    public async Task A_failing_send_does_not_stop_the_batcher()
    {
        await using var run = new BatcherRun(TimeSpan.FromMilliseconds(50)) { FailNext = 1 };

        run.Batcher.Add([A]);
        await Eventually(() => run.Attempts == 1);
        run.Batcher.Add([B]);

        Assert.Equal([B], (await run.NextAsync()).Events);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(10, true)]
    [InlineData(250, true)]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public void Batch_window_must_be_between_10_ms_and_5_s(int milliseconds, bool valid)
    {
        var result = new RealtimeOptionsValidator().Validate(null, new RealtimeOptions { BatchWindow = TimeSpan.FromMilliseconds(milliseconds) });

        Assert.Equal(valid, result.Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(250), new RealtimeOptions().BatchWindow);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 10 s");
            await Task.Delay(20);
        }
    }

    private sealed class BatcherRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Threading.Channels.Channel<RealtimeHintMessage> _messages =
            System.Threading.Channels.Channel.CreateUnbounded<RealtimeHintMessage>();
        private readonly Task _loop;
        private int _attempts;
        private int _sent;

        public BatcherRun(TimeSpan window)
        {
            Batcher = new RealtimeHintBatcher(
                Options.Create(new RealtimeOptions { BatchWindow = window }), TimeProvider.System, NullLogger<RealtimeHintBatcher>.Instance);
            _loop = Batcher.RunAsync(
                (message, _) =>
                {
                    if (Interlocked.Increment(ref _attempts) <= FailNext)
                    {
                        throw new InvalidOperationException("send failed (test)");
                    }

                    Interlocked.Increment(ref _sent);
                    _messages.Writer.TryWrite(message);
                    return Task.CompletedTask;
                },
                _stop.Token);
        }

        public RealtimeHintBatcher Batcher { get; }

        public int FailNext { get; init; }

        public int Attempts => Volatile.Read(ref _attempts);

        public int Sent => Volatile.Read(ref _sent);

        public async Task<RealtimeHintMessage> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _messages.Reader.ReadAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _loop);
            _stop.Dispose();
        }
    }
}

/// <summary>Task 015: the access token of the hub's query string never reaches a log event.</summary>
public sealed class AccessTokenRedactionTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

    [Theory]
    [InlineData("?access_token=" + Token, "?access_token=***")]
    [InlineData("?id=abc&access_token=" + Token + "&negotiateVersion=1", "?id=abc&access_token=***&negotiateVersion=1")]
    [InlineData("?ACCESS_TOKEN=" + Token, "?ACCESS_TOKEN=***")]
    [InlineData("http://localhost/api/hubs/notifications?access_token=" + Token + " - 200", "http://localhost/api/hubs/notifications?access_token=*** - 200")]
    [InlineData("?page=1", "?page=1")]
    public void Token_value_is_replaced(string text, string expected)
    {
        Assert.Equal(expected, AccessTokenRedactionEnricher.Redact(text));
    }

    [Fact]
    public void Every_text_property_of_a_log_event_is_redacted_and_so_is_the_rendered_message()
    {
        var events = new List<LogEvent>();
        using var logger = new LoggerConfiguration()
            .Enrich.With<AccessTokenRedactionEnricher>()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();

        logger.Information(
            "Request starting {Method} {Path}{QueryString} for {UserId} ({Count})",
            "GET", "/api/hubs/notifications", $"?id=1&access_token={Token}", "user-1", 3);

        var logged = Assert.Single(events);
        Assert.DoesNotContain(Token, logged.RenderMessage());
        Assert.DoesNotContain(logged.Properties, p => p.Value.ToString().Contains(Token, StringComparison.Ordinal));
        Assert.Equal("Request starting \"GET\" \"/api/hubs/notifications\"\"?id=1&access_token=***\" for \"user-1\" (3)", logged.RenderMessage());
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}

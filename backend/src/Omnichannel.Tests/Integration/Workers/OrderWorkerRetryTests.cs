using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.Processing;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>Task 007: delayed retry, final failure and DLQ for the Order Worker (test tiers 1 s / 2 s / 3 s / 4 s).</summary>
public sealed partial class OrderWorkerTests
{
    private const string Queue = RabbitMqTopology.OrderProcessingQueue;

    [Fact]
    public async Task Transient_failures_are_retried_with_delay_until_the_attempt_succeeds()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        var failures = new FailureInjection { ReserveFailures = 2 };

        await RunUntilAsync(taskId, ProcessingTaskStatus.Succeeded, failures);

        var attempts = (await LoadTaskAsync(taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts.Take(2), a => Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingErrorClassifier.Timeout), (a.Status, a.ErrorCode)));
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[2].Status);

        // Tier 1 (1 s) after attempt 1, tier 2 (2 s) after attempt 2; the delay starts when the copy is published.
        Assert.True(attempts[1].StartedAt - attempts[0].FinishedAt!.Value >= TimeSpan.FromMilliseconds(900),
            $"gap 1 was {attempts[1].StartedAt - attempts[0].FinishedAt}");
        Assert.True(attempts[2].StartedAt - attempts[1].FinishedAt!.Value >= TimeSpan.FromMilliseconds(1900),
            $"gap 2 was {attempts[2].StartedAt - attempts[1].FinishedAt}");

        Assert.Equal((10, 1, 9), await StockAsync(products[0].ProductId));
        Assert.Single(await MovementsAsync(products[0].ProductId, InventoryMovementType.Reserve));
        await AssertQueuesEmptyAsync(includeDeadLetter: true);
    }

    [Fact]
    public async Task Always_failing_message_fails_the_task_after_max_attempts_and_goes_to_the_dlq()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        var failures = new FailureInjection { ReserveFailures = int.MaxValue };

        await RunUntilAsync(taskId, ProcessingTaskStatus.Failed, failures, maxAttempts: 3);

        var task = await LoadTaskAsync(taskId);
        Assert.Equal(3, task.Attempts.Count);
        Assert.All(task.Attempts, a => Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingErrorClassifier.Timeout), (a.Status, a.ErrorCode)));
        Assert.Equal((10, 0, 10), await StockAsync(products[0].ProductId));

        var failed = Assert.Single(await ProcessingFailedLogsAsync(taskId));
        var payload = JsonNode.Parse(failed.Payload!)!;
        Assert.Equal((SystemEventLevel.Error, task.CorrelationId), (failed.Level, failed.CorrelationId));
        Assert.Equal((3, ProcessingErrorClassifier.Timeout, Queue, "OrderProcessing"),
            (payload["attemptNo"]!.GetValue<int>(), payload["errorCode"]!.GetValue<string>(),
                payload["queue"]!.GetValue<string>(), payload["taskType"]!.GetValue<string>()));

        var dead = Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));
        Assert.Equal(task.MessageId, dead.BasicProperties.MessageId);
        Assert.Equal(task.CorrelationId, dead.BasicProperties.CorrelationId);
        Assert.Equal(2, Convert.ToInt32(dead.BasicProperties.Headers![RabbitMqRetryPublisher.RetryAttemptHeader]));
        Assert.Equal(ProcessingErrorClassifier.Timeout, Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers[RabbitMqRetryPublisher.LastErrorCodeHeader]!));
        await AssertQueuesEmptyAsync(includeDeadLetter: false);
    }

    [Fact]
    public async Task Permanent_error_fails_at_once_without_retry()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        var failures = new FailureInjection
        {
            ReserveFailures = int.MaxValue,
            ReserveException = () => new PostgresException("new row violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)
        };

        await RunUntilAsync(taskId, ProcessingTaskStatus.Failed, failures);

        var attempt = Assert.Single((await LoadTaskAsync(taskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingErrorClassifier.CheckViolation), (attempt.Status, attempt.ErrorCode));
        Assert.Equal(0, failures.RetryPublishCalls);
        Assert.Single(await ProcessingFailedLogsAsync(taskId));
        Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));
        await AssertQueuesEmptyAsync(includeDeadLetter: false);
    }

    [Fact]
    public async Task Failed_retry_publish_requeues_the_original_and_nothing_is_lost()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        var failures = new FailureInjection { ReserveFailures = 1, RetryPublishFailures = 1 };

        await RunUntilAsync(taskId, ProcessingTaskStatus.Succeeded, failures);

        Assert.Equal(1, failures.RetryPublishCalls);
        var attempts = (await LoadTaskAsync(taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal((2, ProcessingAttemptStatus.Failed, ProcessingAttemptStatus.Succeeded), (attempts.Count, attempts[0].Status, attempts[1].Status));
        Assert.Single(await MovementsAsync(products[0].ProductId, InventoryMovementType.Reserve));
        await AssertQueuesEmptyAsync(includeDeadLetter: true);
    }

    [Fact]
    public async Task Unroutable_retry_copy_is_requeued_with_backoff_and_processed_once_the_topology_is_back()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        // The handler throws on every delivery, so each one needs a retry copy in the first delay tier - whose exchange is gone.
        var failures = new FailureInjection { StartAttemptFailures = int.MaxValue };
        var logs = new CapturingLoggerProvider();
        var firstTier = RabbitMqTopology.DelayNameOf(WorkersFixture.TestRetryDelays[0]);

        var hosts = StartWorkers(instances: 1, configureServices: services =>
        {
            services.InjectFailures(failures);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        try
        {
            // The consumer runs only after the topology was declared; delete the exchange afterwards (as an operator might).
            var worker = hosts[0].Services.GetServices<IHostedService>().OfType<OrderWorkerService>().Single();
            await Eventually.WaitAsync(() => Task.FromResult(worker.Consumer.Current), c => c is not null, Timeout, "consumer started");
            await using (var channel = await fixture.OpenChannelAsync())
            {
                await channel.ExchangeDeleteAsync(firstTier);
            }

            var taskId = await SubmitAsync(customerId, (products[0].ProductId, 2));
            await Eventually.WaitAsync(() => Task.FromResult(failures.StartAttemptCalls), n => n >= 1, Timeout, "first delivery");

            // Without a backoff this spun thousands of times per second (nack-requeues are not counted by the broker).
            await Task.Delay(TimeSpan.FromSeconds(10));
            var deliveries = failures.StartAttemptCalls;
            Assert.InRange(deliveries, 3, 6);

            // Rate-limited: one error for all of these requeues (the others are logged at Debug).
            Assert.Equal(1, logs.Count(typeof(ResilientQueueConsumer).FullName!, LogLevel.Error, "Could not publish the retry copy"));

            // Not lost, not failed, nothing dead-lettered.
            Assert.Equal(ProcessingTaskStatus.Pending, (await LoadTaskAsync(taskId)).Status);
            Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));

            await using (var channel = await fixture.OpenChannelAsync())
            {
                await RabbitMqTopology.DeclareAsync(channel, WorkersFixture.TestRetryDelays, CancellationToken.None);
            }

            failures.Heal();
            await WaitForTasksAsync([taskId], ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await StopAsync(hosts);
            await using var channel = await fixture.OpenChannelAsync();
            await RabbitMqTopology.DeclareAsync(channel, WorkersFixture.TestRetryDelays, CancellationToken.None);
        }

        Assert.Equal((10, 2, 8), await StockAsync(products[0].ProductId));
        await AssertQueuesEmptyAsync(includeDeadLetter: true);
    }

    [Fact]
    public async Task Crash_loop_is_finalised_after_max_attempts_instead_of_looping()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        // The handler throws out of the callback on every delivery (before any attempt is recorded). RabbitMQ 4 does not count
        // nack-requeues toward x-delivery-limit, so without the consumer's own count this looped forever (measured: ~16,000
        // deliveries per minute).
        var failures = new FailureInjection { StartAttemptFailures = int.MaxValue };

        await RunUntilAsync(taskId, ProcessingTaskStatus.Failed, failures);

        var attempt = Assert.Single((await LoadTaskAsync(taskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Failed, "handler_error"), (attempt.Status, attempt.ErrorCode));
        Assert.Contains("injected crash", attempt.ErrorMessage);
        Assert.Equal(5, failures.StartAttemptCalls); // OrderWorker:MaxAttempts default
        Assert.Single(await ProcessingFailedLogsAsync(taskId));
        Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));
        Assert.Equal((10, 0, 10), await StockAsync(products[0].ProductId));
    }

    [Fact]
    public async Task Broker_counts_unsettled_returns_but_not_nack_requeues()
    {
        await fixture.PublishAsync(EventTypes.OrderCreated, Encoding.UTF8.GetBytes("{}"), "delivery-count-probe");

        long CountOf(BasicGetResult result) =>
            result.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var value) == true ? Convert.ToInt64(value) : 0;

        // basic.nack with requeue: RabbitMQ 4 does NOT increment the delivery count (so x-delivery-limit cannot stop such loops).
        await using (var channel = await fixture.OpenChannelAsync())
        {
            var first = await Eventually.WaitAsync(() => channel.BasicGetAsync(Queue, autoAck: false), r => r is not null, Timeout, "probe");
            await channel.BasicNackAsync(first!.DeliveryTag, multiple: false, requeue: true);
            var second = await Eventually.WaitAsync(() => channel.BasicGetAsync(Queue, autoAck: false), r => r is not null, Timeout, "probe");
            Assert.Equal(CountOf(first), CountOf(second!));

            // Returned unsettled because the channel closes (consumer died): counted.
            await channel.CloseAsync();
        }

        await using (var channel = await fixture.OpenChannelAsync())
        {
            var third = await Eventually.WaitAsync(() => channel.BasicGetAsync(Queue, autoAck: true), r => r is not null, Timeout, "probe");
            Assert.Equal(1, CountOf(third!));
        }
    }

    [Fact]
    public async Task Last_delivery_allowed_by_the_queue_is_finalised_without_processing()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));

        // Publish the message without a worker, then let it come back unsettled DeliveryLimit times (each time a consumer
        // "dies" holding it: channel closed without ack), as in a real crash loop.
        var publisher = StartWorkers(instances: 1, configureServices: services => services.RemoveHostedService<OrderWorkerService>());
        try
        {
            await Eventually.WaitAsync(() => MessageCountAsync(Queue), count => count == 1, Timeout, "order.created published");
        }
        finally
        {
            await StopAsync(publisher);
        }

        for (var n = 0; n < RabbitMqTopology.DeliveryLimit; n++)
        {
            await using var channel = await fixture.OpenChannelAsync();
            await Eventually.WaitAsync(() => channel.BasicGetAsync(Queue, autoAck: false), r => r is not null, Timeout, "crash-loop delivery");
            await channel.CloseAsync();
        }

        var failures = new FailureInjection();
        await RunUntilAsync(taskId, ProcessingTaskStatus.Failed, failures);

        Assert.Equal(0, failures.StartAttemptCalls); // not processed: finalised before the handler ran
        var attempt = Assert.Single((await LoadTaskAsync(taskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Failed, "delivery_limit_reached"), (attempt.Status, attempt.ErrorCode));
        Assert.Single(await ProcessingFailedLogsAsync(taskId));
        Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));
        Assert.Equal((10, 0, 10), await StockAsync(products[0].ProductId));
    }

    [Fact]
    public async Task Closed_consumer_channel_is_recreated_and_consumption_continues()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var hosts = StartWorkers(instances: 1);
        try
        {
            var worker = hosts[0].Services.GetServices<IHostedService>().OfType<OrderWorkerService>().Single();
            var channel = await Eventually.WaitAsync(
                () => Task.FromResult(worker.Consumer.Current?.Channel),
                c => c is not null,
                Timeout,
                "consumer started");

            // A channel-level protocol error: acknowledging an unknown delivery tag makes the broker close the channel (406).
            await channel!.BasicAckAsync(deliveryTag: 999_999, multiple: false);
            await Eventually.WaitAsync(
                () => Task.FromResult((worker.Consumer.Restarts, worker.Consumer.Current?.Channel)),
                state => state.Restarts == 1 && state.Channel is { IsOpen: true } current && current != channel,
                Timeout,
                "consumer re-created on a new channel");

            var taskId = await SubmitAsync(customerId, (products[0].ProductId, 2));
            await WaitForTasksAsync([taskId], ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await StopAsync(hosts);
        }

        Assert.Equal((10, 2, 8), await StockAsync(products[0].ProductId));
    }

    [Fact]
    public async Task Delayed_copy_arriving_after_success_is_acknowledged_without_a_second_reservation()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var productId = products[0].ProductId;
        var taskId = await SubmitAsync(customerId, (productId, 3));
        var logs = new CapturingLoggerProvider();

        var hosts = StartWorkers(instances: 1, configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        try
        {
            await WaitForTasksAsync([taskId], ProcessingTaskStatus.Succeeded);

            // A retry copy of the same message, e.g. from an attempt whose failure raced with a successful delivery.
            var outbox = await fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking()
                .SingleAsync(e => e.EventType == EventTypes.OrderCreated && e.AggregateId == taskId.ToString()));
            await using (var channel = await fixture.OpenChannelAsync())
            {
                await channel.BasicPublishAsync(
                    RabbitMqTopology.DelayNameOf(WorkersFixture.TestRetryDelays[0]),
                    routingKey: Queue,
                    mandatory: true,
                    new BasicProperties { MessageId = outbox.OutboxEventId.ToString(), CorrelationId = outbox.CorrelationId },
                    MessageJson.SerializeToUtf8Bytes(MessageEnvelope.FromOutboxEvent(outbox)));
            }

            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(OrderMessageHandler).FullName!, LogLevel.Information, "Duplicate delivery")),
                count => count == 1,
                Timeout,
                "delayed duplicate acknowledged");
        }
        finally
        {
            await StopAsync(hosts);
        }

        Assert.Equal((10, 3, 7), await StockAsync(productId));
        Assert.Single(await MovementsAsync(productId, InventoryMovementType.Reserve));
        Assert.Single((await LoadTaskAsync(taskId)).Attempts);
        await AssertQueuesEmptyAsync(includeDeadLetter: true);
    }

    [Fact]
    public async Task Task_005_topology_upgrades_to_the_retry_topology_without_mismatch()
    {
        var virtualHost = $"upgrade-{Guid.NewGuid():N}"[..20];
        await PutManagementAsync($"api/vhosts/{virtualHost}", new { });
        await PutManagementAsync($"api/permissions/{virtualHost}/{WorkersFixture.RabbitUser}", new { configure = ".*", write = ".*", read = ".*" });

        await using var connection = await fixture.ConnectAsync(virtualHost);
        await using (var channel = await connection.CreateChannelAsync())
        {
            // What task 005 declared: exchanges, main queues with their DLQs, event bindings — nothing for retries.
            await channel.ExchangeDeclareAsync(RabbitMqTopology.Exchange, ExchangeType.Topic, durable: true);
            await channel.ExchangeDeclareAsync(RabbitMqTopology.DeadLetterExchange, ExchangeType.Direct, durable: true);
            foreach (var (queue, routingKey) in RabbitMqTopology.Bindings)
            {
                var deadLetter = RabbitMqTopology.DeadLetterQueueOf(queue);
                await channel.QueueDeclareAsync(deadLetter, true, false, false, RabbitMqTopology.DeadLetterQueueArguments());
                await channel.QueueBindAsync(deadLetter, RabbitMqTopology.DeadLetterExchange, deadLetter);
                await channel.QueueDeclareAsync(queue, true, false, false, RabbitMqTopology.MainQueueArguments(queue));
                await channel.QueueBindAsync(queue, RabbitMqTopology.Exchange, routingKey);
            }

            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
        }

        var vhost = Uri.EscapeDataString(virtualHost);
        var retry = await GetManagementAsync($"api/exchanges/{vhost}/{RabbitMqTopology.RetryExchange}");
        Assert.Equal(("direct", true), (retry["type"]!.GetValue<string>(), retry["durable"]!.GetValue<bool>()));

        var retryBindings = (await GetManagementAsync($"api/exchanges/{vhost}/{RabbitMqTopology.RetryExchange}/bindings/source")).AsArray();
        foreach (var (queue, _) in RabbitMqTopology.Bindings)
        {
            Assert.Contains(retryBindings, b => b!["destination"]!.GetValue<string>() == queue && b["routing_key"]!.GetValue<string>() == queue);
        }

        foreach (var delay in RabbitMqTopology.DefaultRetryDelays)
        {
            var name = RabbitMqTopology.DelayNameOf(delay);
            var exchange = await GetManagementAsync($"api/exchanges/{vhost}/{name}");
            Assert.Equal(("fanout", true), (exchange["type"]!.GetValue<string>(), exchange["durable"]!.GetValue<bool>()));

            var queue = await GetManagementAsync($"api/queues/{vhost}/{name}");
            var arguments = queue["arguments"]!.AsObject();
            Assert.Equal("quorum", queue["type"]!.GetValue<string>());
            Assert.Equal((int)delay.TotalMilliseconds, arguments["x-message-ttl"]!.GetValue<int>());
            Assert.Equal(RabbitMqTopology.RetryExchange, arguments["x-dead-letter-exchange"]!.GetValue<string>());
            Assert.False(arguments.ContainsKey("x-dead-letter-routing-key"));
            Assert.Equal(("at-least-once", "reject-publish"),
                (arguments["x-dead-letter-strategy"]!.GetValue<string>(), arguments["x-overflow"]!.GetValue<string>()));

            var bindings = (await GetManagementAsync($"api/queues/{vhost}/{name}/bindings")).AsArray();
            Assert.Contains(bindings, b => b!["source"]!.GetValue<string>() == name);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task RunUntilAsync(Guid taskId, ProcessingTaskStatus status, FailureInjection failures, int? maxAttempts = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxAttempts is { } max)
        {
            settings["OrderWorker:MaxAttempts"] = max.ToString();
        }

        var hosts = StartWorkers(instances: 1, configureServices: services => services.InjectFailures(failures), settings: settings);
        try
        {
            await WaitForTasksAsync([taskId], status);

            // Let the consumer settle the message (reject/ack happens right after the database commit).
            await Eventually.WaitAsync(
                () => MessageCountAsync(Queue),
                count => count == 0,
                Timeout,
                "order.processing drained");
        }
        finally
        {
            await StopAsync(hosts);
        }
    }

    private Task<List<SystemEventLog>> ProcessingFailedLogsAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == ProcessingEvents.Failed && l.TaskId == taskId)
            .ToListAsync());

    private async Task<List<BasicGetResult>> DrainAsync(string queue)
    {
        // The reject is sent right after the final-failure commit; give the broker a moment to route it.
        await Eventually.WaitAsync(() => MessageCountAsync(queue), count => count > 0, Timeout, $"message in {queue}");

        await using var channel = await fixture.OpenChannelAsync();
        var messages = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            messages.Add(message);
        }

        return messages;
    }

    private async Task AssertQueuesEmptyAsync(bool includeDeadLetter)
    {
        Assert.Equal(0u, await MessageCountAsync(Queue));
        foreach (var delayQueue in WorkersFixture.DelayQueues)
        {
            Assert.Equal(0u, await MessageCountAsync(delayQueue));
        }

        if (includeDeadLetter)
        {
            Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(Queue)));
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

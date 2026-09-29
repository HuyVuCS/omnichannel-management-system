using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OutboxPublisher;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxPublisherTests(WorkersFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Topology_is_idempotent_and_matches_the_definition()
    {
        await using (var channel = await fixture.OpenChannelAsync())
        {
            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
            await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
        }

        foreach (var (queue, routingKey) in RabbitMqTopology.Bindings)
        {
            var main = await QueueInfoAsync(queue);
            Assert.Equal("quorum", main["type"]!.GetValue<string>());
            Assert.True(main["durable"]!.GetValue<bool>());
            var arguments = main["arguments"]!.AsObject();
            Assert.Equal(RabbitMqTopology.DeadLetterExchange, arguments["x-dead-letter-exchange"]!.GetValue<string>());
            Assert.Equal(queue + ".dlq", arguments["x-dead-letter-routing-key"]!.GetValue<string>());
            Assert.Equal("at-least-once", arguments["x-dead-letter-strategy"]!.GetValue<string>());
            Assert.Equal("reject-publish", arguments["x-overflow"]!.GetValue<string>());
            Assert.Equal(10, arguments["x-delivery-limit"]!.GetValue<int>());

            var deadLetter = await QueueInfoAsync(queue + ".dlq");
            Assert.Equal("quorum", deadLetter["type"]!.GetValue<string>());
            Assert.False(deadLetter["arguments"]!.AsObject().ContainsKey("x-dead-letter-exchange"));

            var bindings = await GetJsonAsync($"api/queues/%2F/{queue}/bindings");
            Assert.Contains(bindings.AsArray(), b =>
                b!["source"]!.GetValue<string>() == RabbitMqTopology.Exchange && b["routing_key"]!.GetValue<string>() == routingKey);
        }

        var exchange = await GetJsonAsync($"api/exchanges/%2F/{RabbitMqTopology.Exchange}");
        Assert.Equal(("topic", true), (exchange["type"]!.GetValue<string>(), exchange["durable"]!.GetValue<bool>()));
        var deadLetterExchange = await GetJsonAsync($"api/exchanges/%2F/{RabbitMqTopology.DeadLetterExchange}");
        Assert.Equal(("direct", true), (deadLetterExchange["type"]!.GetValue<string>(), deadLetterExchange["durable"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Rejected_message_is_dead_lettered_to_the_queue_dlq()
    {
        await using var channel = await fixture.OpenChannelAsync();
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, EventTypes.OrderCreated, Encoding.UTF8.GetBytes("{}"));

        var delivery = await Eventually.WaitAsync(
            () => channel.BasicGetAsync(RabbitMqTopology.OrderProcessingQueue, autoAck: false),
            result => result is not null, Timeout, "message in order.processing");
        await channel.BasicRejectAsync(delivery!.DeliveryTag, requeue: false);

        await Eventually.WaitAsync(
            async () => (await channel.QueueDeclarePassiveAsync("order.processing.dlq")).MessageCount,
            count => count == 1, Timeout, "message in order.processing.dlq");
    }

    [Fact]
    public async Task Topology_mismatch_stops_the_host_at_start_up()
    {
        const string virtualHost = "mismatch-test";
        await PutJsonAsync($"api/vhosts/{virtualHost}", new { });
        await PutJsonAsync(
            $"api/permissions/{virtualHost}/{WorkersFixture.RabbitUser}",
            new { configure = ".*", write = ".*", read = ".*" });
        // A classic queue with the same name as a quorum queue of the topology.
        await PutJsonAsync($"api/queues/{virtualHost}/{RabbitMqTopology.OrderProcessingQueue}", new { durable = true });

        using var host = fixture.CreateHost("mismatch", new Dictionary<string, string?> { ["RabbitMq:VirtualHost"] = virtualHost });

        await Assert.ThrowsAsync<RabbitMqTopologyMismatchException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Pending_order_created_row_is_published_with_envelope_and_properties()
    {
        var taskId = Guid.CreateVersion7();
        var row = new OutboxEvent(
            EventTypes.OrderCreated,
            taskId.ToString(),
            """{"taskId":"%ID%","externalOrderId":"EXT-1","items":[{"productId":"p","stock":2,"unitPrice":12.5}]}""".Replace("%ID%", taskId.ToString()),
            $"corr-{Guid.NewGuid():N}",
            inventoryMovementId: null);
        await InsertAsync(row);

        await RunPublisherUntilAsync([row.OutboxEventId], OutboxEventStatus.Published);

        var stored = await LoadAsync(row.OutboxEventId);
        Assert.NotNull(stored.PublishedAt);
        Assert.Equal(0, stored.PublishAttempts);

        var message = Assert.Single(await DrainAsync(RabbitMqTopology.OrderProcessingQueue));
        var properties = message.BasicProperties;
        Assert.Equal(row.OutboxEventId.ToString(), properties.MessageId);
        Assert.Equal(row.CorrelationId, properties.CorrelationId);
        Assert.Equal(EventTypes.OrderCreated, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal(stored.CreatedAt.ToUnixTimeSeconds(), properties.Timestamp.UnixTime);
        Assert.Equal(EventTypes.OrderCreated, message.RoutingKey);

        var body = JsonNode.Parse(message.Body.Span)!.AsObject();
        Assert.Equal(
            new[] { "correlationId", "eventType", "messageId", "occurredAt", "payload", "taskId" },
            body.Select(p => p.Key).Order());
        Assert.Equal(row.OutboxEventId.ToString(), body["messageId"]!.GetValue<string>());
        Assert.Equal(row.CorrelationId, body["correlationId"]!.GetValue<string>());
        Assert.Equal(taskId, body["taskId"]!.GetValue<Guid>());
        Assert.Equal(EventTypes.OrderCreated, body["eventType"]!.GetValue<string>());
        Assert.Equal(stored.CreatedAt, body["occurredAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(JsonValueKind.Object, body["payload"]!.GetValueKind());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stored.Payload), body["payload"]));
    }

    [Fact]
    public async Task Inventory_changed_is_delivered_to_every_stock_sync_queue()
    {
        var productId = Guid.CreateVersion7();
        var payload = MessageJson.Serialize(new InventoryChangedPayload(
            productId, Guid.CreateVersion7(), InventoryMovementType.Import, 5, 0, 5, DateTimeOffset.UtcNow));
        var row = new OutboxEvent(EventTypes.InventoryChanged, productId.ToString(), payload, "corr-inv", inventoryMovementId: null);
        await InsertAsync(row);

        await RunPublisherUntilAsync([row.OutboxEventId], OutboxEventStatus.Published);

        foreach (var queue in new[]
                 {
                     RabbitMqTopology.StockSyncWebsiteQueue, RabbitMqTopology.StockSyncPosQueue, RabbitMqTopology.StockSyncMarketplaceQueue
                 })
        {
            var message = Assert.Single(await DrainAsync(queue));
            var body = JsonNode.Parse(message.Body.Span)!;
            Assert.Equal(row.OutboxEventId.ToString(), message.BasicProperties.MessageId);
            Assert.Null(body["taskId"]);
            var published = MessageJson.Deserialize<InventoryChangedPayload>(body["payload"]!.ToJsonString());
            Assert.Equal((productId, 5), (published.ProductId, published.AvailableStock));
        }

        Assert.Empty(await DrainAsync(RabbitMqTopology.OrderProcessingQueue));
    }

    [Fact]
    public async Task Two_publisher_instances_publish_every_row_exactly_once()
    {
        var rows = Enumerable.Range(0, 200).Select(n => NewOrderCreatedRow($"bulk-{n}")).ToArray();
        await InsertAsync(rows);
        var overrides = new Dictionary<string, string?> { ["OutboxPublisher:BatchSize"] = "20" };

        using var first = fixture.CreateHost("publisher-a", overrides);
        using var second = fixture.CreateHost("publisher-b", overrides);
        await Task.WhenAll(first.StartAsync(), second.StartAsync());
        try
        {
            await WaitForStatusAsync(rows.Select(r => r.OutboxEventId).ToArray(), OutboxEventStatus.Published);
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(), second.StopAsync());
        }

        var messageIds = (await DrainAsync(RabbitMqTopology.OrderProcessingQueue)).Select(m => m.BasicProperties.MessageId).ToList();
        Assert.Equal(200, messageIds.Count);
        Assert.Equal(rows.Select(r => r.OutboxEventId.ToString()).Order(), messageIds.Order());
    }

    [Fact]
    public async Task Rows_stay_pending_while_the_broker_is_down_and_are_published_when_it_is_back()
    {
        using var host = fixture.CreateHost("broker-outage", new Dictionary<string, string?> { ["OutboxPublisher:MaxAttempts"] = "1000" });
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var rows = new[] { NewOrderCreatedRow("outage-1"), NewOrderCreatedRow("outage-2") };
        var ids = rows.Select(r => r.OutboxEventId).ToArray();

        try
        {
            await fixture.StopBrokerAsync();
            await InsertAsync(rows);

            var failing = await Eventually.WaitAsync(
                () => LoadAllAsync(ids),
                loaded => loaded.All(r => r.PublishAttempts >= 2),
                Timeout,
                "publish attempts while the broker is down");
            Assert.All(failing, r =>
            {
                Assert.Equal(OutboxEventStatus.Pending, r.Status);
                Assert.NotNull(r.LastError);
                Assert.True(r.NextAttemptAt > r.UpdatedAt, "NextAttemptAt must be in the future when the failure is recorded");
            });
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await fixture.StartBrokerAsync();
        }

        await WaitForStatusAsync(ids, OutboxEventStatus.Published, TimeSpan.FromSeconds(90));
        await host.StopAsync();

        var messageIds = (await DrainAsync(RabbitMqTopology.OrderProcessingQueue)).Select(m => m.BasicProperties.MessageId);
        Assert.Equal(ids.Select(id => id.ToString()).Order(), messageIds.Order());
    }

    [Fact]
    public async Task Unroutable_row_fails_after_the_attempt_limit_and_is_logged()
    {
        var row = new OutboxEvent("test.unroutable", Guid.NewGuid().ToString(), "{}", $"corr-{Guid.NewGuid():N}", null);
        await InsertAsync(row);

        await RunPublisherUntilAsync(
            [row.OutboxEventId],
            OutboxEventStatus.Failed,
            new Dictionary<string, string?> { ["OutboxPublisher:MaxAttempts"] = "3" });

        var stored = await LoadAsync(row.OutboxEventId);
        Assert.Equal(3, stored.PublishAttempts);
        Assert.Null(stored.PublishedAt);
        Assert.Contains("unroutable", stored.LastError);
        Assert.DoesNotContain(WorkersFixture.RabbitPassword, stored.LastError);

        var logs = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.CorrelationId == row.CorrelationId)
            .ToListAsync());
        var log = Assert.Single(logs);
        Assert.Equal((SystemEventLevel.Error, OutboxPublisherService.PublishFailedEventType, OutboxPublisherService.WorkerName),
            (log.Level, log.EventType, log.WorkerName));
        var logPayload = JsonNode.Parse(log.Payload!)!;
        Assert.Equal(row.OutboxEventId, logPayload["outboxEventId"]!.GetValue<Guid>());
        Assert.Equal("test.unroutable", logPayload["eventType"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(logPayload["lastError"]!.GetValue<string>()));
    }

    private async Task RunPublisherUntilAsync(
        Guid[] ids,
        OutboxEventStatus status,
        IDictionary<string, string?>? overrides = null)
    {
        using var host = fixture.CreateHost($"publisher-{Guid.NewGuid():N}", overrides);
        await host.StartAsync();
        try
        {
            await WaitForStatusAsync(ids, status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private Task WaitForStatusAsync(Guid[] ids, OutboxEventStatus status, TimeSpan? timeout = null) =>
        Eventually.WaitAsync(
            () => LoadAllAsync(ids),
            rows => rows.Count == ids.Length && rows.All(r => r.Status == status),
            timeout ?? Timeout,
            $"{ids.Length} outbox rows {status}");

    private Task InsertAsync(params OutboxEvent[] rows) =>
        fixture.WithDbAsync(async db =>
        {
            db.OutboxEvents.AddRange(rows);
            return await db.SaveChangesAsync();
        });

    private Task<OutboxEvent> LoadAsync(Guid id) =>
        fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.OutboxEventId == id));

    private Task<List<OutboxEvent>> LoadAllAsync(Guid[] ids) =>
        fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking().Where(e => ids.Contains(e.OutboxEventId)).ToListAsync());

    private async Task<List<BasicGetResult>> DrainAsync(string queue)
    {
        await using var channel = await fixture.OpenChannelAsync();
        var messages = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            messages.Add(message);
        }

        return messages;
    }

    private async Task<JsonObject> QueueInfoAsync(string queue) => (await GetJsonAsync($"api/queues/%2F/{queue}")).AsObject();

    private async Task<JsonNode> GetJsonAsync(string path)
    {
        // The management plugin can start a little after AMQP; retry briefly.
        return await Eventually.WaitAsync(
            async () =>
            {
                using var response = await fixture.Management.GetAsync(path);
                return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
            },
            node => node is not null,
            Timeout,
            $"management API {path}") ?? throw new InvalidOperationException();
    }

    private async Task PutJsonAsync(string path, object body)
    {
        using var response = await fixture.Management.PutAsJsonAsync(path, body);
        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent,
            $"PUT {path} returned {(int)response.StatusCode}");
    }

    private static OutboxEvent NewOrderCreatedRow(string label)
    {
        var taskId = Guid.CreateVersion7();
        return new OutboxEvent(
            EventTypes.OrderCreated,
            taskId.ToString(),
            $$"""{"taskId":"{{taskId}}","label":"{{label}}"}""",
            $"corr-{label}-{Guid.NewGuid():N}",
            inventoryMovementId: null);
    }
}

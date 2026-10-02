using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Tests.Integration.Workers;

[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class OrderWorkerTests(WorkersFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Order_with_enough_stock_is_reserved_with_movement_outbox_and_attempt()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var product = products[0];
        var taskId = await SubmitAsync(customerId, (product.ProductId, 3));

        await RunWorkersUntilSucceededAsync([taskId]);

        var task = await LoadTaskAsync(taskId);
        var order = await LoadOrderAsync(task.OrderId!.Value);
        Assert.Equal(OrderStatus.Reserved, order.Status);
        var item = Assert.Single(order.Items);
        Assert.Equal((product.ProductId, 3, product.Price), (item.ProductId, item.Stock, item.UnitPrice));
        Assert.Equal(3 * product.Price, order.TotalAmount);
        Assert.Equal(OrderMessageHandler.Actor, order.CreatedBy);

        Assert.Equal((10, 3, 7), await StockAsync(product.ProductId));

        var movement = Assert.Single(await MovementsAsync(product.ProductId, InventoryMovementType.Reserve));
        Assert.Equal((InventoryReferenceType.Order, order.OrderId.ToString(), 3), (movement.ReferenceType, movement.ReferenceId, movement.Stock));

        var outbox = await fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking()
            .SingleAsync(e => e.InventoryMovementId == movement.InventoryMovementId));
        Assert.Equal((EventTypes.InventoryChanged, task.CorrelationId), (outbox.EventType, outbox.CorrelationId));
        // The same host's publisher may already have published it; it was written Pending in the reservation transaction.
        Assert.Contains(outbox.Status, new[] { OutboxEventStatus.Pending, OutboxEventStatus.Published });
        var payload = MessageJson.Deserialize<InventoryChangedPayload>(outbox.Payload);
        Assert.Equal((InventoryMovementType.Reserve, 10, 3, 7), (payload.MovementType, payload.TotalStock, payload.ReservedStock, payload.AvailableStock));

        Assert.Equal((ProcessingTaskStatus.Succeeded, order.OrderId), (task.Status, task.OrderId!.Value));
        var attempt = Assert.Single(task.Attempts);
        Assert.Equal((1, ProcessingAttemptStatus.Succeeded, OrderMessageHandler.WorkerName), (attempt.AttemptNo, attempt.Status, attempt.WorkerName));
        Assert.StartsWith("order-worker-", attempt.WorkerInstanceId);
        Assert.NotNull(attempt.FinishedAt);
    }

    [Fact]
    public async Task Concurrent_orders_for_the_last_units_never_oversell()
    {
        var (customerId, products) = await SeedAsync(stock: [5]);
        var productId = products[0].ProductId;
        var taskIds = new List<Guid>();
        for (var n = 0; n < 30; n++)
        {
            taskIds.Add(await SubmitAsync(customerId, (productId, 1)));
        }

        await RunWorkersUntilSucceededAsync(taskIds, instances: 2);

        var statuses = await fixture.WithDbAsync(db => db.Orders.AsNoTracking()
            .Where(o => o.Items.Any(i => i.ProductId == productId))
            .Select(o => o.Status)
            .ToListAsync());
        Assert.Equal(30, statuses.Count);
        Assert.Equal(5, statuses.Count(s => s == OrderStatus.Reserved));
        Assert.Equal(25, statuses.Count(s => s == OrderStatus.InsufficientStock));
        Assert.Equal((5, 5, 0), await StockAsync(productId));
        Assert.Equal(5, (await MovementsAsync(productId, InventoryMovementType.Reserve)).Count);

        var warnings = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .CountAsync(l => l.EventType == OrderProcessingEvents.InsufficientStock && taskIds.Contains(l.TaskId!.Value)));
        Assert.Equal(25, warnings);
    }

    [Fact]
    public async Task One_short_item_reserves_nothing()
    {
        var (customerId, products) = await SeedAsync(stock: [10, 1]);
        var (a, b) = (products[0].ProductId, products[1].ProductId);
        var taskId = await SubmitAsync(customerId, (a, 2), (b, 5));

        await RunWorkersUntilSucceededAsync([taskId]);

        var task = await LoadTaskAsync(taskId);
        Assert.Equal(OrderStatus.InsufficientStock, (await LoadOrderAsync(task.OrderId!.Value)).Status);
        Assert.Equal((10, 0, 10), await StockAsync(a));
        Assert.Equal((1, 0, 1), await StockAsync(b));
        Assert.Empty(await MovementsAsync(a, InventoryMovementType.Reserve));
        Assert.Empty(await MovementsAsync(b, InventoryMovementType.Reserve));

        var log = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(l => l.EventType == OrderProcessingEvents.InsufficientStock && l.TaskId == taskId));
        Assert.Equal((SystemEventLevel.Warning, task.OrderId), (log.Level, log.OrderId));
        var items = JsonNode.Parse(log.Payload!)!["items"]!.AsArray();
        Assert.Contains(items, i => i!["productId"]!.GetValue<Guid>() == b
            && i["requested"]!.GetValue<int>() == 5 && i["available"]!.GetValue<int>() == 1);
    }

    [Fact]
    public async Task Concurrent_multi_item_orders_in_opposite_order_and_imports_do_not_deadlock()
    {
        var (customerId, products) = await SeedAsync(stock: [1000, 1000]);
        var (a, b) = (products[0].ProductId, products[1].ProductId);
        var taskIds = new List<Guid>();
        for (var n = 0; n < 40; n++)
        {
            taskIds.Add(n % 2 == 0 ? await SubmitAsync(customerId, (a, 1), (b, 1)) : await SubmitAsync(customerId, (b, 1), (a, 1)));
        }

        var hosts = StartWorkers(instances: 2, concurrency: 8);
        try
        {
            // Imports touch the same rows (also in both orders) while the orders are reserved.
            var imports = Enumerable.Range(0, 10).Select(n => ImportAsync(
                $"GRN-DL-{Guid.NewGuid():N}",
                n % 2 == 0 ? [(a, 5), (b, 5)] : [(b, 5), (a, 5)]));
            await Task.WhenAll(imports);
            await WaitForTasksAsync(taskIds, ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await StopAsync(hosts);
        }

        var attempts = await fixture.WithDbAsync(db => db.ProcessingAttempts.AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId))
            .ToListAsync());
        // A deadlock would surface as a failed attempt (ErrorCode "deadlock") even though the retry then succeeds.
        Assert.All(attempts, x => Assert.Equal((1, ProcessingAttemptStatus.Succeeded, (string?)null), (x.AttemptNo, x.Status, x.ErrorCode)));
        Assert.Equal(40, attempts.Count);
        Assert.Equal((1050, 40, 1010), await StockAsync(a));
        Assert.Equal((1050, 40, 1010), await StockAsync(b));
    }

    [Fact]
    public async Task Duplicate_delivery_is_acknowledged_without_a_second_reservation()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var productId = products[0].ProductId;
        var taskId = await SubmitAsync(customerId, (productId, 2));
        var logs = new CapturingLoggerProvider();

        var hosts = StartWorkers(instances: 1, configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        try
        {
            await WaitForTasksAsync([taskId], ProcessingTaskStatus.Succeeded);

            // Redeliver the very same message (as after a publisher crash between confirm and commit).
            var outbox = await fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking()
                .SingleAsync(e => e.EventType == EventTypes.OrderCreated && e.AggregateId == taskId.ToString()));
            await fixture.PublishAsync(
                EventTypes.OrderCreated,
                MessageJson.SerializeToUtf8Bytes(MessageEnvelope.FromOutboxEvent(outbox)),
                outbox.OutboxEventId.ToString());

            await Eventually.WaitAsync(
                () => Task.FromResult(logs.Count(typeof(OrderMessageHandler).FullName!, LogLevel.Information, "Duplicate delivery")),
                count => count == 1,
                Timeout,
                "duplicate delivery acknowledged");
        }
        finally
        {
            await StopAsync(hosts);
        }

        Assert.Equal((10, 2, 8), await StockAsync(productId));
        Assert.Single(await MovementsAsync(productId, InventoryMovementType.Reserve));
        Assert.Single((await LoadTaskAsync(taskId)).Attempts);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.OrderItems.CountAsync(i => i.ProductId == productId)));
        Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.OrderProcessingQueue));
    }

    [Fact]
    public async Task Redelivery_after_a_crash_abandons_the_running_attempt_and_starts_a_new_one()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 1));
        await fixture.WithDbAsync(async db =>
        {
            // A worker started attempt #1 and died before finishing it.
            var task = await db.ProcessingTasks.Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId);
            db.ProcessingAttempts.Add(task.StartAttempt(OrderMessageHandler.WorkerName, "crashed-instance", DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

        await RunWorkersUntilSucceededAsync([taskId]);

        var attempts = (await LoadTaskAsync(taskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingAttempt.AbandonedErrorCode, "crashed-instance"),
            (attempts[0].Status, attempts[0].ErrorCode, attempts[0].WorkerInstanceId));
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[1].Status);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.OrderItems.CountAsync(i => i.ProductId == products[0].ProductId)));
    }

    [Fact]
    public async Task Poison_messages_go_to_the_dlq_and_are_logged()
    {
        var invalidJsonId = $"poison-{Guid.NewGuid():N}";
        var unknownTaskId = Guid.CreateVersion7();
        var unknownTaskMessageId = $"poison-{Guid.NewGuid():N}";
        var unknownTaskEnvelope = new MessageEnvelope(
            unknownTaskMessageId,
            "corr-poison",
            unknownTaskId,
            EventTypes.OrderCreated,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(
                new OrderCreatedPayload(
                    unknownTaskId, Guid.NewGuid(), "website", "EXT-POISON", Guid.NewGuid(),
                    [new OrderCreatedItem(Guid.NewGuid(), 1, 1m)], DateTimeOffset.UtcNow, "test"),
                MessageJson.Options));

        await fixture.PublishAsync(EventTypes.OrderCreated, Encoding.UTF8.GetBytes("{ not json"), invalidJsonId);
        await fixture.PublishAsync(EventTypes.OrderCreated, MessageJson.SerializeToUtf8Bytes(unknownTaskEnvelope), unknownTaskMessageId);
        var ordersBefore = await fixture.WithDbAsync(db => db.Orders.CountAsync());

        var hosts = StartWorkers(instances: 1);
        try
        {
            await using var channel = await fixture.OpenChannelAsync();
            await Eventually.WaitAsync(
                async () => (await channel.QueueDeclarePassiveAsync("order.processing.dlq")).MessageCount,
                count => count == 2,
                Timeout,
                "both poison messages in order.processing.dlq");
        }
        finally
        {
            await StopAsync(hosts);
        }

        var logs = await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == OrderProcessingEvents.MessageRejected)
            .ToListAsync());
        var rejectedIds = logs.Select(l => JsonNode.Parse(l.Payload!)!["messageId"]?.GetValue<string>()).ToList();
        Assert.Contains(invalidJsonId, rejectedIds);
        Assert.Contains(unknownTaskMessageId, rejectedIds);
        Assert.All(logs, l => Assert.Equal((SystemEventLevel.Error, OrderMessageHandler.WorkerName), (l.Level, l.WorkerName)));
        Assert.Equal(ordersBefore, await fixture.WithDbAsync(db => db.Orders.CountAsync()));
        Assert.False(await fixture.WithDbAsync(db => db.ProcessingAttempts.AnyAsync(a => a.TaskId == unknownTaskId)));
    }

    [Fact]
    public async Task Transient_failure_is_recorded_and_the_redelivery_succeeds()
    {
        var (customerId, products) = await SeedAsync(stock: [10]);
        var taskId = await SubmitAsync(customerId, (products[0].ProductId, 4));
        var failure = new FailureInjection { ReserveFailures = 1 };

        var hosts = StartWorkers(instances: 1, configureServices: services => services.InjectFailures(failure));
        try
        {
            await WaitForTasksAsync([taskId], ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await StopAsync(hosts);
        }

        Assert.True(failure.Triggered);
        var task = await LoadTaskAsync(taskId);
        Assert.Equal(1, task.RetryCount);
        var attempts = task.Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingErrorClassifier.Timeout), (attempts[0].Status, attempts[0].ErrorCode));
        Assert.Contains("injected", attempts[0].ErrorMessage);
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[1].Status);
        Assert.Equal((10, 4, 6), await StockAsync(products[0].ProductId));
        Assert.Equal(1, await fixture.WithDbAsync(db => db.OrderItems.CountAsync(i => i.ProductId == products[0].ProductId)));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<(Guid CustomerId, List<Product> Products)> SeedAsync(int[] stock)
    {
        var customer = new Customer($"Worker customer {Guid.NewGuid():N}", null, null, null);
        var products = stock.Select((_, n) => new Product($"W-{Guid.NewGuid():N}"[..20], $"Worker product {n}", 2.5m + n, 1, null)).ToList();
        await fixture.WithDbAsync(async db =>
        {
            db.Customers.Add(customer);
            db.Products.AddRange(products);
            db.Inventories.AddRange(products.Select(p => new Inventory(p.ProductId, 0)));
            return await db.SaveChangesAsync();
        });

        for (var n = 0; n < stock.Length; n++)
        {
            if (stock[n] > 0)
            {
                await ImportAsync($"GRN-{Guid.NewGuid():N}", [(products[n].ProductId, stock[n])]);
            }
        }

        return (customer.CustomerId, products);
    }

    private Task<ImportInventoryResponse> ImportAsync(string documentNumber, (Guid ProductId, int Stock)[] items) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IInventoryImportService>().ImportAsync(
            new ImportInventoryRequest(documentNumber, null, items.Select(i => new ImportInventoryItem(i.ProductId, i.Stock)).ToList()),
            CancellationToken.None));

    /// <summary>Order intake exactly as the API does it (task + order.created outbox row).</summary>
    private async Task<Guid> SubmitAsync(Guid customerId, params (Guid ProductId, int Stock)[] items)
    {
        var response = await fixture.WithServicesAsync(services => services.GetRequiredService<IOrderIntakeService>().SubmitAsync(
            "website",
            new SubmitOrderRequest($"W-EXT-{Guid.NewGuid():N}", customerId, items.Select(i => new SubmitOrderItem(i.ProductId, i.Stock)).ToList()),
            CancellationToken.None));
        return response.TaskId;
    }

    private List<IHost> StartWorkers(
        int instances,
        ushort concurrency = 4,
        Action<IServiceCollection>? configureServices = null,
        IDictionary<string, string?>? settings = null)
    {
        var hosts = Enumerable.Range(0, instances)
            .Select(n => fixture.CreateHost(
                $"order-worker-{n}-{Guid.NewGuid():N}"[..24],
                new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>()) { ["OrderWorker:Concurrency"] = concurrency.ToString() },
                configureServices))
            .ToList();
        Task.WaitAll(hosts.Select(h => h.StartAsync()).ToArray());
        return hosts;
    }

    private static async Task StopAsync(List<IHost> hosts)
    {
        await Task.WhenAll(hosts.Select(h => h.StopAsync()));
        hosts.ForEach(h => h.Dispose());
    }

    private async Task RunWorkersUntilSucceededAsync(IReadOnlyCollection<Guid> taskIds, int instances = 1)
    {
        var hosts = StartWorkers(instances);
        try
        {
            await WaitForTasksAsync(taskIds, ProcessingTaskStatus.Succeeded);
        }
        finally
        {
            await StopAsync(hosts);
        }
    }

    private async Task WaitForTasksAsync(IReadOnlyCollection<Guid> taskIds, ProcessingTaskStatus status)
    {
        try
        {
            await Eventually.WaitAsync(
                () => fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().CountAsync(t => taskIds.Contains(t.TaskId) && t.Status == status)),
                count => count == taskIds.Count,
                Timeout,
                $"{taskIds.Count} tasks {status}");
        }
        catch (TimeoutException timeout)
        {
            // Make failures diagnosable: which errors did the attempts record?
            var errors = await fixture.WithDbAsync(db => db.ProcessingAttempts.AsNoTracking()
                .Where(a => taskIds.Contains(a.TaskId) && a.ErrorCode != null)
                .GroupBy(a => a.ErrorCode)
                .Select(g => $"{g.Key} x{g.Count()}")
                .ToListAsync());
            throw new TimeoutException($"{timeout.Message} Attempt errors: [{string.Join(", ", errors)}]", timeout);
        }
    }

    private Task<ProcessingTask> LoadTaskAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    private Task<Order> LoadOrderAsync(Guid orderId) =>
        fixture.WithDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.OrderId == orderId));

    private Task<(int Total, int Reserved, int Available)> StockAsync(Guid productId) =>
        fixture.WithDbAsync(async db =>
        {
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            return (inventory.TotalStock, inventory.ReservedStock, inventory.AvailableStock);
        });

    private async Task<uint> MessageCountAsync(string queue)
    {
        await using var channel = await fixture.OpenChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private Task<List<InventoryMovement>> MovementsAsync(Guid productId, InventoryMovementType type) =>
        fixture.WithDbAsync(db => db.InventoryMovements.AsNoTracking()
            .Where(m => m.ProductId == productId && m.Type == type)
            .ToListAsync());
}


using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 015: every producer of a business change sends its hints without knowing about them - API services and both
/// workers, observed on a separate <c>LISTEN</c> connection while the real Workers host runs.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeHintProducerTests(WorkersFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Import_intake_reservation_stock_sync_mapping_and_manual_retry_each_send_their_hints()
    {
        var product = await SeedProductAsync();
        var rejected = await SeedProductAsync();
        var customer = new Customer($"Hint customer {Guid.NewGuid():N}", null, null, null);
        await fixture.WithDbAsync(async db =>
        {
            db.Customers.Add(customer);
            return await db.SaveChangesAsync();
        });
        var websiteId = await ChannelIdAsync("website");
        var posId = await ChannelIdAsync("pos");
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);

        // ---- producers in the API: stock import, mapping (initial sync), order intake ----
        await fixture.WithServicesAsync(services => services.GetRequiredService<IInventoryImportService>().ImportAsync(
            new ImportInventoryRequest($"GRN-{Guid.NewGuid():N}", null, [new ImportInventoryItem(product.ProductId, 10)]), CancellationToken.None));
        var imported = (await probe.WaitForAsync(
            all => all.Any(n => n.Events.Any(e => e.ProductIds?.Contains(product.ProductId) == true)), "inventory.changed of the import"))
            .Single(n => n.Events.Any(e => e.ProductIds?.Contains(product.ProductId) == true));
        Assert.Equal(RealtimeEventTypes.InventoryChanged, Assert.Single(imported.Events).Type);

        var mapping = await MapAsync(websiteId, product.ProductId);
        var mapped = Assert.Single((await probe.WaitForTaskAsync(mapping.SyncTaskId!.Value)).Events);
        Assert.Equal(
            RealtimeHint.TaskChanged(mapping.SyncTaskId.Value, ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, null, websiteId, product.ProductId),
            mapped);

        var failing = await MapAsync(posId, rejected.ProductId);

        var submitted = await fixture.WithServicesAsync(services => services.GetRequiredService<IOrderIntakeService>().SubmitAsync(
            "website",
            new SubmitOrderRequest($"HINT-{Guid.NewGuid():N}", customer.CustomerId, [new SubmitOrderItem(product.ProductId, 3)]),
            CancellationToken.None));
        var accepted = Assert.Single((await probe.WaitForTaskAsync(submitted.TaskId)).Events);
        Assert.Equal((RealtimeEventTypes.ProcessingTaskChanged, "OrderProcessing", "Pending", (Guid?)null), (accepted.Type, accepted.TaskType, accepted.Status, accepted.OrderId));

        // ---- producers in the Workers host: Order Worker and Stock Sync Worker ----
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.ProductId == rejected.ProductId
                ? throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "unknown external product (test)", isTransient: false)
                : Task.CompletedTask
        };
        var host = fixture.CreateHost(
            $"hint-workers-{Guid.NewGuid():N}"[..24],
            new Dictionary<string, string?> { ["StockSyncWorker:CircuitBreaker:MinimumThroughput"] = "1000" },
            services => services.UseChannelAdapter(channel));
        await host.StartAsync();
        try
        {
            await probe.WaitForTaskAsync(submitted.TaskId, "Succeeded");
            await probe.WaitForTaskAsync(mapping.SyncTaskId.Value, "Succeeded");
            await probe.WaitForTaskAsync(failing.SyncTaskId!.Value, "Failed");
            // The stock change of the reservation is synced to the channel that sells the product.
            await probe.WaitForAsync(
                all => all.SelectMany(n => n.Events).Any(e =>
                    e.TaskType == "StockSync" && e.ProductId == product.ProductId && e.TaskId != mapping.SyncTaskId && e.Status == "Succeeded"),
                "stock sync of the reservation");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }

        // Order Worker: Pending -> InProgress -> Succeeded, and the reservation is ONE notification with its three hints.
        Assert.Equal(["Pending", "InProgress", "Succeeded"], StatusesOf(probe, submitted.TaskId));
        var reservation = probe.Notifications.Single(n => n.Events.Any(e => e.TaskId == submitted.TaskId && e.Status == "Succeeded"));
        Assert.Equal(
            [RealtimeEventTypes.OrderCreated, RealtimeEventTypes.InventoryChanged, RealtimeEventTypes.ProcessingTaskChanged],
            reservation.Events.Select(e => e.Type));
        var order = await fixture.WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.CustomerId == customer.CustomerId));
        Assert.Equal(RealtimeHint.OrderCreated(order.OrderId, websiteId, OrderStatus.Reserved), reservation.Events[0]);
        Assert.Equal([product.ProductId], reservation.Events[1].ProductIds);
        Assert.Equal(order.OrderId, reservation.Events[2].OrderId);

        // Stock Sync Worker: success and final failure, each transition one hint with channel and product.
        Assert.Equal(["Pending", "InProgress", "Succeeded"], StatusesOf(probe, mapping.SyncTaskId.Value));
        Assert.Equal(["Pending", "InProgress", "Failed"], StatusesOf(probe, failing.SyncTaskId.Value));
        var failed = probe.Events.Single(e => e.TaskId == failing.SyncTaskId && e.Status == "Failed");
        Assert.Equal(((Guid?)posId, (Guid?)rejected.ProductId, "StockSync"), (failed.ChannelId, failed.ProductId, failed.TaskType));

        // A task created by the worker itself (from inventory.changed) announces itself as well.
        var syncOfReservation = probe.Events.Where(e => e.TaskType == "StockSync" && e.ProductId == product.ProductId && e.TaskId != mapping.SyncTaskId).ToList();
        Assert.Contains(syncOfReservation, e => e.Status == "Pending");
        Assert.All(syncOfReservation, e => Assert.Equal(websiteId, e.ChannelId));

        // ---- manual retry (API): Failed -> Pending ----
        await fixture.WithServicesAsync(services => services.GetRequiredService<IProcessingTaskService>().RetryAsync(failing.SyncTaskId.Value, CancellationToken.None));
        await probe.WaitForAsync(_ => StatusesOf(probe, failing.SyncTaskId.Value).Count == 4, "hint of the manual retry");
        Assert.Equal(["Pending", "InProgress", "Failed", "Pending"], StatusesOf(probe, failing.SyncTaskId.Value));

        // No hint carries anything but types and ids; outbox publishing and attempts produced none. (Task 017: the alert
        // writer announces the alerts these changes open - the failed stock sync, the product that became low.)
        Assert.All(probe.Notifications, n => Assert.NotNull(n.Batch));
        Assert.All(probe.Events, e => Assert.Contains(e.Type, new[]
        {
            RealtimeEventTypes.OrderCreated, RealtimeEventTypes.InventoryChanged, RealtimeEventTypes.ProcessingTaskChanged,
            RealtimeEventTypes.AlertChanged, RealtimeEventTypes.LowStockCreated
        }));
        var failedSync = Assert.Single(probe.Events, e => e.Type == RealtimeEventTypes.AlertChanged && e.AlertType == "StockSyncFailed");
        Assert.Equal(("Open", (Guid?)posId, (Guid?)rejected.ProductId), (failedSync.Status, failedSync.ChannelId, failedSync.ProductId));
    }

    private static List<string?> StatusesOf(HintProbe probe, Guid taskId) =>
        probe.Events.Where(e => e.TaskId == taskId).Select(e => e.Status).ToList();

    private async Task<Product> SeedProductAsync()
    {
        var product = new Product($"HP-{Guid.NewGuid():N}"[..20], "Hint product", 2m, 0, null);
        await fixture.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 0));
            return await db.SaveChangesAsync();
        });
        return product;
    }

    private Task<Guid> ChannelIdAsync(string slug) =>
        fixture.WithDbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == slug).Select(c => c.ChannelId).SingleAsync());

    private Task<ChannelProductSavedResponse> MapAsync(Guid channelId, Guid productId) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            channelId, new CreateChannelProductRequest(productId, null, $"EXT-{Guid.NewGuid():N}"[..12]), CancellationToken.None));
}

using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Alerts;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.StockSyncWorker;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 017: alerts are opened, refreshed and resolved by the changes that cause them - product and stock changes of the
/// API services, the Order Worker and the Stock Sync Worker - against real PostgreSQL and RabbitMQ. Alert hints are observed
/// on a separate <c>LISTEN</c> connection.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AlertLifecycleTests(WorkersFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- low stock ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Low_stock_alert_follows_stock_and_threshold_and_each_return_of_the_condition_is_a_new_alert()
    {
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);
        var customerId = await SeedCustomerAsync();

        // A new product has no stock: low from its first moment.
        var product = await CreateProductAsync(threshold: 5);
        var key = AlertDedupKey.LowStock(product.ProductId);
        var first = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (AlertType.LowStock, AlertStatus.Open, (Guid?)product.ProductId, 1),
            (first.Type, first.Status, first.ProductId, first.OccurrenceCount));
        Assert.Equal(
            ((Guid?)null, (Guid?)null, (Guid?)null, (string?)null, (DateTimeOffset?)null, (DateTimeOffset?)null, (string?)null),
            (first.ChannelId, first.TaskId, first.OrderId, first.ErrorCode, first.ResolvedAt, first.AcknowledgedAt, first.AcknowledgedBy));
        Assert.Equal(first.FirstOccurredAt, first.LastOccurredAt);

        // Import above the threshold (10 > 5): resolved.
        await ImportAsync((product.ProductId, 10));
        var resolvedFirst = Assert.Single(await AlertsAsync(key));
        Assert.Equal((first.AlertId, AlertStatus.Resolved), (resolvedFirst.AlertId, resolvedFirst.Status));
        Assert.NotNull(resolvedFirst.ResolvedAt);

        // An order reserving down to the threshold (10 - 5 = 5, inclusive): low again, as a new alert.
        var toThreshold = await SubmitAsync(customerId, (product.ProductId, 5));
        await RunOrderWorkersUntilSucceededAsync([toThreshold]);
        var afterOrder = await AlertsAsync(key);
        Assert.Equal(2, afterOrder.Count);
        var second = afterOrder[1];
        Assert.NotEqual(first.AlertId, second.AlertId);
        Assert.Equal((AlertStatus.Open, 1), (second.Status, second.OccurrenceCount));
        Assert.True(second.FirstOccurredAt >= resolvedFirst.ResolvedAt);

        // Still low after a further reservation (5 - 2 = 3): a state, not a counter - the row is left as it is.
        var further = await SubmitAsync(customerId, (product.ProductId, 2));
        await RunOrderWorkersUntilSucceededAsync([further]);
        var unchanged = await AlertsAsync(key);
        Assert.Equal(2, unchanged.Count);
        Assert.Equal(
            (second.AlertId, AlertStatus.Open, 1, second.LastOccurredAt),
            (unchanged[1].AlertId, unchanged[1].Status, unchanged[1].OccurrenceCount, unchanged[1].LastOccurredAt));

        // Threshold lowered below the stock (3 > 2): resolved. Raised to the stock again (3 <= 3): a third alert.
        await UpdateThresholdAsync(product, 2);
        Assert.Equal(AlertStatus.Resolved, (await AlertsAsync(key))[1].Status);
        await UpdateThresholdAsync(product, 3);
        var all = await AlertsAsync(key);
        Assert.Equal(3, all.Count);
        var third = all[2];
        Assert.Equal([AlertStatus.Resolved, AlertStatus.Resolved, AlertStatus.Open], all.Select(a => a.Status));
        Assert.Equal((3, 3), await StockAndThresholdAsync(product.ProductId));

        // The hints of this product's alerts, in order: an opened alert announces itself twice, the unchanged low state of
        // the further reservation not at all.
        RealtimeHint Changed(Alert alert, AlertStatus status) =>
            RealtimeHint.AlertChanged(alert.AlertId, AlertType.LowStock, status, product.ProductId, null);
        RealtimeHint[] expected =
        [
            Changed(first, AlertStatus.Open), RealtimeHint.LowStockCreated(first.AlertId, product.ProductId),
            Changed(first, AlertStatus.Resolved),
            Changed(second, AlertStatus.Open), RealtimeHint.LowStockCreated(second.AlertId, product.ProductId),
            Changed(second, AlertStatus.Resolved),
            Changed(third, AlertStatus.Open), RealtimeHint.LowStockCreated(third.AlertId, product.ProductId)
        ];
        await probe.WaitForAsync(_ => AlertHintsOf(probe, product.ProductId).Count >= expected.Length, "alert hints of the product");
        Assert.Equal(expected, AlertHintsOf(probe, product.ProductId));

        // An opened alert is one notification with both hints: they are never seen apart.
        var opened = probe.Notifications.Single(n => n.Events.Any(e => e.Type == RealtimeEventTypes.LowStockCreated && e.AlertId == second.AlertId));
        Assert.Equal([expected[3], expected[4]], opened.Events);
    }

    [Fact]
    public async Task New_product_with_threshold_zero_is_low_too_and_its_first_stock_resolves_the_alert()
    {
        var product = await CreateProductAsync(threshold: 0);
        var key = AlertDedupKey.LowStock(product.ProductId);
        Assert.Equal(AlertStatus.Open, Assert.Single(await AlertsAsync(key)).Status);

        await ImportAsync((product.ProductId, 1));

        Assert.Equal(AlertStatus.Resolved, Assert.Single(await AlertsAsync(key)).Status);
    }

    [Fact]
    public async Task Concurrent_reservations_crossing_the_threshold_all_succeed_at_the_first_attempt_and_open_exactly_one_alert()
    {
        var customerId = await SeedCustomerAsync();
        var product = await CreateProductAsync(threshold: 15);
        await ImportAsync((product.ProductId, 20));
        var key = AlertDedupKey.LowStock(product.ProductId);
        Assert.Equal(AlertStatus.Resolved, Assert.Single(await AlertsAsync(key)).Status);

        // 12 orders of one unit: the fifth reservation reaches the threshold, the others see the product low already.
        var taskIds = new List<Guid>();
        for (var n = 0; n < 12; n++)
        {
            taskIds.Add(await SubmitAsync(customerId, (product.ProductId, 1)));
        }

        await RunOrderWorkersUntilSucceededAsync(taskIds, instances: 2, concurrency: 8);

        var orders = await fixture.WithDbAsync(db => db.Orders.AsNoTracking()
            .Where(o => o.Items.Any(i => i.ProductId == product.ProductId))
            .Select(o => o.Status)
            .ToListAsync());
        Assert.Equal(12, orders.Count);
        Assert.All(orders, status => Assert.Equal(OrderStatus.Reserved, status));
        Assert.Equal((8, 15), await StockAndThresholdAsync(product.ProductId));

        // No reservation was retried: the alert write neither failed (23505) nor deadlocked.
        var attempts = await fixture.WithDbAsync(db => db.ProcessingAttempts.AsNoTracking().Where(a => taskIds.Contains(a.TaskId)).ToListAsync());
        Assert.Equal(12, attempts.Count);
        Assert.All(attempts, a => Assert.Equal((1, ProcessingAttemptStatus.Succeeded, (string?)null), (a.AttemptNo, a.Status, a.ErrorCode)));

        var alerts = await AlertsAsync(key);
        Assert.Equal(2, alerts.Count);
        Assert.Equal((AlertStatus.Open, 1), (Assert.Single(alerts, a => a.Status != AlertStatus.Resolved).Status, alerts[1].OccurrenceCount));
    }

    [Fact]
    public async Task Low_stock_rule_in_sql_text_selects_the_same_rows_as_the_shared_predicate()
    {
        // (total, reserved, threshold) around the boundary; AvailableStock = total - reserved is computed by the database.
        (int Total, int Reserved, int Threshold)[] cases =
        [
            (0, 0, 0), (1, 0, 0), (5, 0, 5), (6, 0, 5), (4, 0, 5), (10, 5, 5), (10, 4, 5), (10, 6, 5), (10, 10, 0), (100, 0, 99), (100, 1, 99)
        ];
        var products = cases.Select((c, n) => new Product($"LSR-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), $"Rule {n}", 1m, c.Threshold, null)).ToList();
        var ids = products.Select(p => p.ProductId).ToArray();

        var (byPredicate, bySql) = await fixture.WithDbAsync(async db =>
        {
            db.Products.AddRange(products);
            db.Inventories.AddRange(products.Select((p, n) => new Inventory(p.ProductId, cases[n].Total)));
            await db.SaveChangesAsync();
            for (var n = 0; n < cases.Length; n++)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "Inventories" SET "ReservedStock" = {cases[n].Reserved} WHERE "ProductId" = {ids[n]}""");
            }

            var predicate = await db.Inventories.AsNoTracking()
                .Where(i => ids.Contains(i.ProductId))
                .Where(InventoryPredicates.IsLowStock)
                .Select(i => i.ProductId)
                .ToListAsync();
            var sql = await db.Database
                .SqlQueryRaw<Guid>(
                    $"""
                     SELECT i."ProductId" AS "Value"
                     FROM "Inventories" i JOIN "Products" p ON p."ProductId" = i."ProductId"
                     WHERE i."ProductId" = ANY(@ids) AND ({InventoryPredicates.IsLowStockSql})
                     """,
                    new NpgsqlParameter("ids", ids))
                .ToListAsync();
            return (predicate, sql);
        });

        Assert.Equal(byPredicate.Order(), bySql.Order());
        // Both sides of the boundary are among the rows (the comparison would also hold for two empty sets).
        var low = cases.Select((c, n) => (Low: c.Total - c.Reserved <= c.Threshold, Id: ids[n])).ToList();
        Assert.Equal(low.Where(x => x.Low).Select(x => x.Id).Order(), bySql.Order());
        Assert.Contains(low, x => x.Low);
        Assert.Contains(low, x => !x.Low);
    }

    // ---- insufficient stock ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejected_order_opens_an_alert_for_the_short_product_only_and_the_next_import_of_that_product_resolves_it()
    {
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);
        var customerId = await SeedCustomerAsync();
        // "enough" is reserved first (lower ProductId) and rolled back when "short" turns out short.
        var created = new[] { await CreateProductAsync(threshold: 8), await CreateProductAsync(threshold: 8) }.OrderBy(p => p.ProductId).ToList();
        var (enough, shortOne) = (created[0], created[1]);
        await ImportAsync((enough.ProductId, 10), (shortOne.ProductId, 1));
        var key = AlertDedupKey.InsufficientStock(shortOne.ProductId);

        var firstTask = await SubmitAsync(customerId, (enough.ProductId, 2), (shortOne.ProductId, 5));
        await RunOrderWorkersUntilSucceededAsync([firstTask]);
        var firstOrder = await OrderOfTaskAsync(firstTask);
        Assert.Equal(OrderStatus.InsufficientStock, firstOrder.Status);

        var alert = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (AlertType.InsufficientStock, AlertStatus.Open, (Guid?)shortOne.ProductId, (Guid?)firstOrder.OrderId, 1),
            (alert.Type, alert.Status, alert.ProductId, alert.OrderId, alert.OccurrenceCount));
        Assert.Equal(((Guid?)null, (Guid?)null, (string?)null), (alert.ChannelId, alert.TaskId, alert.ErrorCode));
        // The product that had enough gets none - and the rolled-back reservation (10 - 2 = 8 <= 8) left no low-stock alert.
        Assert.Empty(await AlertsAsync(AlertDedupKey.InsufficientStock(enough.ProductId)));
        Assert.Equal(AlertStatus.Resolved, Assert.Single(await AlertsAsync(AlertDedupKey.LowStock(enough.ProductId))).Status);
        Assert.Equal((10, 8), await StockAndThresholdAsync(enough.ProductId));

        // A second rejected order: the same alert, counted, with the latest order.
        var secondTask = await SubmitAsync(customerId, (shortOne.ProductId, 3));
        await RunOrderWorkersUntilSucceededAsync([secondTask]);
        var secondOrder = await OrderOfTaskAsync(secondTask);
        var refreshed = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (alert.AlertId, AlertStatus.Open, (Guid?)secondOrder.OrderId, 2, alert.FirstOccurredAt),
            (refreshed.AlertId, refreshed.Status, refreshed.OrderId, refreshed.OccurrenceCount, refreshed.FirstOccurredAt));
        Assert.True(refreshed.LastOccurredAt > alert.LastOccurredAt);

        // Stock of another product arrives: unchanged. Stock of this product arrives: resolved.
        await ImportAsync((enough.ProductId, 5));
        Assert.Equal((AlertStatus.Open, 2), ((await AlertsAsync(key))[0].Status, (await AlertsAsync(key))[0].OccurrenceCount));
        await ImportAsync((shortOne.ProductId, 1));
        var resolved = Assert.Single(await AlertsAsync(key));
        Assert.Equal((AlertStatus.Resolved, (Guid?)secondOrder.OrderId, 2), (resolved.Status, resolved.OrderId, resolved.OccurrenceCount));
        Assert.NotNull(resolved.ResolvedAt);

        // An order that is rejected afterwards opens a new alert.
        var thirdTask = await SubmitAsync(customerId, (shortOne.ProductId, 50));
        await RunOrderWorkersUntilSucceededAsync([thirdTask]);
        var reopened = await AlertsAsync(key);
        Assert.Equal([AlertStatus.Resolved, AlertStatus.Open], reopened.Select(a => a.Status));
        Assert.Equal(1, reopened[1].OccurrenceCount);

        RealtimeHint Changed(Alert a, AlertStatus status) =>
            RealtimeHint.AlertChanged(a.AlertId, AlertType.InsufficientStock, status, shortOne.ProductId, null);
        RealtimeHint[] expected =
        [
            Changed(alert, AlertStatus.Open), Changed(alert, AlertStatus.Open), Changed(alert, AlertStatus.Resolved), Changed(reopened[1], AlertStatus.Open)
        ];
        await probe.WaitForAsync(
            _ => AlertHintsOf(probe, shortOne.ProductId, AlertType.InsufficientStock).Count >= expected.Length, "insufficient-stock hints");
        Assert.Equal(expected, AlertHintsOf(probe, shortOne.ProductId, AlertType.InsufficientStock));
        // The product with enough stock: its low-stock alert of the creation and its resolution by the first import, nothing else.
        Assert.Equal(3, AlertHintsOf(probe, enough.ProductId).Count);
    }

    // ---- stock sync -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_sync_failure_alert_is_per_channel_and_product_keeps_its_acknowledgement_and_any_later_success_of_the_pair_resolves_it()
    {
        var product = await CreateProductAsync(threshold: 0);
        var (websiteId, posId) = (await ChannelIdAsync("website"), await ChannelIdAsync("pos"));
        var key = AlertDedupKey.StockSyncFailed(websiteId, product.ProductId);
        var rejecting = true;
        var channel = new RecordingChannelClient
        {
            Behaviour = (update, _) => update.ChannelSlug == "website" && update.ProductId == product.ProductId && Volatile.Read(ref rejecting)
                ? throw new ChannelApiException(ChannelApiErrorCodes.Rejected, "unknown external product (test)", isTransient: false)
                : Task.CompletedTask
        };

        // The initial sync of the mapping fails for good.
        var mapping = await MapAsync(websiteId, product.ProductId);
        await RunStockSyncWorkersAsync(channel, () => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 1 && tasks.All(t => t.IsTerminal)));
        var alert = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (AlertType.StockSyncFailed, AlertStatus.Open, (Guid?)websiteId, (Guid?)product.ProductId, mapping.SyncTaskId, ChannelApiErrorCodes.Rejected, 1),
            (alert.Type, alert.Status, alert.ChannelId, alert.ProductId, alert.TaskId, alert.ErrorCode, alert.OccurrenceCount));
        Assert.Null(alert.OrderId);
        // Per channel and product, not per task.
        Assert.Empty(await AlertsAsync(AlertDedupKey.ProcessingFailed(mapping.SyncTaskId!.Value)));

        // A manager has seen it.
        var acknowledged = await fixture.WithServicesAsync(services =>
            services.GetRequiredService<IAlertService>().AcknowledgeAsync(alert.AlertId, CancellationToken.None));
        Assert.Equal(AlertStatus.Acknowledged, acknowledged.Status);

        // A second task of the pair fails (the stock changed): the same alert, refreshed - and still acknowledged.
        await ImportAsync((product.ProductId, 4));
        await RunStockSyncWorkersAsync(channel, () => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 2 && tasks.All(t => t.IsTerminal)));
        var secondTask = (await SyncTasksAsync(product.ProductId)).Single(t => t.TaskId != mapping.SyncTaskId);
        Assert.Equal(ProcessingTaskStatus.Failed, secondTask.Status);
        var refreshed = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (alert.AlertId, AlertStatus.Acknowledged, (Guid?)secondTask.TaskId, ChannelApiErrorCodes.Rejected, 2, alert.FirstOccurredAt),
            (refreshed.AlertId, refreshed.Status, refreshed.TaskId, refreshed.ErrorCode, refreshed.OccurrenceCount, refreshed.FirstOccurredAt));
        Assert.Equal((acknowledged.AcknowledgedAt, true), (refreshed.AcknowledgedAt, refreshed.AcknowledgedBy is not null));
        Assert.True(refreshed.LastOccurredAt > alert.LastOccurredAt);

        // A task of another channel succeeds for the same product: this channel's alert is not touched.
        await MapAsync(posId, product.ProductId);
        await RunStockSyncWorkersAsync(channel, () => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 3 && tasks.All(t => t.IsTerminal)));
        Assert.Equal(ProcessingTaskStatus.Succeeded, (await SyncTasksAsync(product.ProductId)).Single(t => t.ChannelId == posId).Status);
        Assert.Equal((AlertStatus.Acknowledged, 2), ((await AlertsAsync(key))[0].Status, (await AlertsAsync(key))[0].OccurrenceCount));
        Assert.Empty(await AlertsAsync(AlertDedupKey.StockSyncFailed(posId, product.ProductId)));

        // The channel accepts again: another task of the pair - not one of the failed ones - succeeds and ends the alert.
        Volatile.Write(ref rejecting, false);
        await ImportAsync((product.ProductId, 1));
        await RunStockSyncWorkersAsync(channel, () => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 5 && tasks.All(t => t.IsTerminal)));
        var website = (await SyncTasksAsync(product.ProductId)).Where(t => t.ChannelId == websiteId).ToList();
        Assert.Equal((2, 1), (website.Count(t => t.Status == ProcessingTaskStatus.Failed), website.Count(t => t.Status == ProcessingTaskStatus.Succeeded)));
        Assert.DoesNotContain(website, t => t.Status == ProcessingTaskStatus.Succeeded && (t.TaskId == mapping.SyncTaskId || t.TaskId == secondTask.TaskId));
        var resolved = Assert.Single(await AlertsAsync(key));
        Assert.Equal((AlertStatus.Resolved, (Guid?)secondTask.TaskId, 2), (resolved.Status, resolved.TaskId, resolved.OccurrenceCount));
        Assert.Equal((true, acknowledged.AcknowledgedAt), (resolved.ResolvedAt is not null, resolved.AcknowledgedAt));
    }

    // ---- deactivated mapping (task 018 Part A6) ------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_a_mapping_resolves_the_stock_sync_alert_of_its_pair_with_a_hint_and_a_failing_reactivation_opens_a_new_alert()
    {
        var (product, mapping, key) = await FailedPairAsync();
        var failed = Assert.Single(await AlertsAsync(key));
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);

        // Active -> Inactive: the pair is not synced any more, its failure needs no attention.
        var deactivated = await UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Inactive);
        Assert.Equal((ChannelProductStatus.Inactive, (Guid?)null), (deactivated.Status, deactivated.SyncTaskId));
        var resolved = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (failed.AlertId, AlertStatus.Resolved, failed.TaskId, failed.OccurrenceCount),
            (resolved.AlertId, resolved.Status, resolved.TaskId, resolved.OccurrenceCount));
        Assert.NotNull(resolved.ResolvedAt);
        await probe.WaitForAsync(_ => AlertHintsOf(probe, product.ProductId, AlertType.StockSyncFailed).Count >= 1, "hint of the resolved alert");
        var hint = Assert.Single(AlertHintsOf(probe, product.ProductId, AlertType.StockSyncFailed));
        Assert.Equal(
            (RealtimeEventTypes.AlertChanged, (Guid?)failed.AlertId, AlertStatus.Resolved.ToString(), (Guid?)mapping.ChannelId),
            (hint.Type, hint.AlertId, hint.Status, hint.ChannelId));

        // Inactive -> Active starts the initial sync again; the channel still rejects the SKU: a new alert, the old row stays resolved.
        var reactivated = await UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Active);
        Assert.NotNull(reactivated.SyncTaskId);
        await RunSimulatedStockSyncAsync(() => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 2 && tasks.All(t => t.IsTerminal)));
        var alerts = await AlertsAsync(key);
        Assert.Equal(2, alerts.Count);
        Assert.Equal((failed.AlertId, AlertStatus.Resolved), (alerts[0].AlertId, alerts[0].Status));
        Assert.Equal(
            (AlertStatus.Open, reactivated.SyncTaskId, ChannelApiErrorCodes.Rejected, 1),
            (alerts[1].Status, alerts[1].TaskId, alerts[1].ErrorCode, alerts[1].OccurrenceCount));
        Assert.NotEqual(failed.AlertId, alerts[1].AlertId);
    }

    [Fact]
    public async Task Deactivating_a_mapping_without_an_alert_writes_no_alert_and_sends_no_alert_hint()
    {
        // Threshold 0 without stock: the product has an open low-stock alert, whose resolution marks the end of the probe's wait.
        var product = await CreateProductAsync(threshold: 0);
        var websiteId = await ChannelIdAsync("website");
        var mapping = await MapAsync(websiteId, product.ProductId);
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);

        var deactivated = await UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Inactive);

        Assert.Equal(ChannelProductStatus.Inactive, deactivated.Status);
        Assert.Empty(await AlertsAsync(AlertDedupKey.StockSyncFailed(websiteId, product.ProductId)));
        // A later alert change of the product arrives; nothing about a stock-sync alert was sent before it.
        await ImportAsync((product.ProductId, 3));
        await probe.WaitForAsync(
            _ => AlertHintsOf(probe, product.ProductId, AlertType.LowStock).Any(h => h.Status == AlertStatus.Resolved.ToString()),
            "hint of the resolved low-stock alert");
        Assert.Empty(AlertHintsOf(probe, product.ProductId, AlertType.StockSyncFailed));
        Assert.DoesNotContain(probe.Events, e => e.AlertId is not null && e.ChannelId == websiteId && e.ProductId == product.ProductId);
    }

    [Fact]
    public async Task Mapping_update_that_keeps_it_active_leaves_the_stock_sync_alert_unchanged()
    {
        var (product, mapping, key) = await FailedPairAsync();
        var failed = Assert.Single(await AlertsAsync(key));

        // Another external SKU, still Active: a new sync is requested, the alert waits for its result.
        var updated = await UpdateMappingAsync(mapping, $"{ChannelSimulationOptions.PermanentErrorSkuPrefix}{Guid.NewGuid():N}", ChannelProductStatus.Active);

        Assert.Equal(ChannelProductStatus.Active, updated.Status);
        Assert.NotNull(updated.SyncTaskId);
        var unchanged = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (failed.AlertId, AlertStatus.Open, failed.TaskId, 1, (DateTimeOffset?)null),
            (unchanged.AlertId, unchanged.Status, unchanged.TaskId, unchanged.OccurrenceCount, unchanged.ResolvedAt));
        Assert.Equal(product.ProductId, unchanged.ProductId);
    }

    [Fact]
    public async Task Update_of_a_mapping_that_is_inactive_already_leaves_a_late_stock_sync_alert_open()
    {
        // The known edge: an attempt that was running when the mapping was switched off fails for good afterwards and opens
        // the alert again. Only the switch-off itself resolves; a later change of the inactive mapping does not.
        var product = await CreateProductAsync(threshold: 0);
        var websiteId = await ChannelIdAsync("website");
        var mapping = await MapAsync(websiteId, product.ProductId);
        await UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Inactive);
        var key = AlertDedupKey.StockSyncFailed(websiteId, product.ProductId);
        await fixture.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<OmnichannelDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await services.GetRequiredService<AlertStore>().OpenOrRefreshAsync(
                [AlertOccurrence.StockSyncFailed(websiteId, product.ProductId, mapping.SyncTaskId!.Value, ChannelApiErrorCodes.Rejected)],
                CancellationToken.None);
            await transaction.CommitAsync();
            return 0;
        });
        var late = Assert.Single(await AlertsAsync(key));

        var updated = await UpdateMappingAsync(mapping, $"EXT-{Guid.NewGuid():N}"[..12], ChannelProductStatus.Inactive);

        Assert.Equal((ChannelProductStatus.Inactive, (Guid?)null), (updated.Status, updated.SyncTaskId));
        var unchanged = Assert.Single(await AlertsAsync(key));
        Assert.Equal((late.AlertId, AlertStatus.Open, (DateTimeOffset?)null), (unchanged.AlertId, unchanged.Status, unchanged.ResolvedAt));
    }

    [Fact]
    public async Task Rolled_back_deactivation_leaves_the_mapping_active_and_the_stock_sync_alert_open()
    {
        var (product, mapping, key) = await FailedPairAsync();
        var failed = Assert.Single(await AlertsAsync(key));
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);

        // Another product of the channel owns this external product id: the deactivation that also takes it is refused as a whole.
        var other = await CreateProductAsync(threshold: 0);
        var takenId = $"TAKEN-{Guid.NewGuid():N}";
        await MapAsync(mapping.ChannelId, other.ProductId, externalProductId: takenId, externalSku: null);
        var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
            UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Inactive, externalProductId: takenId));
        Assert.Equal(ChannelProductErrorCodes.ExternalProductIdTaken, conflict.ErrorCode);

        var stored = await fixture.WithDbAsync(db => db.ChannelProducts.AsNoTracking().SingleAsync(m => m.ChannelProductId == mapping.ChannelProductId));
        Assert.Equal((ChannelProductStatus.Active, (string?)null), (stored.Status, stored.ExternalProductId));
        var unchanged = Assert.Single(await AlertsAsync(key));
        Assert.Equal((failed.AlertId, AlertStatus.Open, (DateTimeOffset?)null), (unchanged.AlertId, unchanged.Status, unchanged.ResolvedAt));

        // The same deactivation without the conflict is applied; the only stock-sync alert hint of the pair is its own.
        await UpdateMappingAsync(mapping, mapping.ExternalSku, ChannelProductStatus.Inactive);
        await probe.WaitForAsync(_ => AlertHintsOf(probe, product.ProductId, AlertType.StockSyncFailed).Count >= 1, "hint of the resolved alert");
        Assert.Equal(AlertStatus.Resolved.ToString(), Assert.Single(AlertHintsOf(probe, product.ProductId, AlertType.StockSyncFailed)).Status);
    }

    // ---- other task types -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_order_task_opens_an_alert_per_task_that_survives_the_manual_retry_and_ends_when_the_task_succeeds()
    {
        var customerId = await SeedCustomerAsync();
        var product = await CreateProductAsync(threshold: 0);
        await ImportAsync((product.ProductId, 10));
        var taskId = await SubmitAsync(customerId, (product.ProductId, 4));
        var key = AlertDedupKey.ProcessingFailed(taskId);
        // Attempt 1 hits a permanent error; afterwards the "bug" is fixed.
        var failures = new FailureInjection
        {
            OnlyTaskId = taskId,
            ReserveFailures = 1,
            ReserveException = () => new PostgresException("new row violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)
        };

        await RunOrderWorkersAsync(
            () => WaitForTasksAsync([taskId], ProcessingTaskStatus.Failed),
            configureServices: services => services.InjectFailures(failures));

        var alert = Assert.Single(await AlertsAsync(key));
        Assert.Equal(
            (AlertType.ProcessingFailed, AlertStatus.Open, (Guid?)taskId, "check_violation", 1),
            (alert.Type, alert.Status, alert.TaskId, alert.ErrorCode, alert.OccurrenceCount));
        Assert.Equal(((Guid?)null, (Guid?)null, (Guid?)null), (alert.ProductId, alert.ChannelId, alert.OrderId));

        // The manual retry itself does not touch the alert: the task has not succeeded yet.
        await fixture.WithServicesAsync(services => services.GetRequiredService<IProcessingTaskService>().RetryAsync(taskId, CancellationToken.None));
        var afterRetry = Assert.Single(await AlertsAsync(key));
        Assert.Equal((AlertStatus.Open, 1, alert.LastOccurredAt), (afterRetry.Status, afterRetry.OccurrenceCount, afterRetry.LastOccurredAt));

        await RunOrderWorkersUntilSucceededAsync([taskId]);

        var resolved = Assert.Single(await AlertsAsync(key));
        Assert.Equal((alert.AlertId, AlertStatus.Resolved, 1), (resolved.AlertId, resolved.Status, resolved.OccurrenceCount));
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(OrderStatus.Reserved, (await OrderOfTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Task_that_fails_again_after_a_manual_retry_refreshes_its_alert_with_the_task_order_and_latest_error()
    {
        var customerId = await SeedCustomerAsync();
        var product = await CreateProductAsync(threshold: 0);
        await ImportAsync((product.ProductId, 10));
        var taskId = await SubmitAsync(customerId, (product.ProductId, 1));
        var key = AlertDedupKey.ProcessingFailed(taskId);
        var failures = new FailureInjection { OnlyTaskId = taskId, ReserveFailures = int.MaxValue }; // transient timeouts, forever
        var settings = new Dictionary<string, string?> { ["OrderWorker:MaxAttempts"] = "2" };

        await RunOrderWorkersAsync(
            () => WaitForTasksAsync([taskId], ProcessingTaskStatus.Failed), settings, services => services.InjectFailures(failures));
        var alert = Assert.Single(await AlertsAsync(key));
        Assert.Equal((AlertStatus.Open, 1), (alert.Status, alert.OccurrenceCount));

        await fixture.WithServicesAsync(services => services.GetRequiredService<IProcessingTaskService>().RetryAsync(taskId, CancellationToken.None));
        await RunOrderWorkersAsync(
            () => Eventually.WaitAsync(
                () => fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId)),
                t => t.Status == ProcessingTaskStatus.Failed && t.Attempts.Count == 4,
                Timeout,
                "task failed again"),
            settings,
            services => services.InjectFailures(failures));

        var refreshed = Assert.Single(await AlertsAsync(key));
        Assert.Equal((alert.AlertId, AlertStatus.Open, 2, (Guid?)taskId), (refreshed.AlertId, refreshed.Status, refreshed.OccurrenceCount, refreshed.TaskId));
        Assert.True(refreshed.LastOccurredAt > alert.LastOccurredAt);
    }

    // ---- cost of the alert writes in the reserve transaction -------------------------------------------------------

    [Fact]
    public async Task Reservation_runs_three_alert_statements_in_its_transaction_and_one_more_notification_only_when_an_alert_changes()
    {
        var customerId = await SeedCustomerAsync();
        var product = await CreateProductAsync(threshold: 5);
        await ImportAsync((product.ProductId, 20));
        var commands = new CommandLog();
        using var host = fixture.CreateHost(
            "alert-statements",
            configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(commands)));

        // The reserve transaction of a one-line order, as the Order Worker runs it (attempt started before, as its own transaction).
        async Task<List<string>> ReserveAsync(int quantity)
        {
            var taskId = await SubmitAsync(customerId, (product.ProductId, quantity));
            var message = await fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking()
                .SingleAsync(e => e.EventType == EventTypes.OrderCreated && e.AggregateId == taskId.ToString()));
            await using var scope = host.Services.CreateAsyncScope();
            var reservations = scope.ServiceProvider.GetRequiredService<IOrderReservationService>();
            var attempt = await reservations.StartAttemptAsync(
                taskId, message.OutboxEventId.ToString(), OrderMessageHandler.WorkerName, "alert-statements", CancellationToken.None);

            commands.Clear();
            var result = await reservations.ReserveAsync(
                MessageJson.Deserialize<OrderCreatedPayload>(message.Payload), attempt!.AttemptId, OrderMessageHandler.WorkerName, CancellationToken.None);
            Assert.Equal(ReservationOutcome.Reserved, result.Outcome);
            return commands.Texts;
        }

        static int Count(List<string> texts, string fragment) => texts.Count(t => t.Contains(fragment, StringComparison.Ordinal));

        // 20 - 3 = 17: not low before, not low after. The alert writer runs its statements and changes nothing.
        var unchanged = await ReserveAsync(3);
        Assert.Equal(3, Count(unchanged, "\"Alerts\""));
        Assert.Equal(1, Count(unchanged, "pg_notify")); // order, stock and task of the unit of work (task 015)
        Assert.Equal(8, unchanged.Count);

        // 17 - 12 = 5: the product becomes low. The same statements, plus the notification of the new alert.
        var opened = await ReserveAsync(12);
        Assert.Equal(3, Count(opened, "\"Alerts\""));
        Assert.Equal(2, Count(opened, "pg_notify"));
        Assert.Equal(9, opened.Count);
        Assert.Equal(AlertStatus.Open, (await AlertsAsync(AlertDedupKey.LowStock(product.ProductId)))[^1].Status);

        // The statements: the low-stock reconcile (open where low, resolve where not low) and the resolve of the task's own
        // failure alert; the reserve UPDATE itself does not mention alerts.
        Assert.Single(unchanged, t => t.Contains("INSERT INTO \"Alerts\"", StringComparison.Ordinal));
        Assert.Equal(2, unchanged.Count(t => t.Contains("UPDATE \"Alerts\"", StringComparison.Ordinal)));
        var reserve = Assert.Single(unchanged, t => t.Contains("UPDATE \"Inventories\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Alerts", reserve);
    }

    [Fact]
    public async Task Alert_list_is_a_count_plus_one_page_statement_whatever_the_alerts_refer_to_and_the_detail_is_one_statement()
    {
        var product = await CreateProductAsync(threshold: 5);
        var websiteId = await ChannelIdAsync("website");
        var mapping = await MapAsync(websiteId, product.ProductId);
        await fixture.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<OmnichannelDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await services.GetRequiredService<AlertStore>().OpenOrRefreshAsync(
                [AlertOccurrence.StockSyncFailed(websiteId, product.ProductId, mapping.SyncTaskId!.Value, "channel_rejected")], CancellationToken.None);
            await transaction.CommitAsync();
            return 0;
        });
        var commands = new CommandLog();
        using var host = fixture.CreateHost(
            "alert-query-shape",
            configureServices: services => services.ConfigureDbContext<OmnichannelDbContext>(o => o.AddInterceptors(commands)));
        await using var scope = host.Services.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertService>();

        // Product, channel, the current stock of the low-stock alert and the acknowledging user: joins of the page statement.
        var page = await alerts.ListAsync(new AlertListQuery { ProductId = product.ProductId }, CancellationToken.None);
        Assert.Equal(2, commands.Texts.Count);
        Assert.Equal([AlertType.StockSyncFailed, AlertType.LowStock], page.Items.Select(a => a.Type));
        Assert.Equal(("Website", (int?)0), (page.Items[0].ChannelName, page.Items[1].AvailableStock));

        commands.Clear();
        Assert.Equal(page.Items[1], await alerts.GetAsync(page.Items[1].AlertId, CancellationToken.None));
        Assert.Single(commands.Texts);
    }

    // ---- transactions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alert_writer_refuses_to_write_outside_an_explicit_transaction()
    {
        var product = await SeedLowProductAsync();

        await fixture.WithServicesAsync<object?>(async services =>
        {
            var alerts = services.GetRequiredService<AlertStore>();
            Guid[] ids = [product.ProductId];
            string[] keys = [AlertDedupKey.LowStock(product.ProductId)];

            await Assert.ThrowsAsync<InvalidOperationException>(() => alerts.ReconcileLowStockAsync(ids, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => alerts.OpenOrRefreshAsync(
                [AlertOccurrence.InsufficientStock(product.ProductId, Guid.NewGuid())], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => alerts.ResolveAsync(keys, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => alerts.AcknowledgeAsync(Guid.NewGuid(), "someone", CancellationToken.None));
            return null;
        });

        Assert.Empty(await AlertsAsync(AlertDedupKey.LowStock(product.ProductId)));
    }

    [Fact]
    public async Task Rolled_back_transaction_leaves_no_alert_and_its_hint_is_never_delivered()
    {
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);
        var product = await SeedLowProductAsync();
        var key = AlertDedupKey.LowStock(product.ProductId);

        async Task<IReadOnlyList<AlertChange>> ReconcileAsync(bool commit) =>
            await fixture.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<OmnichannelDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var changes = await services.GetRequiredService<AlertStore>().ReconcileLowStockAsync([product.ProductId], CancellationToken.None);
                // Inside the transaction the alert exists.
                Assert.Equal(1, await db.Alerts.CountAsync(a => a.DedupKey == key));
                if (commit)
                {
                    await transaction.CommitAsync();
                }
                else
                {
                    await transaction.RollbackAsync();
                }

                return changes;
            });

        var rolledBack = Assert.Single(await ReconcileAsync(commit: false));
        Assert.Empty(await AlertsAsync(key));

        // The same write, committed: now the alert exists and its hints arrive - those of the rolled-back write never did.
        var committed = Assert.Single(await ReconcileAsync(commit: true));
        Assert.NotEqual(rolledBack.AlertId, committed.AlertId);
        Assert.Equal(committed.AlertId, Assert.Single(await AlertsAsync(key)).AlertId);
        await probe.WaitForAsync(_ => AlertHintsOf(probe, product.ProductId).Count >= 2, "hints of the committed alert");
        Assert.All(AlertHintsOf(probe, product.ProductId), hint => Assert.Equal(committed.AlertId, hint.AlertId));
        Assert.Equal(2, AlertHintsOf(probe, product.ProductId).Count);
    }

    [Fact]
    public async Task Rolled_back_import_changes_no_alert_and_sends_no_alert_hint()
    {
        await using var probe = await HintProbe.StartAsync(fixture.DatabaseConnectionString);
        var product = await CreateProductAsync(threshold: 5);
        var key = AlertDedupKey.LowStock(product.ProductId);
        var open = Assert.Single(await AlertsAsync(key));

        // The first line would lift the product above its threshold; the second line (an unknown product, with the highest
        // id so that it is applied last) fails the whole import.
        var unknownProduct = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => ImportAsync((product.ProductId, 10), (unknownProduct, 1)));

        Assert.Equal((0, 5), await StockAndThresholdAsync(product.ProductId));
        var unchanged = Assert.Single(await AlertsAsync(key));
        Assert.Equal((open.AlertId, AlertStatus.Open), (unchanged.AlertId, unchanged.Status));

        // A later import that is applied resolves it; the hints seen for the product are those of the creation and of this
        // import only.
        await ImportAsync((product.ProductId, 10));
        await probe.WaitForAsync(_ => AlertHintsOf(probe, product.ProductId).Count >= 3, "hints of the product's alert");
        Assert.Equal(
            [AlertStatus.Open.ToString(), null, AlertStatus.Resolved.ToString()],
            AlertHintsOf(probe, product.ProductId).Select(h => h.Status));
    }

    // ---- backfill of the migration --------------------------------------------------------------------------------

    [Fact]
    public async Task Backfill_opens_alerts_for_low_products_for_pairs_whose_last_finished_sync_failed_and_for_failed_tasks_of_other_types()
    {
        var t0 = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var (websiteId, posId) = (await ChannelIdAsync("website"), await ChannelIdAsync("pos"));

        Product NewProduct(int threshold) => new($"BF-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), "Backfill product", 1m, threshold, null);
        var lowAtZero = NewProduct(0);      // 0 <= 0
        var lowAtThreshold = NewProduct(5); // 5 <= 5
        var notLow = NewProduct(5);         // 6 > 5
        var alreadyAlerted = NewProduct(3); // low, and has an unresolved alert already

        ProcessingTask Sync(Guid channelId, Product product, bool fail, string errorCode = "channel_rejected")
        {
            var task = ProcessingTask.ForStockSync(channelId, product.ProductId, "backfill", null);
            var attempt = task.StartAttempt("StockSyncWorker", "bf-1", t0);
            if (fail)
            {
                task.Fail(attempt.AttemptId, errorCode, "failed (test)", t0);
            }
            else
            {
                task.Succeed(null, attempt.AttemptId, t0);
            }

            return task;
        }

        // website + notLow: succeeded first, failed later -> alert. pos + notLow: failed first, succeeded later -> none.
        var succeededThenFailed = (Old: Sync(websiteId, notLow, fail: false), New: Sync(websiteId, notLow, fail: true, "channel_unavailable"));
        var failedThenSucceeded = (Old: Sync(posId, notLow, fail: true), New: Sync(posId, notLow, fail: false));
        // website + lowAtZero: failed, and a newer task that is still waiting decides nothing -> alert for the failed one.
        var failedThenWaiting = (Old: Sync(websiteId, lowAtZero, fail: true), New: ProcessingTask.ForStockSync(websiteId, lowAtZero.ProductId, "backfill", null));

        // An order task that failed at its second attempt, and one that succeeded.
        var failedOrder = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "backfill", null);
        failedOrder.ScheduleRetry(failedOrder.StartAttempt("OrderWorker", "bf-1", t0).AttemptId, "timeout", "slow (test)", t0);
        failedOrder.Fail(failedOrder.StartAttempt("OrderWorker", "bf-1", t0).AttemptId, "check_violation", "bad row (test)", t0);
        var succeededOrder = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "backfill", null);
        succeededOrder.Succeed(null, succeededOrder.StartAttempt("OrderWorker", "bf-1", t0).AttemptId, t0);

        await fixture.WithServicesAsync<object?>(async services =>
        {
            var db = services.GetRequiredService<OmnichannelDbContext>();
            // Everything here is rolled back at the end: the backfill sees the whole shared test database.
            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Products.AddRange(lowAtZero, lowAtThreshold, notLow, alreadyAlerted);
            db.Inventories.AddRange(
                new Inventory(lowAtZero.ProductId, 0), new Inventory(lowAtThreshold.ProductId, 5),
                new Inventory(notLow.ProductId, 6), new Inventory(alreadyAlerted.ProductId, 1));
            db.ProcessingTasks.AddRange(
                succeededThenFailed.Old, succeededThenFailed.New, failedThenSucceeded.Old, failedThenSucceeded.New,
                failedThenWaiting.Old, failedThenWaiting.New, failedOrder, succeededOrder);
            await db.SaveChangesAsync();

            // When each task reached its status (UpdatedAt); the auditing interceptor wrote "now" for all of them.
            async Task FinishedAtAsync(ProcessingTask task, DateTimeOffset at) =>
                await db.ProcessingTasks.Where(t => t.TaskId == task.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, at));
            await FinishedAtAsync(succeededThenFailed.Old, t0.AddHours(1));
            await FinishedAtAsync(succeededThenFailed.New, t0.AddHours(2));
            await FinishedAtAsync(failedThenSucceeded.Old, t0.AddHours(1));
            await FinishedAtAsync(failedThenSucceeded.New, t0.AddHours(2));
            await FinishedAtAsync(failedThenWaiting.Old, t0.AddHours(3));
            await FinishedAtAsync(failedOrder, t0.AddHours(4));

            var existing = Assert.Single(await services.GetRequiredService<AlertStore>().ReconcileLowStockAsync([alreadyAlerted.ProductId], CancellationToken.None));

            await db.Database.ExecuteSqlRawAsync(AlertBackfill.Sql);
            // Applying it twice changes nothing.
            await db.Database.ExecuteSqlRawAsync(AlertBackfill.Sql);

            async Task<List<Alert>> ByKeyAsync(string key) => await db.Alerts.AsNoTracking().Where(a => a.DedupKey == key).ToListAsync();

            // Low stock: the shared rule, inclusive; a product that has its alert is left alone.
            foreach (var product in new[] { lowAtZero, lowAtThreshold })
            {
                var alert = Assert.Single(await ByKeyAsync(AlertDedupKey.LowStock(product.ProductId)));
                Assert.Equal(
                    (AlertType.LowStock, AlertStatus.Open, (Guid?)product.ProductId, 1, (Guid?)null, (Guid?)null),
                    (alert.Type, alert.Status, alert.ProductId, alert.OccurrenceCount, alert.TaskId, alert.ChannelId));
            }

            Assert.Empty(await ByKeyAsync(AlertDedupKey.LowStock(notLow.ProductId)));
            Assert.Equal(existing.AlertId, Assert.Single(await ByKeyAsync(AlertDedupKey.LowStock(alreadyAlerted.ProductId))).AlertId);

            // Stock sync: the most recently finished task of the pair decides.
            var failedPair = Assert.Single(await ByKeyAsync(AlertDedupKey.StockSyncFailed(websiteId, notLow.ProductId)));
            Assert.Equal(
                (AlertType.StockSyncFailed, AlertStatus.Open, (Guid?)websiteId, (Guid?)notLow.ProductId, (Guid?)succeededThenFailed.New.TaskId, "channel_unavailable", 1),
                (failedPair.Type, failedPair.Status, failedPair.ChannelId, failedPair.ProductId, failedPair.TaskId, failedPair.ErrorCode, failedPair.OccurrenceCount));
            Assert.Equal((t0.AddHours(2), t0.AddHours(2)), (failedPair.FirstOccurredAt, failedPair.LastOccurredAt));
            Assert.Null(failedPair.OrderId);

            Assert.Empty(await ByKeyAsync(AlertDedupKey.StockSyncFailed(posId, notLow.ProductId)));

            var waitingPair = Assert.Single(await ByKeyAsync(AlertDedupKey.StockSyncFailed(websiteId, lowAtZero.ProductId)));
            Assert.Equal(((Guid?)failedThenWaiting.Old.TaskId, "channel_rejected", t0.AddHours(3)), (waitingPair.TaskId, waitingPair.ErrorCode, waitingPair.LastOccurredAt));

            // Stock-sync tasks never get a per-task alert.
            Assert.Empty(await ByKeyAsync(AlertDedupKey.ProcessingFailed(succeededThenFailed.New.TaskId)));

            // Other task types: one alert per failed task, with the error of its last attempt.
            var failedTask = Assert.Single(await ByKeyAsync(AlertDedupKey.ProcessingFailed(failedOrder.TaskId)));
            Assert.Equal(
                (AlertType.ProcessingFailed, AlertStatus.Open, (Guid?)failedOrder.TaskId, "check_violation", 1, t0.AddHours(4)),
                (failedTask.Type, failedTask.Status, failedTask.TaskId, failedTask.ErrorCode, failedTask.OccurrenceCount, failedTask.FirstOccurredAt));
            Assert.Equal(((Guid?)null, (Guid?)null, (Guid?)null), (failedTask.ProductId, failedTask.ChannelId, failedTask.OrderId));
            Assert.Empty(await ByKeyAsync(AlertDedupKey.ProcessingFailed(succeededOrder.TaskId)));

            // No insufficient-stock backfill.
            Assert.False(await db.Alerts.AnyAsync(a => a.Type == AlertType.InsufficientStock
                && (a.ProductId == lowAtZero.ProductId || a.ProductId == notLow.ProductId)));

            await transaction.RollbackAsync();
            return null;
        });

        Assert.Empty(await AlertsAsync(AlertDedupKey.LowStock(lowAtZero.ProductId)));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>The alerts of a key, oldest first (resolved ones included).</summary>
    private Task<List<Alert>> AlertsAsync(string dedupKey) =>
        fixture.WithDbAsync(db => db.Alerts.AsNoTracking()
            .Where(a => a.DedupKey == dedupKey)
            .OrderBy(a => a.FirstOccurredAt)
            .ThenBy(a => a.AlertId)
            .ToListAsync());

    /// <summary>Alert hints about a product in the order they were delivered.</summary>
    private static List<RealtimeHint> AlertHintsOf(HintProbe probe, Guid productId, AlertType? type = null) =>
        probe.Events
            .Where(e => e.AlertId is not null && e.ProductId == productId)
            .Where(e => type is null || e.AlertType == type.ToString())
            .ToList();

    private Task<ProductResponse> CreateProductAsync(int threshold) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IProductService>().CreateAsync(
            new CreateProductRequest($"AL-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), "Alert product", 2m, threshold, null),
            CancellationToken.None));

    private Task<ProductResponse> UpdateThresholdAsync(ProductResponse product, int threshold) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IProductService>().UpdateAsync(
            product.ProductId, new UpdateProductRequest(product.Name, product.Price, threshold, product.ExpiryDate), CancellationToken.None));

    /// <summary>A product without stock and without an alert: written directly, as the other worker tests seed theirs.</summary>
    private async Task<Product> SeedLowProductAsync()
    {
        var product = new Product($"ALS-{Guid.NewGuid():N}"[..20].ToUpperInvariant(), "Alert product (seeded)", 2m, 0, null);
        await fixture.WithDbAsync(async db =>
        {
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 0));
            return await db.SaveChangesAsync();
        });
        return product;
    }

    private async Task<Guid> SeedCustomerAsync()
    {
        var customer = new Customer($"Alert customer {Guid.NewGuid():N}", null, null, null);
        await fixture.WithDbAsync(async db =>
        {
            db.Customers.Add(customer);
            return await db.SaveChangesAsync();
        });
        return customer.CustomerId;
    }

    private Task<ImportInventoryResponse> ImportAsync(params (Guid ProductId, int Stock)[] items) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IInventoryImportService>().ImportAsync(
            new ImportInventoryRequest($"GRN-{Guid.NewGuid():N}", null, items.Select(i => new ImportInventoryItem(i.ProductId, i.Stock)).ToList()),
            CancellationToken.None));

    private async Task<Guid> SubmitAsync(Guid customerId, params (Guid ProductId, int Stock)[] items)
    {
        var response = await fixture.WithServicesAsync(services => services.GetRequiredService<IOrderIntakeService>().SubmitAsync(
            "website",
            new SubmitOrderRequest($"AL-EXT-{Guid.NewGuid():N}", customerId, items.Select(i => new SubmitOrderItem(i.ProductId, i.Stock)).ToList()),
            CancellationToken.None));
        return response.TaskId;
    }

    private Task<Guid> ChannelIdAsync(string slug) =>
        fixture.WithDbAsync(db => db.Channels.AsNoTracking().Where(c => c.Slug == slug).Select(c => c.ChannelId).SingleAsync());

    private Task<ChannelProductSavedResponse> MapAsync(Guid channelId, Guid productId) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            channelId, new CreateChannelProductRequest(productId, null, $"EXT-{Guid.NewGuid():N}"[..12]), CancellationToken.None));

    private Task<ChannelProductSavedResponse> MapAsync(Guid channelId, Guid productId, string? externalProductId, string? externalSku) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().CreateProductAsync(
            channelId, new CreateChannelProductRequest(productId, externalProductId, externalSku), CancellationToken.None));

    private Task<ChannelProductSavedResponse> UpdateMappingAsync(
        ChannelProductSavedResponse mapping, string? externalSku, ChannelProductStatus status, string? externalProductId = null) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>().UpdateProductAsync(
            mapping.ChannelId, mapping.ChannelProductId, new UpdateChannelProductRequest(externalProductId, externalSku, status), CancellationToken.None));

    /// <summary>
    /// A product mapped on the website channel with an external SKU the simulated channel rejects for good: its initial sync
    /// has failed and the pair's stock-sync alert is open.
    /// </summary>
    private async Task<(ProductResponse Product, ChannelProductSavedResponse Mapping, string AlertKey)> FailedPairAsync()
    {
        var product = await CreateProductAsync(threshold: 0);
        var websiteId = await ChannelIdAsync("website");
        var mapping = await MapAsync(
            websiteId, product.ProductId, externalProductId: null, externalSku: $"{ChannelSimulationOptions.PermanentErrorSkuPrefix}{Guid.NewGuid():N}");
        await RunSimulatedStockSyncAsync(() => WaitForSyncTasksAsync(product.ProductId, tasks => tasks.Count == 1 && tasks.All(t => t.IsTerminal)));

        var key = AlertDedupKey.StockSyncFailed(websiteId, product.ProductId);
        var alert = Assert.Single(await AlertsAsync(key));
        Assert.Equal((AlertStatus.Open, mapping.SyncTaskId, ChannelApiErrorCodes.Rejected), (alert.Status, alert.TaskId, alert.ErrorCode));
        return (product, mapping, key);
    }

    private Task<Order> OrderOfTaskAsync(Guid taskId) =>
        fixture.WithDbAsync(async db =>
        {
            var orderId = await db.ProcessingTasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.OrderId).SingleAsync();
            return await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);
        });

    private Task<(int Available, int Threshold)> StockAndThresholdAsync(Guid productId) =>
        fixture.WithDbAsync(async db =>
        {
            var row = await db.Inventories.AsNoTracking()
                .Where(i => i.ProductId == productId)
                .Select(i => new { i.AvailableStock, i.Product.LowStockThreshold })
                .SingleAsync();
            return (row.AvailableStock, row.LowStockThreshold);
        });

    private Task<List<ProcessingTask>> SyncTasksAsync(Guid productId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking()
            .Where(t => t.TaskType == ProcessingTaskType.StockSync && t.ProductId == productId)
            .ToListAsync());

    private Task WaitForSyncTasksAsync(Guid productId, Func<List<ProcessingTask>, bool> done) =>
        Eventually.WaitAsync(() => SyncTasksAsync(productId), done, Timeout, "stock-sync tasks of the product finished");

    private Task WaitForTasksAsync(IReadOnlyCollection<Guid> taskIds, ProcessingTaskStatus status) =>
        Eventually.WaitAsync(
            () => fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().CountAsync(t => taskIds.Contains(t.TaskId) && t.Status == status)),
            count => count == taskIds.Count,
            Timeout,
            $"{taskIds.Count} tasks {status}");

    /// <summary>Order Worker (and Outbox Publisher) only: stock sync is not part of these tests.</summary>
    private async Task RunOrderWorkersAsync(
        Func<Task> until,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        int instances = 1,
        ushort concurrency = 4)
    {
        var hosts = Enumerable.Range(0, instances)
            .Select(n => fixture.CreateHost(
                $"alert-orders-{n}-{Guid.NewGuid():N}"[..24],
                new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>()) { ["OrderWorker:Concurrency"] = concurrency.ToString() },
                services =>
                {
                    services.RemoveHostedService<StockSyncWorkerService>();
                    configureServices?.Invoke(services);
                }))
            .ToList();
        await Task.WhenAll(hosts.Select(h => h.StartAsync()));
        try
        {
            await until();
        }
        finally
        {
            await Task.WhenAll(hosts.Select(h => h.StopAsync()));
            hosts.ForEach(h => h.Dispose());
        }
    }

    private Task RunOrderWorkersUntilSucceededAsync(IReadOnlyCollection<Guid> taskIds, int instances = 1, ushort concurrency = 4) =>
        RunOrderWorkersAsync(() => WaitForTasksAsync(taskIds, ProcessingTaskStatus.Succeeded), instances: instances, concurrency: concurrency);

    /// <summary>Stock Sync Worker (and Outbox Publisher) with a scripted channel.</summary>
    private async Task RunStockSyncWorkersAsync(RecordingChannelClient channel, Func<Task> until)
    {
        var host = fixture.CreateHost(
            $"alert-sync-{Guid.NewGuid():N}"[..24],
            // The breaker has its own tests; here it must not interfere with scripted failures.
            new Dictionary<string, string?> { ["StockSyncWorker:CircuitBreaker:MinimumThroughput"] = "1000" },
            services =>
            {
                services.RemoveHostedService<OrderWorkerService>();
                services.UseChannelAdapter(channel);
            });
        await host.StartAsync();
        try
        {
            await until();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    /// <summary>Stock Sync Worker (and Outbox Publisher) with the simulated channel and its SKU rules, as in Development.</summary>
    private async Task RunSimulatedStockSyncAsync(Func<Task> until)
    {
        var host = fixture.CreateHost(
            $"alert-sim-{Guid.NewGuid():N}"[..24],
            new Dictionary<string, string?> { ["ChannelSimulation:SkuRules"] = "true" },
            services => services.RemoveHostedService<OrderWorkerService>());
        await host.StartAsync();
        try
        {
            await until();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    /// <summary>The text of every SQL command a host's DbContexts send.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public List<string> Texts => [.. _texts];

        public void Clear() => _texts.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

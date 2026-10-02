using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Dashboard;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 015 Part C: <c>GET /api/dashboard/summary</c>. Range numbers are checked with rows moved to a window of their own
/// (nothing else in the shared test database lies there); current numbers are compared with independent counts.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DashboardSummaryTests(ApiFactory factory)
{
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Fact]
    public async Task Orders_and_finished_tasks_are_counted_in_the_range_only()
    {
        // A window nobody else writes to: one hour of a year of its own per test run.
        var from = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(Random.Shared.Next(1, 200_000));
        var to = from.AddHours(1);
        var (website, pos, marketplace) = (await ChannelAsync("website"), await ChannelAsync("pos"), await ChannelAsync("digital-platform"));
        var customer = new Customer($"Dashboard customer {TestData.UniqueSuffix()}", null, null, null);
        var product = new Product($"DASH-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Dashboard product", 10m, 0, null);

        Order NewOrder(Channel channel, OrderStatus status, int quantity)
        {
            var order = new Order(channel.ChannelId, customer.CustomerId, $"DASH-{Guid.NewGuid():N}", status);
            order.AddItem(product.ProductId, quantity, 10m);
            return order;
        }

        var inRange = new[]
        {
            NewOrder(website, OrderStatus.Reserved, 2),          // 20
            NewOrder(website, OrderStatus.Reserved, 3),          // 30
            NewOrder(website, OrderStatus.InsufficientStock, 9), // not sold
            NewOrder(pos, OrderStatus.Completed, 5),             // 50
            NewOrder(pos, OrderStatus.Cancelled, 7)              // not sold
        };
        var before = NewOrder(website, OrderStatus.Reserved, 1);
        var after = NewOrder(pos, OrderStatus.Reserved, 1);

        // Tasks that reached their status in the range, and some that did not.
        var succeeded = Finished(ProcessingTask.ForStockSync(website.ChannelId, product.ProductId, "dash", null), fail: false);
        var failedSync = Finished(ProcessingTask.ForStockSync(pos.ChannelId, product.ProductId, "dash", null), fail: true);
        var failedOrder = Finished(new ProcessingTask(ProcessingTaskType.OrderProcessing, null, "dash", null), fail: true);
        var failedOutside = Finished(ProcessingTask.ForStockSync(pos.ChannelId, product.ProductId, "dash", null), fail: true);

        await factory.QueryDbAsync(async db =>
        {
            db.Customers.Add(customer);
            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 0));
            db.Orders.AddRange([.. inRange, before, after]);
            db.ProcessingTasks.AddRange(succeeded, failedSync, failedOrder, failedOutside);
            await db.SaveChangesAsync();

            var middle = from.AddMinutes(30);
            var ids = inRange.Select(o => o.OrderId).ToList();
            await db.Orders.Where(o => ids.Contains(o.OrderId)).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, middle));
            // The ends of the range are part of it; a second before and after are not.
            await db.Orders.Where(o => o.OrderId == inRange[0].OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, from));
            await db.Orders.Where(o => o.OrderId == inRange[3].OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, to));
            await db.Orders.Where(o => o.OrderId == before.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, from.AddSeconds(-1)));
            await db.Orders.Where(o => o.OrderId == after.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, to.AddSeconds(1)));

            var finished = new[] { succeeded.TaskId, failedSync.TaskId, failedOrder.TaskId };
            await db.ProcessingTasks.Where(t => finished.Contains(t.TaskId)).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, middle));
            await db.ProcessingTasks.Where(t => t.TaskId == failedOutside.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, to.AddSeconds(1)));
            return 0;
        });

        // An offset other than UTC in the request: the same instants.
        var summary = await GetAsync($"from={Iso(from.ToOffset(TimeSpan.FromHours(7)))}&to={Iso(to.ToOffset(TimeSpan.FromHours(7)))}");

        Assert.Equal((from, to), (summary.From, summary.To));
        Assert.True((DateTimeOffset.UtcNow - summary.GeneratedAt).Duration() < TimeSpan.FromMinutes(1));

        Assert.Equal(5, summary.Orders.Total);
        Assert.Equal(100m, summary.Orders.SalesAmount);
        Assert.Equal(
            [(OrderStatus.Pending, 0), (OrderStatus.Reserved, 2), (OrderStatus.InsufficientStock, 1), (OrderStatus.Cancelled, 1), (OrderStatus.Completed, 1)],
            summary.Orders.ByStatus.Select(s => (s.Status, s.Count)));
        var web = summary.Orders.ByChannel.Single(c => c.ChannelId == website.ChannelId);
        Assert.Equal(("website", website.Name, 3, 2, 1), (web.Slug, web.Name, web.Total, web.Reserved, web.InsufficientStock));
        var shop = summary.Orders.ByChannel.Single(c => c.ChannelId == pos.ChannelId);
        Assert.Equal((2, 0, 0), (shop.Total, shop.Reserved, shop.InsufficientStock));
        var market = summary.Orders.ByChannel.Single(c => c.ChannelId == marketplace.ChannelId);
        Assert.Equal((0, 0, 0), (market.Total, market.Reserved, market.InsufficientStock));
        // Every channel is listed, also those created by other tests without any order here.
        Assert.Equal(await factory.QueryDbAsync(db => db.Channels.CountAsync()), summary.Orders.ByChannel.Count);
        Assert.Equal(5, summary.Orders.ByChannel.Sum(c => c.Total));

        // Finished in the range: by task type.
        Assert.Equal(Enum.GetValues<ProcessingTaskType>(), summary.Processing.Select(p => p.TaskType));
        var stockSync = summary.Processing.Single(p => p.TaskType == ProcessingTaskType.StockSync);
        Assert.Equal((1, 1), (stockSync.Succeeded, stockSync.Failed));
        var orderTasks = summary.Processing.Single(p => p.TaskType == ProcessingTaskType.OrderProcessing);
        Assert.Equal((0, 1), (orderTasks.Succeeded, orderTasks.Failed));
        Assert.Equal((0, 0), (summary.Processing.Single(p => p.TaskType == ProcessingTaskType.Email).Succeeded,
            summary.Processing.Single(p => p.TaskType == ProcessingTaskType.Email).Failed));

        // Stock sync per channel that has a queue: failures in the range; the last success is not bound to the range.
        Assert.Equal(["digital-platform", "pos", "website"], summary.StockSync.Select(s => s.Slug).Order());
        Assert.Equal(1, summary.StockSync.Single(s => s.Slug == "pos").Failed);
        Assert.Equal(0, summary.StockSync.Single(s => s.Slug == "website").Failed);
        var lastOnWebsite = await factory.QueryDbAsync(db => db.ProcessingTasks
            .Where(t => t.ChannelId == website.ChannelId && t.TaskType == ProcessingTaskType.StockSync && t.Status == ProcessingTaskStatus.Succeeded)
            .MaxAsync(t => t.UpdatedAt));
        Assert.Equal(lastOnWebsite, summary.StockSync.Single(s => s.Slug == "website").LastSucceededAt);
        Assert.True(lastOnWebsite >= from.AddMinutes(30));
    }

    [Fact]
    public async Task Empty_range_lists_every_status_task_type_and_channel_with_zeros()
    {
        var summary = await GetAsync("from=1990-01-01T00:00:00Z&to=1990-01-02T00:00:00Z");

        Assert.Equal((0, 0m), (summary.Orders.Total, summary.Orders.SalesAmount));
        Assert.Equal(Enum.GetValues<OrderStatus>(), summary.Orders.ByStatus.Select(s => s.Status));
        Assert.All(summary.Orders.ByStatus, s => Assert.Equal(0, s.Count));
        Assert.Contains(summary.Orders.ByChannel, c => c.Slug == "website");
        Assert.All(summary.Orders.ByChannel, c => Assert.Equal((0, 0, 0), (c.Total, c.Reserved, c.InsufficientStock)));
        Assert.Equal(Enum.GetValues<ProcessingTaskType>(), summary.Processing.Select(p => p.TaskType));
        Assert.All(summary.Processing, p => Assert.Equal((0, 0), (p.Failed, p.Succeeded)));
        Assert.Equal(3, summary.StockSync.Count);
        Assert.All(summary.StockSync, s => Assert.Equal(0, s.Failed));
    }

    [Fact]
    public async Task Current_numbers_ignore_the_range_and_match_independent_counts()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        // One product that is out of stock and below its threshold, one that has stock.
        var empty = await (await manager.PostAsJsonAsync(
            "/api/products", new CreateProductRequest($"DASH-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Dashboard empty", 1m, 0, null))).ReadAsync<ProductResponse>();
        var stocked = await (await manager.PostAsJsonAsync(
            "/api/products", new CreateProductRequest($"DASH-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Dashboard stocked", 1m, 2, null))).ReadAsync<ProductResponse>();
        await manager.PostAsJsonAsync(
            "/api/inventory/import", new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(stocked.ProductId, 50)]));
        var pos = await ChannelAsync("pos");
        var waiting = ProcessingTask.ForStockSync(pos.ChannelId, stocked.ProductId, "dash-current", null);
        waiting.ScheduleRetry(waiting.StartAttempt("StockSyncWorker", "i-1", DateTimeOffset.UtcNow).AttemptId, "channel_unavailable", "seeded", DateTimeOffset.UtcNow);
        var failedOutbox = new OutboxEvent("test.dashboard", "x", "{}", "dash-current", null);
        failedOutbox.RecordPublishFailure("unroutable (seeded)", DateTimeOffset.UtcNow, TimeSpan.Zero, maxAttempts: 1);
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.Add(waiting);
            db.OutboxEvents.Add(failedOutbox);
            return await db.SaveChangesAsync();
        });

        // A range in the past: the current numbers are the same as with the default range.
        var past = await GetAsync("from=1990-01-01T00:00:00Z&to=1990-01-02T00:00:00Z");
        var now = await GetAsync(string.Empty);

        Assert.Equal(now.Inventory, past.Inventory);
        Assert.Equal(now.Outbox, past.Outbox);
        Assert.Equal(
            now.Processing.Select(p => (p.TaskType, p.Pending, p.InProgress, p.RetryScheduled)),
            past.Processing.Select(p => (p.TaskType, p.Pending, p.InProgress, p.RetryScheduled)));

        // Inventory: the low-stock number is the low-stock filter of the inventory list (the shared rule).
        var lowStockList = await _staff.GetFromJsonAsync<PagedResult<InventoryItemResponse>>("/api/inventory?lowStockOnly=true&pageSize=1", TestData.Json);
        var allProducts = await _staff.GetFromJsonAsync<PagedResult<ProductResponse>>("/api/products?pageSize=1", TestData.Json);
        Assert.Equal(lowStockList!.TotalCount, now.Inventory.LowStock);
        Assert.Equal(allProducts!.TotalCount, now.Inventory.Products);
        Assert.Equal(await factory.QueryDbAsync(db => db.Inventories.CountAsync(i => i.TotalStock - i.ReservedStock == 0)), now.Inventory.OutOfStock);
        Assert.True(now.Inventory.OutOfStock >= 1 && now.Inventory.LowStock >= 1 && now.Inventory.Products > now.Inventory.OutOfStock);
        Assert.NotEqual(Guid.Empty, empty.ProductId);

        // Processing: tasks waiting or running now, per type.
        var open = await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking()
            .Where(t => t.Status != ProcessingTaskStatus.Succeeded && t.Status != ProcessingTaskStatus.Failed)
            .Select(t => new { t.TaskType, t.Status, t.ChannelId })
            .ToListAsync());
        Assert.All(now.Processing, p => Assert.Equal(
            (open.Count(t => t.TaskType == p.TaskType && t.Status == ProcessingTaskStatus.Pending),
                open.Count(t => t.TaskType == p.TaskType && t.Status == ProcessingTaskStatus.InProgress),
                open.Count(t => t.TaskType == p.TaskType && t.Status == ProcessingTaskStatus.RetryScheduled)),
            (p.Pending, p.InProgress, p.RetryScheduled)));
        var posSync = now.StockSync.Single(s => s.Slug == "pos");
        Assert.Equal(
            open.Count(t => t.TaskType == ProcessingTaskType.StockSync && t.ChannelId == pos.ChannelId && t.Status == ProcessingTaskStatus.RetryScheduled),
            posSync.RetryScheduled);
        Assert.True(posSync.RetryScheduled >= 1);

        // Outbox: waiting and given-up rows, and the age of the oldest waiting one.
        var outbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking()
            .Where(e => e.Status != OutboxEventStatus.Published)
            .Select(e => new { e.Status, e.CreatedAt })
            .ToListAsync());
        var pending = outbox.Where(e => e.Status == OutboxEventStatus.Pending).ToList();
        Assert.Equal(pending.Count, now.Outbox.Pending);
        Assert.Equal(outbox.Count(e => e.Status == OutboxEventStatus.Failed), now.Outbox.Failed);
        Assert.True(now.Outbox.Failed >= 1);
        Assert.Equal(pending.Count == 0 ? null : pending.Min(e => e.CreatedAt), now.Outbox.OldestPendingAt);
    }

    [Fact]
    public async Task Default_range_is_the_last_24_hours()
    {
        var summary = await GetAsync(string.Empty);

        Assert.Equal(TimeSpan.FromHours(24), summary.To - summary.From);
        Assert.Equal(summary.GeneratedAt, summary.To);

        // Only "to": 24 hours before it. Only "from": up to now.
        var onlyTo = await GetAsync("to=2026-09-01T12:00:00Z");
        Assert.Equal(
            (new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)),
            (onlyTo.From, onlyTo.To));
        var recent = DateTimeOffset.UtcNow.AddDays(-3);
        var onlyFrom = await GetAsync($"from={Iso(recent)}");
        Assert.Equal(onlyFrom.GeneratedAt, onlyFrom.To);
        Assert.True((onlyFrom.From - recent).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData("from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z", "to", "'To date' must be later than 'From date'.")]
    [InlineData("from=2026-09-01T00:00:00Z&to=2026-09-01T00:00:00Z", "to", "'To date' must be later than 'From date'.")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-04-03T00:00:01Z", "to", "The range from 'From date' to 'To date' must not be longer than 92 days.")]
    [InlineData("from=2999-01-01T00:00:00Z", "from", "'To date' must be later than 'From date'.")]
    [InlineData("from=2020-01-01T00:00:00Z", "from", "The range from 'From date' to 'To date' must not be longer than 92 days.")]
    public async Task Invalid_range_is_rejected_on_its_field(string query, string key, string message)
    {
        var response = await _staff.GetAsync($"/api/dashboard/summary?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await ErrorsAsync(response);
        Assert.Equal([key], errors.Keys);
        Assert.Equal([message], errors[key]);
    }

    [Fact]
    public async Task Range_of_exactly_92_days_is_accepted_and_messages_follow_the_request_language()
    {
        var accepted = await _staff.GetAsync("/api/dashboard/summary?from=2026-01-01T00:00:00Z&to=2026-04-03T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/summary?from=2026-01-01T00:00:00Z&to=2026-12-01T00:00:00Z");
        request.Headers.Add("Accept-Language", "vi");
        var rejected = await _staff.SendAsync(request);
        Assert.Equal(["Khoảng thời gian từ 'Từ ngày' đến 'Đến ngày' không được dài quá 92 ngày."], (await ErrorsAsync(rejected))["to"]);

        var unparseable = await _staff.GetAsync("/api/dashboard/summary?from=yesterday");
        Assert.Equal(HttpStatusCode.BadRequest, unparseable.StatusCode);
        Assert.Equal(["from"], (await ErrorsAsync(unparseable)).Keys);
    }

    [Fact]
    public async Task Every_role_may_read_the_summary_and_anonymous_callers_may_not()
    {
        foreach (var role in AppRoles.All)
        {
            Assert.Equal(HttpStatusCode.OK, (await factory.CreateClientAs(role).GetAsync("/api/dashboard/summary")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/dashboard/summary")).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static ProcessingTask Finished(ProcessingTask task, bool fail)
    {
        var attempt = task.StartAttempt("Worker", "i-1", DateTimeOffset.UtcNow);
        if (fail)
        {
            task.Fail(attempt.AttemptId, "seeded", "seeded", DateTimeOffset.UtcNow);
        }
        else
        {
            task.Succeed(null, attempt.AttemptId, DateTimeOffset.UtcNow);
        }

        return task;
    }

    private Task<Channel> ChannelAsync(string slug) =>
        factory.QueryDbAsync(db => db.Channels.AsNoTracking().SingleAsync(c => c.Slug == slug));

    private async Task<DashboardSummaryResponse> GetAsync(string query)
    {
        var response = await _staff.GetAsync($"/api/dashboard/summary?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<DashboardSummaryResponse>();
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
    }
}

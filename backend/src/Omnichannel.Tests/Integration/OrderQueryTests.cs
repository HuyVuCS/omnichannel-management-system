using System.Net;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>Task 008: <c>GET /api/orders</c> and <c>GET /api/orders/{id}</c>. Orders are seeded directly (as the worker stores them).</summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderQueryTests(ApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Fact]
    public async Task Order_list_filters_sorts_and_pages()
    {
        var s = TestData.UniqueSuffix();
        var channel = new Channel($"Query channel {s}", $"query-{s}", ChannelType.Website);
        var website = await factory.QueryDbAsync(db => db.Channels.AsNoTracking().SingleAsync(c => c.Slug == "website"));
        var x = new Customer($"Query customer {s}", null, null, null);
        var y = new Customer($"50% off {s}", null, null, null);
        var z = new Customer($"500 off {s}", null, null, null); // would match an unescaped "50%" pattern
        var product = new Product($"Q-{s}".ToUpperInvariant(), "Query product", 2.5m, 0, null);
        await factory.QueryDbAsync(db =>
        {
            db.Channels.Add(channel);
            db.Customers.AddRange(x, y, z);
            db.Products.Add(product);
            return db.SaveChangesAsync();
        });

        var o5 = await SeedOrderAsync(channel.ChannelId, z.CustomerId, $"EXT-{s}-5", OrderStatus.Reserved, T0.AddHours(-4), (product, 1));
        var o1 = await SeedOrderAsync(channel.ChannelId, x.CustomerId, $"EXT-{s}-1", OrderStatus.Reserved, T0.AddHours(-3), (product, 4));
        var o2 = await SeedOrderAsync(channel.ChannelId, x.CustomerId, $"EXT-{s}-2", OrderStatus.InsufficientStock, T0.AddHours(-2), (product, 2));
        var o3 = await SeedOrderAsync(channel.ChannelId, y.CustomerId, $"EXT-{s}-3", OrderStatus.Reserved, T0.AddHours(-1), (product, 3));
        var o4 = await SeedOrderAsync(website.ChannelId, x.CustomerId, $"EXT-{s}-4", OrderStatus.Reserved, T0.AddMinutes(-30), (product, 1));

        // Default: newest first.
        var byChannel = await ListAsync($"channel={channel.Slug.ToUpperInvariant()}");
        Assert.Equal([o3, o2, o1, o5], byChannel.Items.Select(o => o.OrderId));
        Assert.Equal(4, byChannel.TotalCount);
        var first = byChannel.Items[0];
        Assert.Equal(($"EXT-{s}-3", OrderStatus.Reserved, 7.5m, 1), (first.ExternalOrderId, first.Status, first.TotalAmount, first.ItemCount));
        Assert.Equal(new OrderChannelResponse(channel.ChannelId, channel.Slug, channel.Name), first.Channel);
        Assert.Equal(new OrderCustomerResponse(y.CustomerId, y.Name), first.Customer);
        Assert.Equal(T0.AddHours(-1), first.CreatedAt);

        Assert.Equal([o3, o1, o5], (await ListAsync($"channel={channel.Slug}&status=Reserved")).Items.Select(o => o.OrderId));
        Assert.Equal([o4, o2, o1], (await ListAsync($"customerId={x.CustomerId}")).Items.Select(o => o.OrderId));

        // Inclusive range on CreatedAt (any offset accepted).
        var from = Uri.EscapeDataString(T0.AddHours(-2).ToOffset(TimeSpan.FromHours(7)).ToString("O"));
        var to = Uri.EscapeDataString(T0.AddHours(-1).ToString("O"));
        Assert.Equal([o3, o2], (await ListAsync($"channel={channel.Slug}&from={from}&to={to}")).Items.Select(o => o.OrderId));

        // Search: customer name with a literal "%", and the external order id.
        Assert.Equal([o3], (await ListAsync($"channel={channel.Slug}&search=50%25")).Items.Select(o => o.OrderId));
        Assert.Equal([o2], (await ListAsync($"search=ext-{s}-2")).Items.Select(o => o.OrderId));

        // Paging and explicit sorting.
        var page2 = await ListAsync($"channel={channel.Slug}&pageSize=3&page=2");
        Assert.Equal([o5], page2.Items.Select(o => o.OrderId));
        Assert.Equal((4, 2, 3), (page2.TotalCount, page2.Page, page2.PageSize));
        Assert.Equal([o5, o1, o2, o3], (await ListAsync($"channel={channel.Slug}&sortDir=asc")).Items.Select(o => o.OrderId));
        Assert.Equal([o1, o3, o2, o5], (await ListAsync($"channel={channel.Slug}&sortBy=totalAmount&sortDir=desc")).Items.Select(o => o.OrderId));
        Assert.Equal([o1, o2, o3, o5], (await ListAsync($"channel={channel.Slug}&sortBy=externalOrderId")).Items.Select(o => o.OrderId));

        Assert.Empty((await ListAsync($"channel=no-such-channel-{s}")).Items);
    }

    [Theory]
    [InlineData("/api/orders?sortBy=unknown")]
    [InlineData("/api/orders?status=Shipped")]
    [InlineData("/api/orders?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z")]
    [InlineData("/api/orders?pageSize=101")]
    public async Task Invalid_order_list_parameters_return_validation_problem(string url)
    {
        var response = await _staff.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Order_detail_has_items_and_tasks_with_every_attempt()
    {
        var s = TestData.UniqueSuffix();
        var channel = await factory.QueryDbAsync(db => db.Channels.AsNoTracking().SingleAsync(c => c.Slug == "pos"));
        var customer = new Customer($"Detail customer {s}", null, null, null);
        var a = new Product($"DA-{s}".ToUpperInvariant(), "Detail product A", 12.34m, 0, null);
        var b = new Product($"DB-{s}".ToUpperInvariant(), "Detail product B", 5m, 0, null);
        await factory.QueryDbAsync(db =>
        {
            db.Customers.Add(customer);
            db.Products.AddRange(a, b);
            return db.SaveChangesAsync();
        });
        var orderId = await SeedOrderAsync(channel.ChannelId, customer.CustomerId, $"DET-{s}", OrderStatus.Reserved, T0, (b, 1), (a, 3));

        // The order needed a retry: attempt 1 timed out on one instance, attempt 2 succeeded on another.
        var task = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, $"detail-{s}", Guid.CreateVersion7().ToString());
        var first = task.StartAttempt("OrderWorker", "instance-a", T0);
        task.ScheduleRetry(first.AttemptId, "timeout", "Timeout: injected", T0.AddSeconds(1));
        var second = task.StartAttempt("OrderWorker", "instance-b", T0.AddSeconds(6));
        task.Succeed(orderId, second.AttemptId, T0.AddSeconds(7));
        await factory.QueryDbAsync(db =>
        {
            db.ProcessingTasks.Add(task);
            return db.SaveChangesAsync();
        });

        var response = await _staff.GetAsync($"/api/orders/{orderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.ReadAsync<OrderDetailResponse>();
        Assert.Equal(($"DET-{s}", OrderStatus.Reserved, 42.02m, 2), (detail.ExternalOrderId, detail.Status, detail.TotalAmount, detail.ItemCount));
        Assert.Equal((channel.Slug, customer.Name), (detail.Channel.Slug, detail.Customer.Name));
        Assert.Equal(
            [
                new OrderItemResponse(a.ProductId, a.Sku, a.Name, 3, 12.34m, 37.02m),
                new OrderItemResponse(b.ProductId, b.Sku, b.Name, 1, 5m, 5m)
            ],
            detail.Items);

        var shown = Assert.Single(detail.Tasks);
        Assert.Equal((task.TaskId, ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.Succeeded, 1, $"detail-{s}", task.MessageId),
            (shown.TaskId, shown.TaskType, shown.Status, shown.RetryCount, shown.CorrelationId, shown.MessageId));
        Assert.Equal(2, shown.Attempts.Count);
        Assert.Equal((1, ProcessingAttemptStatus.Failed, "OrderWorker", "instance-a", "timeout", "Timeout: injected"),
            (shown.Attempts[0].AttemptNo, shown.Attempts[0].Status, shown.Attempts[0].WorkerName, shown.Attempts[0].WorkerInstanceId,
                shown.Attempts[0].ErrorCode, shown.Attempts[0].ErrorMessage));
        Assert.Equal((T0, T0.AddSeconds(1)), (shown.Attempts[0].StartedAt, shown.Attempts[0].FinishedAt!.Value));
        Assert.Equal((2, ProcessingAttemptStatus.Succeeded, "instance-b", (string?)null),
            (shown.Attempts[1].AttemptNo, shown.Attempts[1].Status, shown.Attempts[1].WorkerInstanceId, shown.Attempts[1].ErrorCode));
    }

    [Fact]
    public async Task Unknown_order_returns_404_order_not_found()
    {
        var response = await _staff.GetAsync($"/api/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OrderErrorCodes.OrderNotFound, await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Anonymous_order_list_is_401()
    {
        var response = await factory.CreateClient().GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<PagedResult<OrderSummaryResponse>> ListAsync(string query)
    {
        var response = await _staff.GetAsync($"/api/orders?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<OrderSummaryResponse>>();
    }

    /// <summary>An order as the Order Worker stores it, with a chosen creation time.</summary>
    private async Task<Guid> SeedOrderAsync(
        Guid channelId,
        Guid customerId,
        string externalOrderId,
        OrderStatus status,
        DateTimeOffset createdAt,
        params (Product Product, int Stock)[] items)
    {
        var order = new Order(channelId, customerId, externalOrderId, status);
        foreach (var (product, stock) in items)
        {
            order.AddItem(product.ProductId, stock, product.Price);
        }

        await factory.QueryDbAsync(async db =>
        {
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Orders" SET "CreatedAt" = {createdAt} WHERE "OrderId" = {order.OrderId}""");
        });

        return order.OrderId;
    }
}

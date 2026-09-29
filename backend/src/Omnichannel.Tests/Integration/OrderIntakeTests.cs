using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderIntakeTests(ApiFactory factory)
{
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    [Fact]
    public async Task Staff_order_is_accepted_as_task_and_outbox_event_without_creating_an_order()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync(price: 12.34m);
        var request = NewOrder(customerId, (product.ProductId, 3));

        var response = await _staff.PostAsJsonAsync("/api/orders/website", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.ReadAsync<SubmitOrderResponse>();
        Assert.Equal((ProcessingTaskStatus.Pending, false), (body.Status, body.Duplicate));

        var (tasks, outbox, orders) = await RowsAsync(request.ExternalOrderId);
        var task = Assert.Single(tasks);
        var outboxEvent = Assert.Single(outbox);
        Assert.Empty(orders);

        Assert.Equal(body.TaskId, task.TaskId);
        Assert.Equal((ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.Pending), (task.TaskType, task.Status));
        Assert.Null(task.OrderId);
        Assert.Equal(outboxEvent.OutboxEventId.ToString(), task.MessageId);
        Assert.Equal((EventTypes.OrderCreated, OutboxEventStatus.Pending), (outboxEvent.EventType, outboxEvent.Status));
        Assert.Equal(task.TaskId.ToString(), outboxEvent.AggregateId);
        Assert.Null(outboxEvent.InventoryMovementId);

        var payload = MessageJson.Deserialize<OrderCreatedPayload>(outboxEvent.Payload);
        Assert.Equal((task.TaskId, "website", request.ExternalOrderId, customerId),
            (payload.TaskId, payload.ChannelSlug, payload.ExternalOrderId, payload.CustomerId));
        var item = Assert.Single(payload.Items);
        Assert.Equal((product.ProductId, 3, 12.34m), (item.ProductId, item.Stock, item.UnitPrice));
        Assert.False(string.IsNullOrEmpty(payload.ReceivedBy));
    }

    [Fact]
    public async Task Correlation_id_header_is_stored_on_task_and_outbox_and_returned()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var request = NewOrder(customerId, (product.ProductId, 1));
        var correlationId = $"order-{TestData.UniqueSuffix()}";

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders/pos") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Correlation-Id", correlationId);
        var response = await _staff.SendAsync(message);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(correlationId, (await response.ReadAsync<SubmitOrderResponse>()).CorrelationId);
        var (tasks, outbox, _) = await RowsAsync(request.ExternalOrderId);
        Assert.Equal(correlationId, Assert.Single(tasks).CorrelationId);
        Assert.Equal(correlationId, Assert.Single(outbox).CorrelationId);
    }

    [Fact]
    public async Task Resubmitting_the_same_order_returns_the_existing_task()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var request = NewOrder(customerId, (product.ProductId, 1));

        var first = await (await _staff.PostAsJsonAsync("/api/orders/website", request)).ReadAsync<SubmitOrderResponse>();
        var secondResponse = await _staff.PostAsJsonAsync("/api/orders/website", request with { ExternalOrderId = $"  {request.ExternalOrderId} " });

        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var second = await secondResponse.ReadAsync<SubmitOrderResponse>();
        Assert.Equal((first.TaskId, true, first.CorrelationId), (second.TaskId, second.Duplicate, second.CorrelationId));

        var (tasks, outbox, _) = await RowsAsync(request.ExternalOrderId);
        Assert.Single(tasks);
        Assert.Single(outbox);
    }

    [Fact]
    public async Task Concurrent_identical_submissions_create_one_task()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var request = NewOrder(customerId, (product.ProductId, 2));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _staff.PostAsJsonAsync("/api/orders/digital-platform", request)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.ReadAsync<SubmitOrderResponse>()));
        Assert.Single(bodies.Select(b => b.TaskId).Distinct());
        Assert.Equal(1, bodies.Count(b => !b.Duplicate));

        var (tasks, outbox, _) = await RowsAsync(request.ExternalOrderId);
        Assert.Single(tasks);
        Assert.Single(outbox);
    }

    [Fact]
    public async Task Same_external_order_id_on_two_channels_creates_two_tasks()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var request = NewOrder(customerId, (product.ProductId, 1));

        var website = await (await _staff.PostAsJsonAsync("/api/orders/website", request)).ReadAsync<SubmitOrderResponse>();
        var pos = await (await _staff.PostAsJsonAsync("/api/orders/pos", request)).ReadAsync<SubmitOrderResponse>();

        Assert.NotEqual(website.TaskId, pos.TaskId);
        Assert.False(pos.Duplicate);
        var (tasks, outbox, _) = await RowsAsync(request.ExternalOrderId);
        Assert.Equal(2, tasks.Count);
        Assert.Equal(2, outbox.Count);
    }

    [Fact]
    public async Task Unknown_or_inactive_channel_is_not_found()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var admin = factory.CreateClientAs(AppRoles.Admin);
        var slug = $"closed-{TestData.UniqueSuffix()}";
        var channel = await (await admin.PostAsJsonAsync(
            "/api/channels", new CreateChannelRequest("Closed shop", slug, ChannelType.Website))).ReadAsync<ChannelResponse>();
        await admin.PutAsJsonAsync($"/api/channels/{channel.ChannelId}", new UpdateChannelRequest("Closed shop", ChannelStatus.Inactive));
        var request = NewOrder(customerId, (product.ProductId, 1));

        var unknown = await _staff.PostAsJsonAsync($"/api/orders/no-such-{TestData.UniqueSuffix()}", request);
        var inactive = await _staff.PostAsJsonAsync($"/api/orders/{slug}", request);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(OrderErrorCodes.ChannelNotFound, await unknown.ReadErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
        Assert.Equal(OrderErrorCodes.ChannelNotFound, await inactive.ReadErrorCodeAsync());
        await AssertNothingWrittenAsync(request.ExternalOrderId);
    }

    [Fact]
    public async Task Unknown_customer_or_product_is_rejected_and_nothing_is_written()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();
        var unknownCustomer = NewOrder(Guid.NewGuid(), (product.ProductId, 1));
        var unknownProduct = NewOrder(customerId, (product.ProductId, 1), (Guid.NewGuid(), 1));

        var customerResponse = await _staff.PostAsJsonAsync("/api/orders/website", unknownCustomer);
        var productResponse = await _staff.PostAsJsonAsync("/api/orders/website", unknownProduct);

        Assert.Equal(new[] { "customerId" }, await ErrorKeysAsync(customerResponse));
        Assert.Equal(new[] { "items[1].productId" }, await ErrorKeysAsync(productResponse));
        await AssertNothingWrittenAsync(unknownCustomer.ExternalOrderId);
        await AssertNothingWrittenAsync(unknownProduct.ExternalOrderId);
    }

    [Fact]
    public async Task Invalid_requests_are_rejected()
    {
        var (customerId, product) = await CreateCustomerAndProductAsync();

        var empty = await _staff.PostAsJsonAsync("/api/orders/website", NewOrder(customerId));
        var duplicate = await _staff.PostAsJsonAsync("/api/orders/website", NewOrder(customerId, (product.ProductId, 1), (product.ProductId, 2)));
        var zeroStock = await _staff.PostAsJsonAsync("/api/orders/website", NewOrder(customerId, (product.ProductId, 0)));

        Assert.Equal(new[] { "items" }, await ErrorKeysAsync(empty));
        Assert.Equal(new[] { "items" }, await ErrorKeysAsync(duplicate));
        Assert.Equal(new[] { "items[0].stock" }, await ErrorKeysAsync(zeroStock));
    }

    [Fact]
    public async Task Anonymous_cannot_submit_orders()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/orders/website", NewOrder(Guid.NewGuid(), (Guid.NewGuid(), 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Decision A: intake does not depend on RabbitMQ. The integration test host has no broker at all (every test in this
    /// class runs without one), and no API-side assembly references a RabbitMQ client.
    /// </summary>
    [Fact]
    public void Api_has_no_rabbitmq_dependency()
    {
        Assembly[] apiSide = [typeof(Program).Assembly, typeof(OmnichannelDbContext).Assembly, typeof(SubmitOrderRequest).Assembly];

        Assert.All(apiSide, assembly => Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name!.Contains("RabbitMQ", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<(Guid CustomerId, ProductResponse Product)> CreateCustomerAndProductAsync(decimal price = 9.99m)
    {
        var customer = await (await _staff.PostAsJsonAsync(
            "/api/customers", new CustomerRequest("Order Customer", null, null, null))).ReadAsync<CustomerResponse>();

        var manager = factory.CreateClientAs(AppRoles.Manager);
        var sku = $"ORD-{TestData.UniqueSuffix()}".ToUpperInvariant();
        var productResponse = await manager.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, "Order product", price, 0, null));
        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);

        return (customer.CustomerId, await productResponse.ReadAsync<ProductResponse>());
    }

    private Task<(List<ProcessingTask> Tasks, List<OutboxEvent> Outbox, List<Order> Orders)>
        RowsAsync(string externalOrderId) =>
        factory.QueryDbAsync(async db =>
        {
            var tasks = await db.ProcessingTasks.AsNoTracking()
                .Where(t => t.IdempotencyKey != null && t.IdempotencyKey.EndsWith(":" + externalOrderId))
                .ToListAsync();
            var taskIds = tasks.Select(t => t.TaskId.ToString()).ToList();
            var outbox = await db.OutboxEvents.AsNoTracking().Where(e => taskIds.Contains(e.AggregateId)).ToListAsync();
            var orders = await db.Orders.AsNoTracking().Where(o => o.ExternalOrderId == externalOrderId).ToListAsync();
            return (tasks, outbox, orders);
        });

    private async Task AssertNothingWrittenAsync(string externalOrderId)
    {
        var (tasks, outbox, orders) = await RowsAsync(externalOrderId);
        Assert.Empty(tasks);
        Assert.Empty(outbox);
        Assert.Empty(orders);
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
        return body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();
    }

    private static SubmitOrderRequest NewOrder(Guid customerId, params (Guid ProductId, int Stock)[] items) =>
        new($"EXT-{TestData.UniqueSuffix()}", customerId, items.Select(i => new SubmitOrderItem(i.ProductId, i.Stock)).ToList());
}

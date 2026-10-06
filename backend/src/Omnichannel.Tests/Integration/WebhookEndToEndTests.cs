using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Enums;
using Omnichannel.Tests.Integration.Workers;
using Testcontainers.RabbitMq;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 023: a webhook all the way - signed request → API (customer, task, outbox) → Outbox Publisher → RabbitMQ →
/// Order Worker → reservation, with the real hosts (the pattern of <see cref="OrderEndToEndTests"/>).
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WebhookEndToEndTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(WorkersFixture.RabbitUser)
        .WithPassword(WorkersFixture.RabbitPassword)
        .Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task A_webhook_order_is_reserved_with_the_mapped_products_their_prices_and_the_channel_s_customer()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var tag = TestData.UniqueSuffix();
        var website = (await manager.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json))!.Single(c => c.Slug == "website");

        // Two products with stock: the website knows one by an external product id, the other by an external SKU.
        async Task<ProductResponse> ProductAsync(decimal price, string? externalProductId, string? externalSku)
        {
            var product = await (await manager.PostAsJsonAsync(
                "/api/products", new CreateProductRequest($"WHE-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Webhook E2E product", price, 0, null)))
                .ReadAsync<ProductResponse>();
            var imported = await manager.PostAsJsonAsync(
                "/api/inventory/import",
                new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, 10)]));
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var mapped = await manager.PostAsJsonAsync(
                $"/api/channels/{website.ChannelId}/products", new CreateChannelProductRequest(product.ProductId, externalProductId, externalSku));
            Assert.Equal(HttpStatusCode.Created, mapped.StatusCode);
            return product;
        }

        var byId = await ProductAsync(7.5m, $"E2E-ID-{tag}", null);
        var bySku = await ProductAsync(20m, null, $"E2E-SKU-{tag}");

        var (orderId, customerId, correlationId) = ($"E2E-ORDER-{tag}", $"E2E-CUSTOMER-{tag}", $"e2e-hook-{tag}");
        var body = WebhookTesting.Order(
            orderId,
            WebhookTesting.Customer(customerId, $"E2E Webhook Customer {tag}", "0933 333 333"),
            WebhookTesting.ById($"E2E-ID-{tag}", 4),
            WebhookTesting.BySku($"E2E-SKU-{tag}", 1));
        var sent = await factory.CreateAnonymousClient().SendAsync(
            WebhookTesting.Request("website", body, ApiFactory.WebsiteWebhookSecret, correlationId: correlationId));
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var taskId = (await sent.ReadAsync<SubmitOrderResponse>()).TaskId;

        using var workers = WorkersFixture.BuildWorkersHost(
            factory.DatabaseConnectionString, _rabbit.Hostname, _rabbit.GetMappedPublicPort(5672), "webhook-e2e-workers");
        await workers.StartAsync();
        try
        {
            await Eventually.WaitAsync(
                () => factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.Status).SingleAsync()),
                status => status == ProcessingTaskStatus.Succeeded,
                TimeSpan.FromSeconds(60),
                "webhook order processed end to end");
        }
        finally
        {
            await workers.StopAsync();
        }

        var task = await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId));
        Assert.Equal(correlationId, task.CorrelationId);
        var order = await factory.QueryDbAsync(db => db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.OrderId == task.OrderId));

        // The order of the channel, under the channel's order id, for the customer the channel named ...
        Assert.Equal((OrderStatus.Reserved, website.ChannelId, orderId), (order.Status, order.ChannelId, order.ExternalOrderId));
        var mapping = await factory.QueryDbAsync(db => db.ChannelCustomers.AsNoTracking()
            .SingleAsync(m => m.ChannelId == website.ChannelId && m.ExternalCustomerId == customerId));
        var customer = await factory.QueryDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.CustomerId == order.CustomerId));
        Assert.Equal(mapping.CustomerId, order.CustomerId);
        Assert.Equal(($"E2E Webhook Customer {tag}", "0933 333 333", "webhook:website"), (customer.Name, customer.Phone, customer.CreatedBy));

        // ... with the products the channel's ids are mapped to, at the products' own prices (the webhook names none).
        Assert.Equal(
            new[] { (byId.ProductId, 4, 7.5m), (bySku.ProductId, 1, 20m) }.Order(),
            order.Items.Select(i => (i.ProductId, i.Stock, i.UnitPrice)).Order());
        Assert.Equal(50m, order.TotalAmount);

        // And the stock is reserved, as for an order of the order-entry page.
        var stock = await factory.QueryDbAsync(db => db.Inventories.AsNoTracking()
            .Where(i => i.ProductId == byId.ProductId || i.ProductId == bySku.ProductId)
            .ToDictionaryAsync(i => i.ProductId, i => (i.TotalStock, i.ReservedStock, i.AvailableStock)));
        Assert.Equal(((10, 4, 6), (10, 1, 9)), (stock[byId.ProductId], stock[bySku.ProductId]));

        // The order is readable through the API like any other, with the customer's name.
        var read = await manager.GetFromJsonAsync<OrderDetailResponse>($"/api/orders/{order.OrderId}", TestData.Json);
        Assert.Equal((orderId, $"E2E Webhook Customer {tag}", 2), (read!.ExternalOrderId, read.Customer.Name, read.ItemCount));
    }
}

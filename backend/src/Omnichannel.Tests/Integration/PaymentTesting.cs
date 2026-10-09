using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>What the payment tests of the API collection share: seeded orders and the reads of what a payment wrote.</summary>
internal static class PaymentTesting
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A product (with stock), a customer and an order of two units, stored as the Order Worker stores them.</summary>
    /// <param name="email">The customer's e-mail address; null = none.</param>
    public static async Task<SeededOrder> SeedOrderAsync(
        ApiFactory factory,
        OrderStatus status = OrderStatus.Reserved,
        string? email = "customer@example.com",
        string? externalOrderId = null,
        string channelSlug = "website")
    {
        var suffix = TestData.UniqueSuffix().ToUpperInvariant();
        var product = new Product($"PAY-{suffix}", $"Paid product {suffix}", 75_000m, 0, null);
        var customer = new Customer($"Paying customer {suffix}", "0901 234 567", email, "1 Private Street");

        return await factory.QueryDbAsync(async db =>
        {
            var channelId = await db.Channels.Where(c => c.Slug == channelSlug).Select(c => c.ChannelId).SingleAsync();
            var order = new Order(channelId, customer.CustomerId, externalOrderId ?? $"PAY-{suffix}", status);
            order.AddItem(product.ProductId, 2, 75_000m);

            db.Products.Add(product);
            // As after a reservation: 2 of 10 reserved.
            var inventory = new Inventory(product.ProductId, 10);
            db.Inventories.Add(inventory);
            db.Customers.Add(customer);
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Inventories" SET "ReservedStock" = 2 WHERE "ProductId" = {product.ProductId}""");
            return new SeededOrder(order.OrderId, order.ExternalOrderId, customer.CustomerId, product.ProductId, product.Sku, product.Name, customer.Name);
        });
    }

    public static Task<HttpResponseMessage> PayAsync(
        HttpClient client, Guid orderId, object? body = null, string? language = null, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/payment")
        {
            Content = JsonContent.Create(body ?? new { paymentMethod = "Cash" })
        };
        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return client.SendAsync(request);
    }

    public static async Task<PayOrderResponse> PaidAsync(HttpClient client, Guid orderId, object? body = null)
    {
        var response = await PayAsync(client, orderId, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"payment: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<PayOrderResponse>();
    }

    public static Task<Order> OrderAsync(ApiFactory factory, Guid orderId) =>
        factory.QueryDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId));

    /// <summary>The tasks of an order, oldest first, with their attempts.</summary>
    public static Task<List<ProcessingTask>> TasksAsync(ApiFactory factory, Guid orderId) =>
        factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking()
            .Include(t => t.Attempts)
            .Where(t => t.OrderId == orderId)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.TaskId)
            .ToListAsync());

    public static Task<ProcessingTask> TaskAsync(ApiFactory factory, Guid taskId) =>
        factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    public static Task<ProcessingTask> WaitForTaskAsync(ApiFactory factory, Guid taskId, Func<ProcessingTask, bool> done, string what) =>
        Eventually.WaitAsync(() => TaskAsync(factory, taskId), done, WaitTimeout, $"task {taskId}: {what}");

    public static Task<ProcessingTask> WaitForStatusAsync(ApiFactory factory, Guid taskId, ProcessingTaskStatus status) =>
        WaitForTaskAsync(factory, taskId, t => t.Status == status, status.ToString());

    public static Task<Invoice?> InvoiceAsync(ApiFactory factory, Guid orderId) =>
        factory.QueryDbAsync(db => db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.OrderId == orderId));

    public static Task<(int Total, int Reserved, int Available)> StockAsync(ApiFactory factory, Guid productId) =>
        factory.QueryDbAsync(async db =>
        {
            var row = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            return (row.TotalStock, row.ReservedStock, row.AvailableStock);
        });
}

internal sealed record SeededOrder(
    Guid OrderId,
    string ExternalOrderId,
    Guid CustomerId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string CustomerName);

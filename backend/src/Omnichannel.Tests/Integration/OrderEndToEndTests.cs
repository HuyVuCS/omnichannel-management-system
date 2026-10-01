using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Enums;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.OrderWorker;
using Testcontainers.RabbitMq;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// API → PostgreSQL (task + outbox) → Outbox Publisher → RabbitMQ → Order Worker → reservation, with the real hosts.
/// Runs in the API collection (one WebApplicationFactory per process); the Workers host shares the API test database and
/// uses a broker started for this class.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderEndToEndTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(WorkersFixture.RabbitUser)
        .WithPassword(WorkersFixture.RabbitPassword)
        .Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task Order_submitted_through_the_api_is_reserved_by_the_worker_with_one_correlation_id()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var sku = $"E2E-{TestData.UniqueSuffix()}".ToUpperInvariant();
        var product = await (await manager.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, "E2E product", 7.5m, 0, null)))
            .ReadAsync<ProductResponse>();
        var imported = await manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, 10)]));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var customer = await (await staff.PostAsJsonAsync("/api/customers", new CustomerRequest("E2E customer", null, null, null)))
            .ReadAsync<CustomerResponse>();

        var correlationId = $"e2e-{TestData.UniqueSuffix()}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/website")
        {
            Content = JsonContent.Create(new SubmitOrderRequest(
                $"E2E-{TestData.UniqueSuffix()}", customer.CustomerId, [new SubmitOrderItem(product.ProductId, 4)]))
        };
        request.Headers.Add("X-Correlation-Id", correlationId);
        var submitted = await staff.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var taskId = (await submitted.ReadAsync<SubmitOrderResponse>()).TaskId;

        var logs = new CapturingLoggerProvider();
        using var workers = WorkersFixture.BuildWorkersHost(
            factory.DatabaseConnectionString,
            _rabbit.Hostname,
            _rabbit.GetMappedPublicPort(5672),
            "e2e-workers",
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        await workers.StartAsync();
        try
        {
            await Eventually.WaitAsync(
                () => factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Where(t => t.TaskId == taskId).Select(t => t.Status).SingleAsync()),
                status => status == ProcessingTaskStatus.Succeeded,
                TimeSpan.FromSeconds(60),
                "order task processed end to end");
        }
        finally
        {
            await workers.StopAsync();
        }

        var task = await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));
        var order = await factory.QueryDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == task.OrderId));
        Assert.Equal(OrderStatus.Reserved, order.Status);
        Assert.Equal(30m, order.TotalAmount);
        Assert.Equal(correlationId, task.CorrelationId);
        Assert.Equal(ProcessingAttemptStatus.Succeeded, Assert.Single(task.Attempts).Status);

        var stock = await factory.QueryDbAsync(db => db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == product.ProductId));
        Assert.Equal((10, 4, 6), (stock.TotalStock, stock.ReservedStock, stock.AvailableStock));

        // Movement history (task 012): the import names the manager, the worker's reservation is a system actor.
        var movements = (await staff.GetFromJsonAsync<PagedResult<InventoryMovementResponse>>(
            $"/api/inventory/movements?productId={product.ProductId}", TestData.Json))!.Items;
        var reserve = Assert.Single(movements, m => m.Type == InventoryMovementType.Reserve);
        Assert.StartsWith("system:", reserve.CreatedBy);
        Assert.Null(reserve.CreatedByEmail);
        Assert.Equal("manager@integration.test", Assert.Single(movements, m => m.Type == InventoryMovementType.Import).CreatedByEmail);

        // The order's inventory.changed outbox rows carry the request's correlation id ...
        var reserveOutbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking()
            .Where(e => e.EventType == EventTypes.InventoryChanged && e.InventoryMovement!.ReferenceId == order.OrderId.ToString())
            .ToListAsync());
        Assert.Equal(correlationId, Assert.Single(reserveOutbox).CorrelationId);

        // ... and so does every log line the worker wrote for this task (scope values).
        var workerLogs = logs.Entries
            .Where(e => e.Category == typeof(OrderMessageHandler).FullName && e.Scope.TryGetValue("TaskId", out var id) && Equals(id, taskId))
            .ToList();
        Assert.NotEmpty(workerLogs);
        Assert.All(workerLogs, e =>
        {
            Assert.Equal(correlationId, e.Scope["CorrelationId"]);
            Assert.Equal(OrderMessageHandler.WorkerName, e.Scope["WorkerName"]);
            Assert.Equal("e2e-workers", e.Scope["WorkerInstanceId"]);
        });
        Assert.Contains(workerLogs, e => e.Scope.TryGetValue("AttemptNo", out var attemptNo) && Equals(attemptNo, 1));
    }
}

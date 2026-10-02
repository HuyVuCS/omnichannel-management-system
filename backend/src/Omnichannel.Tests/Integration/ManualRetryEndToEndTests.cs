using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.OrderWorker;
using Omnichannel.Workers.Processing;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Omnichannel.Tests.Integration;

/// <summary>One RabbitMQ broker for the manual-retry end-to-end tests.</summary>
public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(WorkersFixture.RabbitUser)
        .WithPassword(WorkersFixture.RabbitPassword)
        .Build();

    public string HostName => _rabbit.Hostname;

    public int Port => _rabbit.GetMappedPublicPort(5672);

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() => new ConnectionFactory
    {
        HostName = HostName,
        Port = Port,
        UserName = WorkersFixture.RabbitUser,
        Password = WorkersFixture.RabbitPassword,
        AutomaticRecoveryEnabled = false
    }.CreateConnectionAsync();
}

/// <summary>
/// Task 008: <c>POST /api/processing/tasks/{id}/retry</c> through the real path — API → outbox → Outbox Publisher → RabbitMQ →
/// Order Worker. The Workers host runs against the API test database (it also processes order tasks other tests left
/// behind; failures are injected for this test's task only).
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ManualRetryEndToEndTests(ApiFactory factory, RabbitMqBrokerFixture broker) : IClassFixture<RabbitMqBrokerFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string DeadLetterQueue = "order.processing.dlq";

    [Fact]
    public async Task Failed_task_retried_manually_is_processed_again_and_the_order_is_reserved()
    {
        var managerId = factory.UserIdOf(AppRoles.Manager);
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var (taskId, productId) = await SubmitOrderAsync(stock: 10, quantity: 4);
        // Attempt 1 hits a permanent error (check violation); afterwards the "bug" is fixed and processing works again.
        var failures = new FailureInjection
        {
            OnlyTaskId = taskId,
            ReserveFailures = 1,
            ReserveException = () => new PostgresException("new row violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)
        };

        using var workers = await StartWorkersAsync(failures);
        try
        {
            var failed = await WaitForTaskAsync(taskId, t => t.Status == ProcessingTaskStatus.Failed);
            await WaitForDeadLetterAsync(failed.MessageId!);

            var response = await manager.PostAsync($"/api/processing/tasks/{taskId}/retry", null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var retry = await response.ReadAsync<RetryProcessingTaskResponse>();
            Assert.Equal((taskId, ProcessingTaskStatus.Pending), (retry.TaskId, retry.Status));
            Assert.NotEqual(failed.MessageId, retry.MessageId);

            var done = await WaitForTaskAsync(taskId, t => t.Status == ProcessingTaskStatus.Succeeded);
            var attempts = done.Attempts.OrderBy(a => a.AttemptNo).ToList();
            Assert.Equal(2, attempts.Count);
            Assert.Equal((1, ProcessingAttemptStatus.Failed, "check_violation"), (attempts[0].AttemptNo, attempts[0].Status, attempts[0].ErrorCode));
            Assert.Equal((2, ProcessingAttemptStatus.Succeeded), (attempts[1].AttemptNo, attempts[1].Status));
            Assert.Equal((retry.MessageId, 1), (done.MessageId, done.AttemptBudgetStart));

            // The order, seen through the API: reserved, with both attempts of its task.
            var order = await (await factory.CreateClientAs(AppRoles.Staff).GetAsync($"/api/orders/{done.OrderId}")).ReadAsync<OrderDetailResponse>();
            Assert.Equal(OrderStatus.Reserved, order.Status);
            var task = Assert.Single(order.Tasks);
            Assert.Equal([1, 2], task.Attempts.Select(a => a.AttemptNo));
            Assert.Equal(("check_violation", OrderMessageHandler.WorkerName, "e2e-retry-workers"),
                (task.Attempts[0].ErrorCode, task.Attempts[0].WorkerName, task.Attempts[0].WorkerInstanceId));

            // The new message went through the outbox and was published.
            var detail = await Eventually.WaitAsync(
                async () => await (await manager.GetAsync($"/api/processing/tasks/{taskId}")).ReadAsync<ProcessingTaskDetailResponse>(),
                d => d.Message?.Status == OutboxEventStatus.Published,
                Timeout,
                "retry message published");
            Assert.Equal(Guid.Parse(retry.MessageId), detail.Message!.OutboxEventId);
        }
        finally
        {
            await workers.StopAsync();
        }

        Assert.Equal((10, 4, 6), await StockAsync(productId));
        Assert.Equal(2, await factory.QueryDbAsync(db => db.OutboxEvents.CountAsync(e => e.AggregateId == taskId.ToString())));

        var retryEvent = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(l => l.EventType == ProcessingEvents.RetryRequested && l.TaskId == taskId));
        Assert.Equal(managerId.ToString(), JsonNode.Parse(retryEvent.Payload!)!["requestedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Manual_retry_gives_a_fresh_attempt_budget_while_the_failure_persists()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var (taskId, productId) = await SubmitOrderAsync(stock: 10, quantity: 1);
        var failures = new FailureInjection { OnlyTaskId = taskId, ReserveFailures = int.MaxValue }; // transient timeouts, forever

        using var workers = await StartWorkersAsync(failures, maxAttempts: 3);
        try
        {
            var firstFailure = await WaitForTaskAsync(taskId, t => t.Status == ProcessingTaskStatus.Failed);
            Assert.Equal(3, firstFailure.Attempts.Count);

            var response = await manager.PostAsync($"/api/processing/tasks/{taskId}/retry", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var secondFailure = await WaitForTaskAsync(taskId, t => t.Status == ProcessingTaskStatus.Failed && t.Attempts.Count > 3);

            // MaxAttempts new attempts (not zero, not one), numbered on from the first round.
            var attempts = secondFailure.Attempts.OrderBy(a => a.AttemptNo).ToList();
            Assert.Equal([1, 2, 3, 4, 5, 6], attempts.Select(a => a.AttemptNo));
            Assert.All(attempts, a => Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingErrorClassifier.Timeout), (a.Status, a.ErrorCode)));
            Assert.Equal(3, secondFailure.AttemptBudgetStart);
        }
        finally
        {
            await workers.StopAsync();
        }

        var failedEvents = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == ProcessingEvents.Failed && l.TaskId == taskId)
            .OrderBy(l => l.LogId)
            .ToListAsync());
        Assert.Equal([3, 6], failedEvents.Select(l => JsonNode.Parse(l.Payload!)!["attemptNo"]!.GetValue<int>()));
        Assert.Equal((10, 0, 10), await StockAsync(productId));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<(Guid TaskId, Guid ProductId)> SubmitOrderAsync(int stock, int quantity)
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var sku = $"RETRY-{TestData.UniqueSuffix()}".ToUpperInvariant();
        var product = await (await manager.PostAsJsonAsync("/api/products", new CreateProductRequest(sku, "Retry product", 2m, 0, null)))
            .ReadAsync<ProductResponse>();
        var imported = await manager.PostAsJsonAsync(
            "/api/inventory/import",
            new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, stock)]));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var customer = await (await manager.PostAsJsonAsync("/api/customers", new CustomerRequest("Retry customer", null, null, null)))
            .ReadAsync<CustomerResponse>();

        var submitted = await manager.PostAsJsonAsync(
            "/api/orders/website",
            new SubmitOrderRequest($"RETRY-{TestData.UniqueSuffix()}", customer.CustomerId, [new SubmitOrderItem(product.ProductId, quantity)]));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        return ((await submitted.ReadAsync<SubmitOrderResponse>()).TaskId, product.ProductId);
    }

    private async Task<IHost> StartWorkersAsync(FailureInjection failures, int? maxAttempts = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxAttempts is { } max)
        {
            settings["OrderWorker:MaxAttempts"] = max.ToString();
        }

        var host = WorkersFixture.BuildWorkersHost(
            factory.DatabaseConnectionString,
            broker.HostName,
            broker.Port,
            "e2e-retry-workers",
            settings,
            services => services.InjectFailures(failures));
        await host.StartAsync();
        return host;
    }

    private Task<ProcessingTask> WaitForTaskAsync(Guid taskId, Func<ProcessingTask, bool> done) =>
        Eventually.WaitAsync(
            () => factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId)),
            done,
            Timeout,
            $"task {taskId}");

    /// <summary>Drains the order DLQ until <paramref name="messageId"/> was seen (other tests' poison messages may be there too).</summary>
    private async Task WaitForDeadLetterAsync(string messageId)
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var seen = new List<string?>();
        await Eventually.WaitAsync(
            async () =>
            {
                while (await channel.BasicGetAsync(DeadLetterQueue, autoAck: true) is { } message)
                {
                    seen.Add(message.BasicProperties.MessageId);
                }

                return seen.Contains(messageId);
            },
            found => found,
            Timeout,
            $"message {messageId} in {DeadLetterQueue}");
    }

    private Task<(int Total, int Reserved, int Available)> StockAsync(Guid productId) =>
        factory.QueryDbAsync(async db =>
        {
            var inventory = await db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == productId);
            return (inventory.TotalStock, inventory.ReservedStock, inventory.AvailableStock);
        });
}

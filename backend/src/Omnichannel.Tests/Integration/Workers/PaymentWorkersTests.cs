using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.ExternalProviders;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using Omnichannel.Workers.InvoiceWorker;
using Omnichannel.Workers.NotificationWorker;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 024 Parts C and D: the Invoice Worker and the Notification Worker against real PostgreSQL and RabbitMQ (test
/// delay tiers 1 s / 2 s / 3 s / 4 s). The providers are the production simulators behind their production timeout and
/// circuit breaker, with the Development rules switched on; a probe records the calls that reach them.
/// </summary>
[Collection(WorkersCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class PaymentWorkersTests(WorkersFixture fixture) : IAsyncLifetime
{
    private const string InvoiceQueue = RabbitMqTopology.InvoiceProcessingQueue;
    private const string EmailQueue = RabbitMqTopology.EmailNotificationQueue;

    /// <summary>The user who records the payments of these tests (the payment service needs one).</summary>
    private static readonly string Payer = Guid.NewGuid().ToString();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    public Task InitializeAsync() => fixture.PurgeQueuesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- success -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_paid_order_gets_its_invoice_and_its_confirmation_by_two_independent_tasks()
    {
        var order = await SeedOrderAsync(email: "anna.nguyen@example.com");
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();
        var logs = new CapturingLoggerProvider();
        var before = DateTimeOffset.UtcNow;

        await RunWorkersAsync(
            "pay-success",
            async () =>
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
                await WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded);
                await WaitForEmptyAsync(InvoiceQueue, EmailQueue);
            },
            probe,
            logs);

        // ---- the invoice: what the provider issued for the key of the order, with the order's total ----
        var invoice = await InvoiceAsync(order.OrderId);
        var key = $"invoice:{order.OrderId}";
        Assert.Equal(
            (SimulatedInvoiceProviderClient.InvoiceNumberOf(key, paid.PaidAt), 150_000m, key, InvoiceMessageHandler.Actor),
            (invoice!.InvoiceNumber, invoice.TotalAmount, invoice.ProviderReference, invoice.CreatedBy));
        Assert.Matches("^SIM-[0-9]{6}-[0-9A-F]{8}$", invoice.InvoiceNumber);
        Assert.InRange(invoice.IssuedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.InRange(invoice.CreatedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        var request = Assert.Single(probe.InvoiceRequestsOf(order.OrderId));
        Assert.Equal(
            (key, order.ExternalOrderId, order.CustomerName, 150_000m, 1),
            (request.Key, request.ExternalOrderId, request.CustomerName, request.Total, request.AttemptNo));
        Assert.Equal(paid.PaidAt, request.PaidAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal((order.Sku, order.ProductName, 2, 75_000m), Assert.Single(request.Lines.Select(l => (l.Sku, l.Name, l.Quantity, l.UnitPrice))));

        // ---- the e-mail: to the customer, in Vietnamese (the default), built from the order ----
        var email = Assert.Single(probe.EmailsOf(order.OrderId));
        Assert.Equal(("anna.nguyen@example.com", $"Đã nhận thanh toán cho đơn hàng {order.ExternalOrderId}", 1), (email.To, email.Subject, email.AttemptNo));
        Assert.Contains($"Xin chào {order.CustomerName},", email.TextBody);
        Assert.Contains($"- {order.Sku} | {order.ProductName} | 2 x 75.000 = 150.000", email.TextBody);
        Assert.Contains("Tổng tiền: 150.000 VND", email.TextBody);
        Assert.Contains("(Asia/Ho_Chi_Minh)", email.TextBody);

        // ---- the tasks: one attempt each, by its own worker ----
        var invoiceTask = await TaskAsync(paid.InvoiceTaskId);
        var emailTask = await TaskAsync(paid.EmailTaskId!.Value);
        foreach (var (task, worker) in new[] { (invoiceTask, InvoiceMessageHandler.WorkerName), (emailTask, EmailMessageHandler.WorkerName) })
        {
            var attempt = Assert.Single(task.Attempts);
            Assert.Equal(
                (ProcessingTaskStatus.Succeeded, 1, ProcessingAttemptStatus.Succeeded, worker, "pay-success", (string?)null, (Guid?)order.OrderId),
                (task.Status, attempt.AttemptNo, attempt.Status, attempt.WorkerName, attempt.WorkerInstanceId, attempt.ErrorCode, task.OrderId));
            Assert.NotNull(attempt.FinishedAt);
        }

        // ---- the order and its stock: exactly as the payment left them ----
        await AssertOrderUntouchedAsync(order, paid);
        Assert.Empty(await ProcessingFailedLogsAsync(paid.InvoiceTaskId, paid.EmailTaskId.Value));
        await AssertNoDeadLettersAsync();

        // ---- the logs: every line of a task carries its ids; no line carries the address or the text ----
        foreach (var (taskId, category, worker) in new[]
                 {
                     (paid.InvoiceTaskId, typeof(InvoiceMessageHandler).FullName, InvoiceMessageHandler.WorkerName),
                     (paid.EmailTaskId.Value, typeof(EmailMessageHandler).FullName, EmailMessageHandler.WorkerName)
                 })
        {
            var lines = logs.Entries.Where(e => e.Category == category && Equals(e.Scope.GetValueOrDefault("TaskId"), taskId)).ToList();
            Assert.NotEmpty(lines);
            Assert.All(lines, e =>
            {
                Assert.Equal((paid.CorrelationId, worker, "pay-success"), (e.Scope["CorrelationId"], e.Scope["WorkerName"], e.Scope["WorkerInstanceId"]));
                Assert.Equal(order.OrderId, e.Scope["OrderId"]);
                Assert.Equal(1, e.Scope["AttemptNo"]);
                Assert.True(e.Scope.ContainsKey("MessageId"));
            });
        }

        AssertNothingPersonalLogged(logs, "anna.nguyen", email.TextBody);
        Assert.Contains(logs.Entries, e => e.Message.Contains("Simulated e-mail to a***@example.com", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Payment confirmation sent to a***@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_language_and_time_zone_of_the_e_mail_are_settings_of_the_host()
    {
        var order = await SeedOrderAsync(email: "berlin@example.com");
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();

        await RunWorkersAsync(
            "pay-english",
            () => WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded),
            probe,
            settings: new Dictionary<string, string?> { ["Notifications:Language"] = "en", ["Notifications:TimeZone"] = "Europe/Berlin" });

        var email = Assert.Single(probe.EmailsOf(order.OrderId));
        Assert.Equal($"Payment received for order {order.ExternalOrderId}", email.Subject);
        Assert.Contains("Total: 150,000 VND", email.TextBody);
        var local = TimeZoneInfo.ConvertTime(paid.PaidAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        Assert.Contains($"Paid at: {local:yyyy-MM-dd HH:mm} (Europe/Berlin)", email.TextBody);
    }

    [Fact]
    public async Task An_order_whose_customer_has_no_address_gets_its_invoice_and_no_e_mail()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-no-email", () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded), probe);

        Assert.Null(paid.EmailTaskId);
        Assert.NotNull(await InvoiceAsync(order.OrderId));
        Assert.Empty(probe.EmailsOf(order.OrderId));
        Assert.Equal(ProcessingTaskType.Invoice, Assert.Single(await TasksOfAsync(order.OrderId)).TaskType);
    }

    // ---- deliveries --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redelivery_after_the_provider_call_and_before_the_commit_ends_with_one_invoice_of_the_same_number()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        // The provider answered, then the completion failed: nothing of the invoice is stored yet.
        var probe = new PaymentProbe { OnlyTaskId = paid.InvoiceTaskId, InvoiceCompletionFailures = 1 };

        await RunWorkersAsync("pay-redelivery", () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded), probe);

        var attempts = (await TaskAsync(paid.InvoiceTaskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Failed, "timeout"), (2, ProcessingAttemptStatus.Succeeded, null)],
            attempts.Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));

        // Asked twice with the one key of the order: the provider answered with the same invoice both times.
        var requests = probe.InvoiceRequestsOf(order.OrderId);
        Assert.Equal([(1, $"invoice:{order.OrderId}"), (2, $"invoice:{order.OrderId}")], requests.Select(r => (r.AttemptNo, r.Key)));
        var invoice = await InvoiceAsync(order.OrderId);
        Assert.Equal(SimulatedInvoiceProviderClient.InvoiceNumberOf(requests[0].Key, paid.PaidAt), invoice!.InvoiceNumber);
        Assert.Equal(1, await fixture.WithDbAsync(db => db.Invoices.CountAsync(i => i.OrderId == order.OrderId)));
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task An_invoice_stored_by_another_delivery_while_this_one_called_the_provider_stays_and_the_task_completes()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        var first = $"FIRST-{_tag}";
        var issuedAt = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
        // Between this delivery's read and its completion another one stored the invoice of the order.
        var probe = new PaymentProbe
        {
            OnlyTaskId = paid.InvoiceTaskId,
            BeforeInvoiceCompletion = () => fixture.WithDbAsync(db =>
            {
                db.Invoices.Add(new Invoice(order.OrderId, first, issuedAt, 150_000m, Invoice.ProviderKeyOf(order.OrderId)));
                return db.SaveChangesAsync();
            })
        };

        await RunWorkersAsync("pay-conflict", () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded), probe);

        // Nothing failed and nothing was replaced: one attempt, the invoice that was first.
        var attempt = Assert.Single((await TaskAsync(paid.InvoiceTaskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Succeeded, (string?)null), (attempt.Status, attempt.ErrorCode));
        var invoice = await InvoiceAsync(order.OrderId);
        Assert.Equal((first, issuedAt), (invoice!.InvoiceNumber, invoice.IssuedAt));
        Assert.Single(probe.InvoiceRequestsOf(order.OrderId));
    }

    [Fact]
    public async Task A_task_completed_by_another_delivery_while_this_one_called_the_provider_only_closes_its_attempt()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        var other = $"OTHER-{_tag}";
        // A second delivery of the same message (another instance) starts its attempt and completes the task while this
        // one is between the provider's answer and its own completion.
        var probe = new PaymentProbe
        {
            OnlyTaskId = paid.InvoiceTaskId,
            BeforeInvoiceCompletion = () => fixture.WithServicesAsync(async services =>
            {
                var invoices = services.GetRequiredService<IInvoiceProcessingService>();
                var attempt = await invoices.StartAttemptAsync(paid.InvoiceTaskId, InvoiceMessageHandler.WorkerName, "other-instance", CancellationToken.None);
                await invoices.CompleteAsync(
                    paid.InvoiceTaskId,
                    attempt!.AttemptId,
                    new Invoice(order.OrderId, other, DateTimeOffset.UtcNow, 150_000m, Invoice.ProviderKeyOf(order.OrderId)),
                    CancellationToken.None);
                return true;
            })
        };

        await RunWorkersAsync(
            "pay-overtaken",
            async () =>
            {
                await WaitForTaskAsync(paid.InvoiceTaskId, t => t.Attempts.Count == 2 && t.Attempts.All(a => a.FinishedAt != null), "both attempts closed");
                await WaitForEmptyAsync(InvoiceQueue);
            },
            probe);

        // The task was completed once, by the delivery that was first; this one closed its attempt and stored nothing.
        var task = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal(ProcessingTaskStatus.Succeeded, task.Status);
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Succeeded, "pay-overtaken", null), (2, ProcessingAttemptStatus.Succeeded, "other-instance", null)],
            task.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.AttemptNo, a.Status, a.WorkerInstanceId, a.ErrorCode)));
        Assert.Equal(0, task.RetryCount);
        Assert.Equal(other, (await InvoiceAsync(order.OrderId))!.InvoiceNumber);
        Assert.Single(probe.InvoiceRequestsOf(order.OrderId));
        Assert.Empty(await ProcessingFailedLogsAsync(paid.InvoiceTaskId));
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task An_order_that_has_its_invoice_completes_a_new_delivery_without_a_call_to_the_provider()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        var stored = $"STORED-{_tag}";
        // The invoice was committed by a delivery whose task update was lost with it never being acknowledged: here the
        // row is simply there before the worker starts.
        await fixture.WithDbAsync(db =>
        {
            db.Invoices.Add(new Invoice(order.OrderId, stored, DateTimeOffset.UtcNow, 150_000m, Invoice.ProviderKeyOf(order.OrderId)));
            return db.SaveChangesAsync();
        });
        var probe = new PaymentProbe();
        var logs = new CapturingLoggerProvider();

        await RunWorkersAsync("pay-stored", () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded), probe, logs);

        Assert.Empty(probe.InvoiceRequestsOf(order.OrderId));
        Assert.Equal(stored, (await InvoiceAsync(order.OrderId))!.InvoiceNumber);
        Assert.Equal(ProcessingAttemptStatus.Succeeded, Assert.Single((await TaskAsync(paid.InvoiceTaskId)).Attempts).Status);
        Assert.Contains(logs.Entries, e =>
            e.Category == typeof(InvoiceMessageHandler).FullName && e.Message.Contains("without a call to the provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_delivery_of_a_finished_task_is_acknowledged_without_another_attempt_or_call()
    {
        var order = await SeedOrderAsync(email: "twice@example.com");
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();
        var logs = new CapturingLoggerProvider();

        await RunWorkersAsync(
            "pay-duplicate",
            async () =>
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
                await WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded);

                // The very same messages again (publisher crash between confirm and commit, or a late retry copy).
                foreach (var taskId in new[] { paid.InvoiceTaskId, paid.EmailTaskId.Value })
                {
                    var message = await fixture.WithDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == taskId.ToString()));
                    await fixture.PublishAsync(
                        message.EventType, MessageJson.SerializeToUtf8Bytes(MessageEnvelope.FromOutboxEvent(message)), message.OutboxEventId.ToString());
                }

                await Eventually.WaitAsync(
                    () => Task.FromResult(logs.Entries.Count(e => e.Message.Contains("Duplicate delivery", StringComparison.Ordinal)
                        && (Equals(e.Scope.GetValueOrDefault("TaskId"), paid.InvoiceTaskId) || Equals(e.Scope.GetValueOrDefault("TaskId"), paid.EmailTaskId.Value)))),
                    count => count == 2,
                    Timeout,
                    "both duplicate deliveries acknowledged");
                await WaitForEmptyAsync(InvoiceQueue, EmailQueue);
            },
            probe,
            logs);

        Assert.Single((await TaskAsync(paid.InvoiceTaskId)).Attempts);
        Assert.Single((await TaskAsync(paid.EmailTaskId!.Value)).Attempts);
        Assert.Single(probe.InvoiceRequestsOf(order.OrderId));
        // One e-mail: the customer is not written to twice.
        Assert.Single(probe.EmailsOf(order.OrderId));
        await AssertNoDeadLettersAsync();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private sealed record Seeded(Guid OrderId, string ExternalOrderId, Guid CustomerId, Guid ProductId, string Sku, string ProductName, string CustomerName);

    /// <param name="CorrelationId">The correlation id of the payment, and so of its tasks and messages.</param>
    private sealed record Paid(Guid InvoiceTaskId, Guid? EmailTaskId, DateTimeOffset PaidAt, string CorrelationId);

    /// <summary>A product (2 of 10 reserved), a customer and an order of two units, as the Order Worker stores them.</summary>
    private async Task<Seeded> SeedOrderAsync(string? email, string? orderPrefix = null, OrderStatus status = OrderStatus.Reserved)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var product = new Product($"PW-{_tag}-{suffix}", $"Paid product {suffix}", 75_000m, 0, null);
        var customer = new Customer($"Paying customer {suffix}", null, email, null);

        return await fixture.WithDbAsync(async db =>
        {
            var channelId = await db.Channels.Where(c => c.Slug == "website").Select(c => c.ChannelId).SingleAsync();
            var order = new Order(channelId, customer.CustomerId, $"{orderPrefix}PW-{_tag}-{suffix}", status);
            order.AddItem(product.ProductId, 2, 75_000m);

            db.Products.Add(product);
            db.Inventories.Add(new Inventory(product.ProductId, 10));
            db.Customers.Add(customer);
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Inventories" SET "ReservedStock" = 2 WHERE "ProductId" = {product.ProductId}""");
            return new Seeded(order.OrderId, order.ExternalOrderId, customer.CustomerId, product.ProductId, product.Sku, product.Name, customer.Name);
        });
    }

    /// <summary>The payment step of the API, run by the service the endpoint calls (no API host in this collection).</summary>
    private async Task<Paid> PayAsync(Guid orderId)
    {
        var (response, correlationId) = await fixture.WithServicesAsync(async services =>
        {
            services.GetRequiredService<WorkerCurrentUser>().UserId = Payer;
            var correlation = services.GetRequiredService<WorkerCorrelationContext>().CorrelationId;
            var paid = await services.GetRequiredService<IOrderPaymentService>()
                .PayAsync(orderId, new PayOrderRequest(PaymentMethod.Cash, "R-1"), CancellationToken.None);
            return (paid, correlation);
        });

        return new Paid(response.InvoiceTaskId, response.EmailTaskId, response.PaidAt, correlationId);
    }

    /// <summary>A task of <paramref name="taskType"/> with its message, as a payment creates it - for an order nobody paid.</summary>
    private async Task<ProcessingTask> SeedTaskAsync(ProcessingTaskType taskType, Guid? orderId, string eventType)
    {
        var outboxEventId = Guid.CreateVersion7();
        var task = new ProcessingTask(taskType, orderId, $"seeded-{_tag}", outboxEventId.ToString());
        await fixture.WithDbAsync(db =>
        {
            db.ProcessingTasks.Add(task);
            db.OutboxEvents.Add(new OutboxEvent(
                eventType, task.TaskId.ToString(), $$"""{"taskId":"{{task.TaskId}}"}""", task.CorrelationId, inventoryMovementId: null, outboxEventId));
            return db.SaveChangesAsync();
        });
        return task;
    }

    private IHost StartWorkers(
        string instanceId, PaymentProbe? probe = null, CapturingLoggerProvider? logs = null, IDictionary<string, string?>? settings = null)
    {
        // The Development rules of both simulators (off by default), and enough attempts for the retry tests.
        var overrides = new Dictionary<string, string?>
        {
            ["InvoiceSimulation:Rules"] = "true",
            ["EmailSimulation:Rules"] = "true"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            overrides[key] = value;
        }

        var host = fixture.CreateHost(instanceId, overrides, services =>
        {
            if (probe is not null)
            {
                services.ProbePayments(probe);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
            }
        });
        host.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private async Task RunWorkersAsync(
        string instanceId,
        Func<Task> until,
        PaymentProbe? probe = null,
        CapturingLoggerProvider? logs = null,
        IDictionary<string, string?>? settings = null)
    {
        var host = StartWorkers(instanceId, probe, logs, settings);
        try
        {
            await until();
        }
        finally
        {
            await StopAsync(host);
        }
    }

    private Task<ProcessingTask> TaskAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == taskId));

    private Task<List<ProcessingTask>> TasksOfAsync(Guid orderId) =>
        fixture.WithDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).Where(t => t.OrderId == orderId).ToListAsync());

    private async Task<ProcessingTask> WaitForTaskAsync(Guid taskId, Func<ProcessingTask, bool> done, string what)
    {
        try
        {
            return await Eventually.WaitAsync(() => TaskAsync(taskId), done, Timeout, $"task {taskId}: {what}");
        }
        catch (TimeoutException timeout)
        {
            var task = await TaskAsync(taskId);
            var attempts = string.Join(",", task.Attempts.OrderBy(a => a.AttemptNo).Select(a => a.ErrorCode ?? a.Status.ToString()));
            throw new TimeoutException($"{timeout.Message} Task: {task.Status} [{attempts}]", timeout);
        }
    }

    private Task<ProcessingTask> WaitForStatusAsync(Guid taskId, ProcessingTaskStatus status) =>
        WaitForTaskAsync(taskId, t => t.Status == status, status.ToString());

    private Task<Invoice?> InvoiceAsync(Guid orderId) =>
        fixture.WithDbAsync(db => db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.OrderId == orderId));

    private Task<Order> OrderAsync(Guid orderId) =>
        fixture.WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId));

    private Task<Alert?> AlertOfAsync(Guid taskId) =>
        fixture.WithDbAsync(db => db.Alerts.AsNoTracking()
            .Where(a => a.DedupKey == AlertDedupKey.ProcessingFailed(taskId))
            .OrderByDescending(a => a.LastOccurredAt)
            .FirstOrDefaultAsync());

    private Task<List<SystemEventLog>> ProcessingFailedLogsAsync(params Guid[] taskIds) =>
        fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.EventType == ProcessingEvents.Failed && l.TaskId != null && taskIds.Contains(l.TaskId.Value))
            .ToListAsync());

    /// <summary>Neither worker changes the order: its status, payment, total, audit stamps and stock are the payment's.</summary>
    private async Task AssertOrderUntouchedAsync(Seeded seeded, Paid paid)
    {
        var order = await OrderAsync(seeded.OrderId);
        Assert.Equal(
            (OrderStatus.Reserved, PaymentStatus.Paid, 150_000m, Payer, Payer, (PaymentMethod?)PaymentMethod.Cash),
            (order.Status, order.PaymentStatus, order.TotalAmount, order.PaidBy, order.UpdatedBy, order.PaymentMethod));
        Assert.Equal(paid.PaidAt, order.PaidAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(paid.PaidAt, order.UpdatedAt!.Value, TimeSpan.FromSeconds(5));

        var stock = await fixture.WithDbAsync(db => db.Inventories.AsNoTracking().SingleAsync(i => i.ProductId == seeded.ProductId));
        Assert.Equal((10, 2, 8), (stock.TotalStock, stock.ReservedStock, stock.AvailableStock));
        Assert.Equal(0, await fixture.WithDbAsync(db => db.InventoryMovements.CountAsync(m => m.ProductId == seeded.ProductId)));
    }

    /// <summary>No log line, scope value or exception text of the host carries the address or the text of an e-mail.</summary>
    private static void AssertNothingPersonalLogged(CapturingLoggerProvider logs, string addressPart, string? textBody = null)
    {
        Assert.All(logs.Entries, entry =>
        {
            var written = $"{entry.Message}\n{entry.Exception}\n{string.Join('\n', entry.Scope.Values)}";
            Assert.DoesNotContain(addressPart, written, StringComparison.OrdinalIgnoreCase);
            if (textBody is not null)
            {
                Assert.DoesNotContain(textBody, written, StringComparison.Ordinal);
                // Not a line of it either.
                Assert.DoesNotContain("Xin chào", written, StringComparison.Ordinal);
                Assert.DoesNotContain("Tổng tiền", written, StringComparison.Ordinal);
            }
        });
    }

    private async Task<uint> MessageCountAsync(string queue)
    {
        await using var channel = await fixture.OpenChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    /// <summary>The consumers settle a message right after their database commit; wait until the queues are drained.</summary>
    private async Task WaitForEmptyAsync(params string[] queues)
    {
        foreach (var queue in queues)
        {
            await Eventually.WaitAsync(() => MessageCountAsync(queue), count => count == 0, Timeout, $"{queue} drained");
        }
    }

    /// <summary>The messages of a dead-letter queue, once <paramref name="expected"/> of them are there.</summary>
    private async Task<List<BasicGetResult>> DrainAsync(string queue, int expected)
    {
        await Eventually.WaitAsync(() => MessageCountAsync(queue), count => count >= (uint)expected, Timeout, $"{expected} messages in {queue}");

        await using var channel = await fixture.OpenChannelAsync();
        var messages = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
        {
            messages.Add(message);
        }

        return messages;
    }

    private async Task AssertNoDeadLettersAsync()
    {
        foreach (var queue in new[] { InvoiceQueue, EmailQueue })
        {
            Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(queue)));
        }
    }

    private static byte[] Envelope(string messageId, string eventType, Guid taskId) =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            messageId,
            $"corr-{messageId}",
            taskId,
            eventType,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { taskId }, MessageJson.Options)));
}

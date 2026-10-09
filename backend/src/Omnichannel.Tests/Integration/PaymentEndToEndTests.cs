using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.ExternalProviders;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers.InvoiceWorker;
using Omnichannel.Workers.NotificationWorker;
using static Omnichannel.Tests.Integration.PaymentTesting;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 024, the whole flow of doc 8.4 through the real hosts: order intake → Order Worker reserves → a Manager pays →
/// outbox → Outbox Publisher → RabbitMQ → Invoice Worker and Notification Worker → the order detail shows the invoice.
/// The Workers host runs against the API test database with the simulated providers.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PaymentEndToEndTests(ApiFactory factory, RabbitMqBrokerFixture broker) : IClassFixture<RabbitMqBrokerFixture>
{
    [Fact]
    public async Task An_order_is_reserved_paid_invoiced_and_confirmed_and_its_detail_shows_all_of_it()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var suffix = TestData.UniqueSuffix().ToUpperInvariant();
        var product = await (await manager.PostAsJsonAsync("/api/products", new CreateProductRequest($"PE2E-{suffix}", "Paid end to end", 75_000m, 0, null)))
            .ReadAsync<ProductResponse>();
        var imported = await manager.PostAsJsonAsync(
            "/api/inventory/import", new ImportInventoryRequest($"GRN-{suffix}", null, [new ImportInventoryItem(product.ProductId, 10)]));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var customer = await (await staff.PostAsJsonAsync("/api/customers", new CustomerRequest($"E2E payer {suffix}", null, $"payer.{suffix.ToLowerInvariant()}@example.com", null)))
            .ReadAsync<CustomerResponse>();

        var submitted = await staff.PostAsJsonAsync(
            "/api/orders/website", new SubmitOrderRequest($"PE2E-{suffix}", customer.CustomerId, [new SubmitOrderItem(product.ProductId, 2)]));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var orderTaskId = (await submitted.ReadAsync<SubmitOrderResponse>()).TaskId;

        var probe = new PaymentProbe();
        await using var hints = await HintProbe.StartAsync(factory.DatabaseConnectionString);
        using var workers = await ReportExportTesting.StartWorkersAsync(
            factory, broker, "payment-e2e", configureServices: services => services.ProbePayments(probe));
        try
        {
            // ---- reserved by the Order Worker ----
            var orderTask = await WaitForStatusAsync(factory, orderTaskId, ProcessingTaskStatus.Succeeded);
            var orderId = orderTask.OrderId!.Value;
            var reserved = await DetailAsync(staff, orderId);
            Assert.Equal((OrderStatus.Reserved, PaymentStatus.Unpaid, 150_000m), (reserved.Status, reserved.Payment.Status, reserved.TotalAmount));
            Assert.Null(reserved.Invoice);
            Assert.Equal((10, 2, 8), await StockAsync(factory, product.ProductId));

            // Staff cannot pay; the Manager does.
            Assert.Equal(HttpStatusCode.Forbidden, (await PayAsync(staff, orderId)).StatusCode);
            var correlationId = $"pe2e-{suffix}";
            var paid = await (await PayAsync(manager, orderId, new { paymentMethod = "Cash", paymentReference = "CASH-1" }, correlationId: correlationId))
                .ReadAsync<PayOrderResponse>();

            // ---- both tasks, each by its own worker ----
            var invoiceTask = await WaitForStatusAsync(factory, paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
            var emailTask = await WaitForStatusAsync(factory, paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded);
            Assert.Equal(
                (InvoiceMessageHandler.WorkerName, "payment-e2e", correlationId),
                (Assert.Single(invoiceTask.Attempts).WorkerName, invoiceTask.Attempts.Single().WorkerInstanceId, invoiceTask.CorrelationId));
            Assert.Equal((EmailMessageHandler.WorkerName, correlationId), (Assert.Single(emailTask.Attempts).WorkerName, emailTask.CorrelationId));

            // ---- the order as every role sees it: paid, invoiced, still Reserved, the stock where it was ----
            var detail = await DetailAsync(staff, orderId);
            Assert.Equal(
                (OrderStatus.Reserved, PaymentStatus.Paid, "manager@integration.test", (PaymentMethod?)PaymentMethod.Cash, "CASH-1"),
                (detail.Status, detail.Payment.Status, detail.Payment.PaidByEmail, detail.Payment.Method, detail.Payment.Reference));
            Assert.Equal(SimulatedInvoiceProviderClient.InvoiceNumberOf($"invoice:{orderId}", paid.PaidAt), detail.Invoice!.Number);
            Assert.StartsWith("SIM-", detail.Invoice.Number);
            Assert.InRange(detail.Invoice.IssuedAt, paid.PaidAt.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.Equal(
                [(ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.Succeeded), (ProcessingTaskType.Invoice, ProcessingTaskStatus.Succeeded), (ProcessingTaskType.Email, ProcessingTaskStatus.Succeeded)],
                detail.Tasks.OrderBy(t => t.TaskType).Select(t => (t.TaskType, t.Status)));
            Assert.Equal((10, 2, 8), await StockAsync(factory, product.ProductId));
            var listed = Assert.Single((await staff.GetFromJsonAsync<PagedResult<OrderSummaryResponse>>($"/api/orders?search=PE2E-{suffix}", TestData.Json))!.Items);
            Assert.Equal((PaymentStatus.Paid, OrderStatus.Reserved), (listed.PaymentStatus, listed.Status));

            // ---- what left the system: one invoice request and one e-mail, to the customer's address ----
            Assert.Equal(customer.Name, Assert.Single(probe.InvoiceRequestsOf(orderId)).CustomerName);
            var email = Assert.Single(probe.EmailsOf(orderId));
            Assert.Equal($"payer.{suffix.ToLowerInvariant()}@example.com", email.To);
            Assert.Contains($"PE2E-{suffix}", email.Subject);

            // ---- the trace of the payment: its correlation id finds the tasks, the event and the hints ----
            var tasks = await manager.GetFromJsonAsync<PagedResult<ProcessingTaskSummaryResponse>>(
                $"/api/processing/tasks?correlationId={correlationId}", TestData.Json);
            Assert.Equal([ProcessingTaskType.Invoice, ProcessingTaskType.Email], tasks!.Items.Select(t => t.TaskType).Order());
            var events = await manager.GetStringAsync($"/api/system-events?correlationId={correlationId}");
            Assert.Contains(OrderPaymentEvents.Paid, events, StringComparison.Ordinal);
            Assert.DoesNotContain("CASH-1", events, StringComparison.Ordinal);

            await hints.WaitForTaskAsync(paid.InvoiceTaskId, nameof(ProcessingTaskStatus.Succeeded));
            await hints.WaitForTaskAsync(paid.EmailTaskId.Value, nameof(ProcessingTaskStatus.Succeeded));
            Assert.Contains(hints.Events, e => e.Type == RealtimeEventTypes.OrderChanged && e.OrderId == orderId);

            // A second payment is refused, also after everything ran.
            var again = await PayAsync(manager, orderId);
            Assert.Equal((HttpStatusCode.Conflict, OrderErrorCodes.OrderAlreadyPaid), (again.StatusCode, await again.ReadErrorCodeAsync()));
        }
        finally
        {
            await workers.StopAsync();
        }
    }

    private static async Task<OrderDetailResponse> DetailAsync(HttpClient client, Guid orderId)
    {
        var response = await client.GetAsync($"/api/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<OrderDetailResponse>();
    }
}

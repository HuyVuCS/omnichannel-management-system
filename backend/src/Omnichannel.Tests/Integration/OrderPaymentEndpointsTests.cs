using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Realtime;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using static Omnichannel.Tests.Integration.PaymentTesting;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 024 Part B: <c>POST /api/orders/{orderId}/payment</c> - what one payment writes (in one transaction), who may
/// pay what, and how the order responses show it. No Workers host runs here: the two tasks stay <c>Pending</c>.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderPaymentEndpointsTests(ApiFactory factory)
{
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);
    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    // ---- what a payment writes ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_payment_stores_the_payment_two_tasks_their_messages_and_the_audit_event_and_leaves_status_and_stock_alone()
    {
        var order = await SeedOrderAsync(factory);
        var managerId = factory.UserIdOf(AppRoles.Manager);
        var correlationId = $"pay-{TestData.UniqueSuffix()}";
        var before = DateTimeOffset.UtcNow;
        await using var hints = await HintProbe.StartAsync(factory.DatabaseConnectionString);

        var response = await PayAsync(
            _manager, order.OrderId, new { paymentMethod = "BankTransfer", paymentReference = "  FT26100912345  " }, correlationId: correlationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paid = await response.ReadAsync<PayOrderResponse>();
        Assert.Equal((order.OrderId, PaymentStatus.Paid), (paid.OrderId, paid.PaymentStatus));
        Assert.InRange(paid.PaidAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotNull(paid.EmailTaskId);
        Assert.NotEqual(paid.InvoiceTaskId, paid.EmailTaskId);

        // The order: paid by the caller; its status and total are what they were.
        var stored = await OrderAsync(factory, order.OrderId);
        Assert.Equal(
            (PaymentStatus.Paid, managerId.ToString(), (PaymentMethod?)PaymentMethod.BankTransfer, "FT26100912345", OrderStatus.Reserved, 150_000m),
            (stored.PaymentStatus, stored.PaidBy, stored.PaymentMethod, stored.PaymentReference, stored.Status, stored.TotalAmount));
        Assert.Equal(paid.PaidAt, stored.PaidAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(managerId.ToString(), stored.UpdatedBy);

        // No stock moved.
        Assert.Equal((10, 2, 8), await StockAsync(factory, order.ProductId));
        Assert.Empty(await factory.QueryDbAsync(db => db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == order.ProductId).ToListAsync()));

        // Two independent tasks of the order, each with the key that makes it the only one of its kind.
        var tasks = await TasksAsync(factory, order.OrderId);
        Assert.Equal(2, tasks.Count);
        var invoiceTask = Assert.Single(tasks, t => t.TaskType == ProcessingTaskType.Invoice);
        var emailTask = Assert.Single(tasks, t => t.TaskType == ProcessingTaskType.Email);
        Assert.Equal((paid.InvoiceTaskId, (Guid?)paid.EmailTaskId), (invoiceTask.TaskId, emailTask.TaskId));
        Assert.Equal(($"invoice:{order.OrderId}", $"email:payment:{order.OrderId}"), (invoiceTask.IdempotencyKey, emailTask.IdempotencyKey));
        Assert.All(tasks, t =>
        {
            Assert.Equal((ProcessingTaskStatus.Pending, correlationId, (Guid?)order.OrderId, 0), (t.Status, t.CorrelationId, t.OrderId, t.RetryCount));
            Assert.Empty(t.Attempts);
        });

        // One message per task: it names the task and nothing else, and the task knows its message.
        var outbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().Where(e => e.CorrelationId == correlationId).ToListAsync());
        Assert.Equal(2, outbox.Count);
        foreach (var (task, eventType) in new[] { (invoiceTask, EventTypes.InvoiceRequested), (emailTask, EventTypes.EmailRequested) })
        {
            var message = Assert.Single(outbox, e => e.EventType == eventType);
            Assert.Equal(
                (task.TaskId.ToString(), OutboxEventStatus.Pending, (string?)null, task.MessageId),
                (message.AggregateId, message.Status, message.RoutingKey, message.OutboxEventId.ToString()));
            // Stored as jsonb: compared as JSON, not as text.
            var body = JsonNode.Parse(message.Payload)!.AsObject();
            Assert.Equal(("taskId", task.TaskId), (Assert.Single(body).Key, body["taskId"]!.GetValue<Guid>()));
        }

        // The audit event: who paid how much how - and never the reference.
        var audit = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(l => l.EventType == OrderPaymentEvents.Paid && l.OrderId == order.OrderId));
        Assert.Equal((SystemEventLevel.Information, correlationId, (Guid?)null, (string?)null), (audit.Level, audit.CorrelationId, audit.TaskId, audit.WorkerName));
        var payload = JsonNode.Parse(audit.Payload!)!.AsObject();
        Assert.Equal(["amount", "orderId", "paidBy", "paymentMethod"], payload.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            (order.OrderId.ToString(), "BankTransfer", 150_000m, managerId.ToString()),
            (payload["orderId"]!.GetValue<string>(), payload["paymentMethod"]!.GetValue<string>(), payload["amount"]!.GetValue<decimal>(),
                payload["paidBy"]!.GetValue<string>()));
        Assert.DoesNotContain("FT26100912345", audit.Payload, StringComparison.Ordinal);

        // One notification for the whole payment: the order changed, two tasks appeared.
        var notification = (await hints.WaitForAsync(all => all.Any(n => n.HasTask(paid.InvoiceTaskId)), "hints of the payment"))
            .Single(n => n.HasTask(paid.InvoiceTaskId));
        Assert.Equal(
            [(RealtimeEventTypes.OrderChanged, null, null), (RealtimeEventTypes.ProcessingTaskChanged, "Invoice", "Pending"), (RealtimeEventTypes.ProcessingTaskChanged, "Email", "Pending")],
            notification.Events.Select(e => (e.Type, e.TaskType, e.Status)));
        Assert.All(notification.Events, e => Assert.Equal(order.OrderId, e.OrderId));

        // No invoice yet: that is the Invoice Worker's.
        Assert.Null(await InvoiceAsync(factory, order.OrderId));
    }

    [Fact]
    public async Task A_payment_whose_transaction_fails_stores_nothing_and_can_be_made_again()
    {
        var order = await SeedOrderAsync(factory);
        var correlationId = $"pay-{TestData.UniqueSuffix()}";

        factory.CommitFailures.FailNextCommit();
        var failed = await PayAsync(_manager, order.OrderId, correlationId: correlationId);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var stored = await OrderAsync(factory, order.OrderId);
        Assert.Equal((PaymentStatus.Unpaid, (DateTimeOffset?)null, (string?)null), (stored.PaymentStatus, stored.PaidAt, stored.PaidBy));
        Assert.Empty(await TasksAsync(factory, order.OrderId));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.OutboxEvents.CountAsync(e => e.CorrelationId == correlationId)));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.SystemEventLogs.CountAsync(l => l.OrderId == order.OrderId)));

        // Nothing is left behind that would refuse the same payment.
        var paid = await PaidAsync(_manager, order.OrderId);
        Assert.Equal(2, (await TasksAsync(factory, order.OrderId)).Count);
        Assert.NotNull(paid.EmailTaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_customer_without_an_e_mail_address_gets_an_invoice_task_and_no_e_mail_task(string? email)
    {
        var order = await SeedOrderAsync(factory, email: null);
        if (email is not null)
        {
            // Not through the customer form (it stores a blank address as none): a blank left by something else.
            await factory.QueryDbAsync(db => db.Database.ExecuteSqlAsync(
                $"""UPDATE "Customers" SET "Email" = {email} WHERE "CustomerId" = {order.CustomerId}"""));
        }

        var correlationId = $"pay-{TestData.UniqueSuffix()}";

        var response = await PayAsync(_manager, order.OrderId, correlationId: correlationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The response says so.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("emailTaskId").ValueKind);
        var invoiceTaskId = body.RootElement.GetProperty("invoiceTaskId").GetGuid();

        var task = Assert.Single(await TasksAsync(factory, order.OrderId));
        Assert.Equal((invoiceTaskId, ProcessingTaskType.Invoice), (task.TaskId, task.TaskType));
        var message = Assert.Single(await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().Where(e => e.CorrelationId == correlationId).ToListAsync()));
        Assert.Equal(EventTypes.InvoiceRequested, message.EventType);
        Assert.Equal(PaymentStatus.Paid, (await OrderAsync(factory, order.OrderId)).PaymentStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_reference_is_stored_as_none(string? reference)
    {
        var order = await SeedOrderAsync(factory);

        await PaidAsync(_manager, order.OrderId, new { paymentMethod = "Cash", paymentReference = reference });

        var stored = await OrderAsync(factory, order.OrderId);
        Assert.Equal(((PaymentMethod?)PaymentMethod.Cash, (string?)null), (stored.PaymentMethod, stored.PaymentReference));
    }

    // ---- who may pay what --------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_is_forbidden_an_anonymous_caller_unauthorized_and_an_admin_may_pay()
    {
        var order = await SeedOrderAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await PayAsync(_staff, order.OrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PayAsync(factory.CreateClient(), order.OrderId)).StatusCode);
        Assert.Equal(PaymentStatus.Unpaid, (await OrderAsync(factory, order.OrderId)).PaymentStatus);
        Assert.Empty(await TasksAsync(factory, order.OrderId));

        await PaidAsync(_admin, order.OrderId);
        Assert.Equal(factory.UserIdOf(AppRoles.Admin).ToString(), (await OrderAsync(factory, order.OrderId)).PaidBy);
    }

    [Fact]
    public async Task An_unknown_order_is_not_found()
    {
        var response = await PayAsync(_manager, Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.NotFound, OrderErrorCodes.OrderNotFound), (response.StatusCode, await response.ReadErrorCodeAsync()));
    }

    [Theory]
    [InlineData(OrderStatus.InsufficientStock)]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Completed)]
    public async Task An_order_that_is_not_reserved_cannot_be_paid_and_nothing_is_written(OrderStatus status)
    {
        var order = await SeedOrderAsync(factory, status);

        var response = await PayAsync(_manager, order.OrderId);

        Assert.Equal((HttpStatusCode.Conflict, OrderErrorCodes.OrderNotPayable), (response.StatusCode, await response.ReadErrorCodeAsync()));
        var stored = await OrderAsync(factory, order.OrderId);
        Assert.Equal((PaymentStatus.Unpaid, status, (string?)null), (stored.PaymentStatus, stored.Status, stored.UpdatedBy));
        Assert.Empty(await TasksAsync(factory, order.OrderId));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.SystemEventLogs.CountAsync(l => l.OrderId == order.OrderId)));
    }

    [Fact]
    public async Task A_second_payment_is_refused_and_the_first_stays_as_it_was()
    {
        var order = await SeedOrderAsync(factory);
        var first = await PaidAsync(_manager, order.OrderId, new { paymentMethod = "Cash", paymentReference = "R-1" });

        var second = await PayAsync(_admin, order.OrderId, new { paymentMethod = "Card", paymentReference = "R-2" });

        Assert.Equal((HttpStatusCode.Conflict, OrderErrorCodes.OrderAlreadyPaid), (second.StatusCode, await second.ReadErrorCodeAsync()));
        var stored = await OrderAsync(factory, order.OrderId);
        Assert.Equal(
            ((PaymentMethod?)PaymentMethod.Cash, "R-1", factory.UserIdOf(AppRoles.Manager).ToString()),
            (stored.PaymentMethod, stored.PaymentReference, stored.PaidBy));
        Assert.Equal(first.PaidAt, stored.PaidAt!.Value, TimeSpan.FromMilliseconds(1));
        // Still one invoice task, one e-mail task, one audit event.
        Assert.Equal(
            [ProcessingTaskType.Invoice, ProcessingTaskType.Email],
            (await TasksAsync(factory, order.OrderId)).Select(t => t.TaskType).Order());
        Assert.Equal(1, await factory.QueryDbAsync(db => db.SystemEventLogs.CountAsync(l => l.OrderId == order.OrderId && l.EventType == OrderPaymentEvents.Paid)));
    }

    [Fact]
    public async Task Of_several_payments_at_once_one_wins_and_the_order_gets_one_invoice_task()
    {
        var order = await SeedOrderAsync(factory);
        var clients = Enumerable.Range(0, 8).Select(n => n % 2 == 0 ? _manager : _admin).ToList();

        var responses = await Task.WhenAll(clients.Select((client, n) => PayAsync(
            client, order.OrderId, new { paymentMethod = n % 2 == 0 ? "Cash" : "Card", paymentReference = $"R-{n}" })));

        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var losers = responses.Where(r => r != winner).ToList();
        Assert.All(losers, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var loser in losers)
        {
            Assert.Equal(OrderErrorCodes.OrderAlreadyPaid, await loser.ReadErrorCodeAsync());
        }

        var paid = await winner.ReadAsync<PayOrderResponse>();
        var tasks = await TasksAsync(factory, order.OrderId);
        Assert.Equal(2, tasks.Count);
        Assert.Equal(paid.InvoiceTaskId, Assert.Single(tasks, t => t.TaskType == ProcessingTaskType.Invoice).TaskId);
        Assert.Equal(paid.EmailTaskId, Assert.Single(tasks, t => t.TaskType == ProcessingTaskType.Email).TaskId);
        Assert.Equal(1, await factory.QueryDbAsync(db => db.SystemEventLogs.CountAsync(l => l.OrderId == order.OrderId && l.EventType == OrderPaymentEvents.Paid)));
        // The stored payment is the winner's, whole: no field of another request.
        var stored = await OrderAsync(factory, order.OrderId);
        var winnerIndex = Array.IndexOf(responses, winner);
        Assert.Equal(
            ((PaymentMethod?)(winnerIndex % 2 == 0 ? PaymentMethod.Cash : PaymentMethod.Card), $"R-{winnerIndex}"),
            (stored.PaymentMethod, stored.PaymentReference));
    }

    // ---- validation and messages -------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string> InvalidBodies() => new()
    {
        { """{}""", "paymentMethod", "en", "'Payment method' must not be empty." },
        { """{"paymentMethod":null}""", "paymentMethod", "vi", "'Phương thức thanh toán' phải có giá trị." },
        { """{"paymentMethod":7}""", "paymentMethod", "en", "'Payment method' has a range of values which does not include '7'." },
        { $$"""{"paymentMethod":"Cash","paymentReference":"{{new string('x', 101)}}"}""", "paymentReference", "en", "'Payment reference' must be at most 100 characters." },
        { $$"""{"paymentMethod":"Cash","paymentReference":"{{new string('x', 101)}}"}""", "paymentReference", "vi", "'Mã tham chiếu thanh toán' chỉ được dài tối đa 100 ký tự." }
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_is_refused_on_its_field_in_the_language_of_the_request(string json, string key, string language, string message)
    {
        var order = await SeedOrderAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.OrderId}/payment")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.AcceptLanguage.ParseAdd(language);

        var response = await _manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal([key], errors.EnumerateObject().Select(p => p.Name));
        Assert.Equal(message, errors.GetProperty(key)[0].GetString());
        Assert.Equal(PaymentStatus.Unpaid, (await OrderAsync(factory, order.OrderId)).PaymentStatus);
    }

    [Fact]
    public async Task A_method_the_system_does_not_know_is_a_400_and_nothing_is_paid()
    {
        var order = await SeedOrderAsync(factory);

        var response = await PayAsync(_manager, order.OrderId, new { paymentMethod = "Bitcoin" });

        Assert.Equal((HttpStatusCode.BadRequest, "validation_failed"), (response.StatusCode, await response.ReadErrorCodeAsync()));
        Assert.Equal(PaymentStatus.Unpaid, (await OrderAsync(factory, order.OrderId)).PaymentStatus);
    }

    // The keys are part of the contract (the payment dialog puts an error on the field its key names): a text that is no
    // payment method fails in the JSON binder, before validation. Since task 026 (B3) that answer has the field's own
    // key and a generic message in the language of the request - not the binder's path, its English text with a .NET
    // type name and a second error for the action parameter.
    [Theory]
    [InlineData("en", "'Payment method' has a value of the wrong type or format.")]
    [InlineData("vi", "'Phương thức thanh toán' có giá trị không đúng kiểu hoặc định dạng.")]
    public async Task A_method_text_the_system_does_not_know_is_refused_under_the_key_of_its_field(string language, string message)
    {
        var order = await SeedOrderAsync(factory);

        var response = await PayAsync(_manager, order.OrderId, new { paymentMethod = "Cheque" }, language: language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal(["paymentMethod"], errors.EnumerateObject().Select(p => p.Name));
        Assert.Equal([message], errors.GetProperty("paymentMethod").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(PaymentStatus.Unpaid, (await OrderAsync(factory, order.OrderId)).PaymentStatus);
    }

    [Theory]
    [InlineData("en", "Only an order whose stock is reserved can be paid.", "This order has already been paid.")]
    [InlineData("vi", "Chỉ có thể thanh toán đơn hàng đã giữ hàng.", "Đơn hàng này đã được thanh toán.")]
    public async Task The_refusals_of_a_payment_are_in_the_language_of_the_request(string language, string notPayable, string alreadyPaid)
    {
        var insufficient = await SeedOrderAsync(factory, OrderStatus.InsufficientStock);
        var reserved = await SeedOrderAsync(factory);
        await PaidAsync(_manager, reserved.OrderId);

        Assert.Equal(notPayable, await DetailOfAsync(await PayAsync(_manager, insufficient.OrderId, language: language)));
        Assert.Equal(alreadyPaid, await DetailOfAsync(await PayAsync(_manager, reserved.OrderId, language: language)));

        static async Task<string?> DetailOfAsync(HttpResponseMessage response)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("detail").GetString();
        }
    }

    // ---- the order responses -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_order_detail_shows_the_payment_with_who_recorded_it_and_the_invoice_once_it_is_stored()
    {
        var order = await SeedOrderAsync(factory);

        var unpaid = await DetailAsync(_staff, order.OrderId);
        Assert.Equal(new OrderPaymentResponse(PaymentStatus.Unpaid, null, null, null, null), unpaid.Payment);
        Assert.Null(unpaid.Invoice);
        // The JSON says so too: an object with nulls, and an invoice that is null.
        using (var raw = JsonDocument.Parse(await _staff.GetStringAsync($"/api/orders/{order.OrderId}")))
        {
            Assert.Equal("Unpaid", raw.RootElement.GetProperty("payment").GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("payment").GetProperty("paidByEmail").ValueKind);
            Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("invoice").ValueKind);
        }

        var paid = await PaidAsync(_manager, order.OrderId, new { paymentMethod = "EWallet", paymentReference = "MOMO-77" });

        // Every role that reads orders sees the payment, its reference included.
        var detail = await DetailAsync(_staff, order.OrderId);
        Assert.Equal(
            (PaymentStatus.Paid, "manager@integration.test", (PaymentMethod?)PaymentMethod.EWallet, "MOMO-77"),
            (detail.Payment.Status, detail.Payment.PaidByEmail, detail.Payment.Method, detail.Payment.Reference));
        Assert.Equal(paid.PaidAt, detail.Payment.PaidAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(detail.Invoice);
        Assert.Equal(OrderStatus.Reserved, detail.Status);
        // The two tasks of the payment are tasks of the order.
        Assert.Equal(
            [ProcessingTaskType.Invoice, ProcessingTaskType.Email],
            detail.Tasks.Select(t => t.TaskType).Order());

        // The invoice, as the Invoice Worker stores it.
        var issuedAt = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
        await factory.QueryDbAsync(db =>
        {
            db.Invoices.Add(new Invoice(order.OrderId, $"SIM-202610-{TestData.UniqueSuffix()[..8].ToUpperInvariant()}", issuedAt, 150_000m, Invoice.ProviderKeyOf(order.OrderId)));
            return db.SaveChangesAsync();
        });

        var invoiced = await DetailAsync(_staff, order.OrderId);
        Assert.StartsWith("SIM-202610-", invoiced.Invoice!.Number);
        Assert.Equal(issuedAt, invoiced.Invoice.IssuedAt);
        // The lines are still there, once each (the joins of payment and invoice multiply nothing).
        Assert.Equal((1, order.Sku), (invoiced.Items.Count, invoiced.Items[0].Sku));
        Assert.Equal(2, invoiced.Tasks.Count);
    }

    [Fact]
    public async Task A_payment_recorded_by_a_user_that_is_gone_has_no_e_mail_address()
    {
        var order = await SeedOrderAsync(factory);
        await PaidAsync(_manager, order.OrderId);
        await factory.QueryDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "Orders" SET "PaidBy" = {Guid.NewGuid().ToString()} WHERE "OrderId" = {order.OrderId}"""));

        var detail = await DetailAsync(_staff, order.OrderId);

        Assert.Equal((PaymentStatus.Paid, (string?)null), (detail.Payment.Status, detail.Payment.PaidByEmail));
    }

    [Fact]
    public async Task The_order_list_shows_the_payment_status_of_each_order()
    {
        var paidOrder = await SeedOrderAsync(factory);
        var unpaidOrder = await SeedOrderAsync(factory);
        var rejected = await SeedOrderAsync(factory, OrderStatus.InsufficientStock);
        await PaidAsync(_manager, paidOrder.OrderId);

        async Task<OrderSummaryResponse> RowAsync(SeededOrder order) =>
            Assert.Single((await _staff.GetFromJsonAsync<PagedResult<OrderSummaryResponse>>(
                $"/api/orders?search={order.ExternalOrderId}", TestData.Json))!.Items);

        Assert.Equal((PaymentStatus.Paid, OrderStatus.Reserved), ((await RowAsync(paidOrder)).PaymentStatus, (await RowAsync(paidOrder)).Status));
        Assert.Equal(PaymentStatus.Unpaid, (await RowAsync(unpaidOrder)).PaymentStatus);
        Assert.Equal(PaymentStatus.Unpaid, (await RowAsync(rejected)).PaymentStatus);
        using var raw = JsonDocument.Parse(await _staff.GetStringAsync($"/api/orders?search={paidOrder.ExternalOrderId}"));
        Assert.Equal("Paid", raw.RootElement.GetProperty("items")[0].GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task The_payment_event_is_on_the_logs_api_with_its_allowed_fields_and_the_payer()
    {
        var order = await SeedOrderAsync(factory);
        await PaidAsync(_manager, order.OrderId, new { paymentMethod = "Card", paymentReference = "CARD-REF-4242" });

        var body = await _manager.GetStringAsync($"/api/system-events?eventType={OrderPaymentEvents.Paid}&orderId={order.OrderId}");
        var item = Assert.Single(JsonSerializer.Deserialize<PagedResult<SystemEventResponse>>(body, TestData.Json)!.Items);

        Assert.Equal((OrderPaymentEvents.Paid, SystemEventLevel.Information, (Guid?)order.OrderId), (item.EventType, item.Level, item.OrderId));
        Assert.Equal(["orderId", "paymentMethod", "amount", "paidBy"], item.Details!.Select(p => p.Key));
        Assert.Equal("Card", item.Details!["paymentMethod"]!.GetValue<string>());
        Assert.Equal("manager@integration.test", item.ActorEmail);
        Assert.DoesNotContain("CARD-REF-4242", body, StringComparison.Ordinal);
    }

    private static async Task<OrderDetailResponse> DetailAsync(HttpClient client, Guid orderId)
    {
        var response = await client.GetAsync($"/api/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<OrderDetailResponse>();
    }
}

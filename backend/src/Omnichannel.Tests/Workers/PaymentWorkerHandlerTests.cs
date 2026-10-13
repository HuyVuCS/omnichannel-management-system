using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Tests.Integration.Workers;
using Omnichannel.Workers;
using Omnichannel.Workers.InvoiceWorker;
using Omnichannel.Workers.NotificationWorker;
using Omnichannel.Workers.Processing;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Workers;

/// <summary>
/// Task 024 C3 / D2: what the Invoice Worker and the Notification Worker do with one delivery, step by step, against
/// doubles of the database side and of the provider (the real ones are in the integration tests).
/// </summary>
public sealed class PaymentWorkerHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset PaidAt = new(2026, 10, 9, 8, 30, 0, TimeSpan.Zero);

    // ---- invoice: the normal delivery --------------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_is_issued_with_the_key_of_its_order_and_stored_with_the_total_of_the_order()
    {
        var invoices = new FakeInvoices();
        var provider = new FakeProvider(invoices.Calls);

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        // The attempt exists before anything is read; the provider is called between the read and the completion.
        Assert.Equal(["get", "start", "read", "issue", "complete"], invoices.Calls);
        var request = Assert.Single(provider.Requests);
        Assert.Equal(
            ($"invoice:{OrderId}", OrderId, "WEB-1001", "Nguyen Van A", 211_501.5m, PaidAt, 1),
            (request.Key, request.OrderId, request.ExternalOrderId, request.CustomerName, request.Total, request.PaidAt, request.AttemptNo));
        Assert.Equal([("TEA-1", "Green tea", 2, 75_000m), ("TEA-2", "Oolong", 3, 20_500.5m)], request.Lines.Select(l => (l.Sku, l.Name, l.Quantity, l.UnitPrice)));

        var (taskId, attemptId, stored) = Assert.Single(invoices.Completed);
        Assert.Equal((TaskId, invoices.Attempt!.AttemptId), (taskId, attemptId));
        Assert.Equal(
            (OrderId, "INV-0001", provider.IssuedAt, 211_501.5m, $"invoice:{OrderId}"),
            (stored!.OrderId, stored.InvoiceNumber, stored.IssuedAt, stored.TotalAmount, stored.ProviderReference));
    }

    [Fact]
    public async Task An_order_that_has_its_invoice_completes_the_task_without_a_call_to_the_provider()
    {
        var invoices = new FakeInvoices { Order = InvoiceOrder(hasInvoice: true) };
        var provider = new FakeProvider(invoices.Calls);

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get", "start", "read", "complete"], invoices.Calls);
        Assert.Empty(provider.Requests);
        // Nothing to store: the invoice is there.
        Assert.Equal((TaskId, invoices.Attempt!.AttemptId, (Invoice?)null), Assert.Single(invoices.Completed));
    }

    [Fact]
    public async Task The_attempt_number_of_a_later_attempt_reaches_the_provider()
    {
        var invoices = new FakeInvoices { Attempt = new AttemptStart(Guid.NewGuid(), 3, 0) };
        var provider = new FakeProvider(invoices.Calls);

        await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(3, Assert.Single(provider.Requests).AttemptNo);
    }

    // ---- invoice: failures -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PaymentStatus.Unpaid)]
    // Paid without a payment time cannot exist (check constraint); it is refused all the same.
    [InlineData(PaymentStatus.Paid)]
    public async Task An_order_that_is_not_paid_fails_the_invoice_for_good_without_a_call(PaymentStatus status)
    {
        var invoices = new FakeInvoices { Order = InvoiceOrder() with { PaymentStatus = status, PaidAt = null } };
        var provider = new FakeProvider(invoices.Calls);

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        var failure = Assert.IsType<HandlerResult.Fail>(result);
        Assert.Equal((1, InvoiceErrorCodes.OrderNotPaid), (failure.AttemptNo, failure.ErrorCode));
        Assert.Equal(["get", "start", "read"], invoices.Calls);
        Assert.Empty(provider.Requests);
        Assert.Empty(invoices.Completed);
        Assert.Empty(invoices.TransientFailures);
    }

    [Fact]
    public async Task A_stored_invoice_wins_over_an_unpaid_order()
    {
        // The order of the checks: what is stored is never asked for again.
        var invoices = new FakeInvoices { Order = InvoiceOrder(hasInvoice: true) with { PaymentStatus = PaymentStatus.Unpaid, PaidAt = null } };

        var result = await InvoiceHandler(invoices, new FakeProvider(invoices.Calls)).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Single(invoices.Completed);
    }

    [Theory]
    [InlineData(InvoiceErrorCodes.ProviderUnavailable)]
    [InlineData(InvoiceErrorCodes.ProviderTimeout)]
    [InlineData(InvoiceErrorCodes.ProviderCircuitOpen)]
    public async Task A_temporary_failure_of_the_provider_is_recorded_on_the_attempt_and_retried(string errorCode)
    {
        var invoices = new FakeInvoices { Attempt = new AttemptStart(Guid.NewGuid(), 4, 3) };
        var provider = new FakeProvider(invoices.Calls) { Failure = new InvoiceProviderException(errorCode, "provider down", isTransient: true) };
        var logs = new CapturingLoggerProvider();

        var result = await InvoiceHandler(invoices, provider, logs: logs).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        // The attempt number and the budget of the task go back to the consumer: it decides between a retry and the end.
        var retry = Assert.IsType<HandlerResult.Retry>(result);
        Assert.Equal((4, errorCode, 3, 1), (retry.AttemptNo, retry.ErrorCode, retry.AttemptBudgetStart, retry.AttemptInBudget));
        Assert.Equal(["get", "start", "read", "issue", "transient"], invoices.Calls);
        Assert.Equal((TaskId, invoices.Attempt.AttemptId, errorCode, "provider down"), Assert.Single(invoices.TransientFailures));
        Assert.Empty(invoices.Completed);
        // An answer of the provider is a warning without a stack.
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Contains(errorCode, warning.Message);
    }

    [Fact]
    public async Task A_refusal_by_the_provider_fails_the_invoice_for_good_without_a_retry()
    {
        var invoices = new FakeInvoices();
        var provider = new FakeProvider(invoices.Calls)
        {
            Failure = new InvoiceProviderException(InvoiceErrorCodes.ProviderRejected, "refused", isTransient: false)
        };

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(new HandlerResult.Fail(1, InvoiceErrorCodes.ProviderRejected, "refused"), result);
        Assert.Equal(["get", "start", "read", "issue"], invoices.Calls);
        Assert.Empty(invoices.TransientFailures);
        Assert.Empty(invoices.Completed);
    }

    [Fact]
    public async Task A_failure_of_the_completion_after_the_call_is_retried_and_a_permanent_database_error_is_not()
    {
        var temporary = new FakeInvoices { CompleteFailure = new TimeoutException("slow") };
        var permanent = new FakeInvoices
        {
            CompleteFailure = new PostgresException("violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)
        };
        var logs = new CapturingLoggerProvider();

        var retried = await InvoiceHandler(temporary, new FakeProvider(temporary.Calls), logs: logs).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);
        var failed = await InvoiceHandler(permanent, new FakeProvider(permanent.Calls)).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal("timeout", Assert.IsType<HandlerResult.Retry>(retried).ErrorCode);
        Assert.Equal("timeout", Assert.Single(temporary.TransientFailures).ErrorCode);
        Assert.Equal("check_violation", Assert.IsType<HandlerResult.Fail>(failed).ErrorCode);
        Assert.Empty(permanent.TransientFailures);
        // Not an answer of the provider: the stack is logged.
        Assert.IsType<TimeoutException>(Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning).Exception);
    }

    [Fact]
    public async Task A_failure_that_cannot_be_recorded_is_still_retried()
    {
        var invoices = new FakeInvoices { RecordFailure = new TimeoutException("database down") };
        var provider = new FakeProvider(invoices.Calls) { Failure = new InvoiceProviderException(InvoiceErrorCodes.ProviderUnavailable, "down", true) };

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.IsType<HandlerResult.Retry>(result);
    }

    // ---- e-mail ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_confirmation_goes_to_the_customers_current_address_with_the_key_of_the_order()
    {
        var emails = new FakeEmails();
        var sender = new FakeSender(emails.Calls);
        var logs = new CapturingLoggerProvider();

        var result = await EmailHandler(emails, sender, logs: logs).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get", "start", "read", "send", "complete"], emails.Calls);
        var message = Assert.Single(sender.Messages);
        // Trimmed, and built from the settings: Vietnamese, the time of Ho Chi Minh City.
        Assert.Equal(($"email:payment:{OrderId}", "anna.nguyen@example.com", 1), (message.Key, message.To, message.AttemptNo));
        Assert.Equal("Đã nhận thanh toán cho đơn hàng WEB-1001", message.Subject);
        Assert.Contains("09/10/2026 15:30 (Asia/Ho_Chi_Minh)", message.TextBody);
        Assert.Contains("TEA-1", message.TextBody);
        Assert.Equal((TaskId, emails.Attempt!.AttemptId), Assert.Single(emails.Completed));

        // The log has the masked address and nothing of the text.
        Assert.All(logs.Entries, e =>
        {
            Assert.DoesNotContain("anna.nguyen", e.Message);
            Assert.DoesNotContain("Xin chào", e.Message);
        });
        Assert.Contains(logs.Entries, e => e.Message.Contains("a***@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_language_and_the_time_zone_of_an_e_mail_are_the_settings()
    {
        var emails = new FakeEmails();
        var sender = new FakeSender(emails.Calls);

        await EmailHandler(emails, sender, new NotificationsOptions { Language = "en", TimeZone = "Europe/Berlin" })
            .HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        var message = Assert.Single(sender.Messages);
        Assert.Equal("Payment received for order WEB-1001", message.Subject);
        Assert.Contains("Paid at: 2026-10-09 10:30 (Europe/Berlin)", message.TextBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_customer_without_an_address_fails_the_e_mail_for_good_and_nothing_is_sent(string? address)
    {
        var emails = new FakeEmails { Order = Notice(email: address) };
        var sender = new FakeSender(emails.Calls);

        var result = await EmailHandler(emails, sender).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        var failure = Assert.IsType<HandlerResult.Fail>(result);
        Assert.Equal((1, EmailErrorCodes.NoRecipient), (failure.AttemptNo, failure.ErrorCode));
        Assert.Equal(["get", "start", "read"], emails.Calls);
        Assert.Empty(sender.Messages);
        Assert.Empty(emails.Completed);
    }

    [Fact]
    public async Task An_order_that_is_not_paid_fails_the_e_mail_for_good_and_nothing_is_sent()
    {
        var emails = new FakeEmails { Order = Notice() with { PaymentStatus = PaymentStatus.Unpaid, PaidAt = null } };
        var sender = new FakeSender(emails.Calls);

        var result = await EmailHandler(emails, sender).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        Assert.Equal(EmailErrorCodes.OrderNotPaid, Assert.IsType<HandlerResult.Fail>(result).ErrorCode);
        Assert.Empty(sender.Messages);
    }

    [Fact]
    public async Task A_refused_e_mail_fails_for_good_and_a_temporary_failure_is_retried()
    {
        var refused = new FakeEmails();
        var down = new FakeEmails { Attempt = new AttemptStart(Guid.NewGuid(), 2, 0) };

        var failed = await EmailHandler(refused, new FakeSender(refused.Calls) { Failure = new EmailSendException(EmailErrorCodes.Rejected, "refused", false) })
            .HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);
        var retried = await EmailHandler(down, new FakeSender(down.Calls) { Failure = new EmailSendException(EmailErrorCodes.Unavailable, "down", true) })
            .HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        Assert.Equal(new HandlerResult.Fail(1, EmailErrorCodes.Rejected, "refused"), failed);
        Assert.Empty(refused.TransientFailures);
        Assert.Empty(refused.Completed);
        Assert.Equal((2, EmailErrorCodes.Unavailable), (Assert.IsType<HandlerResult.Retry>(retried).AttemptNo, ((HandlerResult.Retry)retried).ErrorCode));
        Assert.Equal((TaskId, down.Attempt.AttemptId, EmailErrorCodes.Unavailable, "down"), Assert.Single(down.TransientFailures));
        Assert.Equal(["get", "start", "read", "send", "transient"], down.Calls);
    }

    // ---- both: deliveries --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProcessingTaskStatus.Succeeded)]
    [InlineData(ProcessingTaskStatus.Failed)]
    public async Task A_delivery_for_a_finished_task_is_acknowledged_without_an_attempt(ProcessingTaskStatus status)
    {
        var invoices = new FakeInvoices { TaskState = State(ProcessingTaskType.Invoice, status) };
        var emails = new FakeEmails { TaskState = State(ProcessingTaskType.Email, status) };

        Assert.Equal(HandlerResult.Ack, await InvoiceHandler(invoices, new FakeProvider(invoices.Calls)).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None));
        Assert.Equal(HandlerResult.Ack, await EmailHandler(emails, new FakeSender(emails.Calls)).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None));

        Assert.Equal(["get"], invoices.Calls);
        Assert.Equal(["get"], emails.Calls);
    }

    [Fact]
    public async Task A_task_completed_by_another_delivery_meanwhile_is_acknowledged_without_work()
    {
        var invoices = new FakeInvoices { Attempt = null };
        var provider = new FakeProvider(invoices.Calls);

        var result = await InvoiceHandler(invoices, provider).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Equal(["get", "start"], invoices.Calls);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task The_worker_acts_as_its_own_system_actor_with_the_correlation_id_of_the_message()
    {
        var user = new WorkerCurrentUser();
        var correlation = new WorkerCorrelationContext();
        var invoices = new FakeInvoices();
        var handler = new InvoiceMessageHandler(
            invoices, new FakeProvider(invoices.Calls), user, correlation, Options.Create(new WorkerOptions { InstanceId = "i-7" }),
            new ReportExportHandlerTests.FakeLifetime(), new Logger<InvoiceMessageHandler>(new LoggerFactory()));

        await handler.HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal((InvoiceMessageHandler.Actor, "corr-1"), (user.UserId, correlation.CorrelationId));
        Assert.Equal((InvoiceMessageHandler.WorkerName, "i-7"), invoices.Started);
        Assert.Equal(("system:invoice-worker", "system:notification-worker"), (InvoiceMessageHandler.Actor, EmailMessageHandler.Actor));
        Assert.Equal(("InvoiceWorker", "NotificationWorker"), (InvoiceMessageHandler.WorkerName, EmailMessageHandler.WorkerName));
    }

    // ---- both: shutdown ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delivery_that_arrives_while_the_host_stops_is_left_alone()
    {
        var invoices = new FakeInvoices();
        var lifetime = new ReportExportHandlerTests.FakeLifetime();
        lifetime.StopApplication();

        var result = await InvoiceHandler(invoices, new FakeProvider(invoices.Calls), lifetime).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Abandon, result);
        Assert.Empty(invoices.Calls);
    }

    [Fact]
    public async Task A_call_that_is_cancelled_because_the_host_stops_abandons_the_delivery_without_recording_a_failure()
    {
        var lifetime = new ReportExportHandlerTests.FakeLifetime();
        var invoices = new FakeInvoices();
        var emails = new FakeEmails();
        // The calls to the provider run on the host's stopping token: they see the stop and give up.
        var provider = new FakeProvider(invoices.Calls) { OnCall = lifetime.StopApplication };
        // A cancellation while the host keeps running comes from somewhere else.
        var sender = new FakeSender(emails.Calls) { Failure = new OperationCanceledException("cancelled, but not by the host") };

        var invoice = await InvoiceHandler(invoices, provider, lifetime).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);
        var email = await EmailHandler(emails, sender).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Abandon, invoice);
        Assert.Empty(invoices.TransientFailures);
        Assert.Empty(invoices.Completed);
        // A host that is not stopping: the same cancellation is a failure like any other.
        Assert.IsType<HandlerResult.Retry>(email);
    }

    [Fact]
    public async Task Only_the_call_to_the_provider_is_tied_to_the_host_stopping()
    {
        // The host stops right after the provider answered: the completion still runs and the message is acknowledged.
        var lifetime = new ReportExportHandlerTests.FakeLifetime();
        var invoices = new FakeInvoices();
        var provider = new FakeProvider(invoices.Calls) { AfterCall = lifetime.StopApplication };

        var result = await InvoiceHandler(invoices, provider, lifetime).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(HandlerResult.Ack, result);
        Assert.Single(invoices.Completed);
    }

    // ---- both: poison ------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[], string?> Rejections() => new()
    {
        { MessageRejectionCodes.InvalidJson, Encoding.UTF8.GetBytes("{ nope"), null },
        { MessageRejectionCodes.EmptyBody, Encoding.UTF8.GetBytes("null"), null },
        { MessageRejectionCodes.UnexpectedEventType, Envelope(EventTypes.OrderCreated, TaskId, new { taskId = TaskId }), null },
        { MessageRejectionCodes.MissingTaskId, Envelope(null, null, new { taskId = TaskId }), null },
        { MessageRejectionCodes.MissingTaskId, Envelope(null, Guid.Empty, new { taskId = Guid.Empty }), null },
        { MessageRejectionCodes.MissingMessageIds, Envelope(null, TaskId, new { taskId = TaskId }, messageId: " "), null },
        { MessageRejectionCodes.MissingMessageIds, Envelope(null, TaskId, new { taskId = TaskId }, correlationId: ""), null },
        { MessageRejectionCodes.PayloadNotObject, Envelope(null, TaskId, new[] { 1, 2 }), null },
        { MessageRejectionCodes.InvalidPayload, Envelope(null, TaskId, new { taskId = "x" }), null },
        { MessageRejectionCodes.TaskIdMismatch, Envelope(null, TaskId, new { taskId = Guid.NewGuid() }), null },
        // Well-formed, but against the database: no such task, a task of another kind, a task without an order.
        { MessageRejectionCodes.UnknownTask, Envelope(null, TaskId, new { taskId = TaskId }), "unknown" },
        { MessageRejectionCodes.TaskMismatch, Envelope(null, TaskId, new { taskId = TaskId }), "other-type" },
        { MessageRejectionCodes.TaskMismatch, Envelope(null, TaskId, new { taskId = TaskId }), "no-order" }
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task A_message_that_can_never_be_processed_is_rejected_with_its_code_by_both_workers_and_both_call_sites(
        string code, byte[] body, string? taskCase)
    {
        foreach (var (eventType, taskType, otherType) in new[]
                 {
                     (EventTypes.InvoiceRequested, ProcessingTaskType.Invoice, ProcessingTaskType.Email),
                     (EventTypes.EmailRequested, ProcessingTaskType.Email, ProcessingTaskType.Invoice)
                 })
        {
            // The bodies of the theory carry a placeholder for "the event type of the queue".
            var message = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace(QueueEventType, eventType, StringComparison.Ordinal));
            TaskRequestState? state = taskCase switch
            {
                "unknown" => null,
                "other-type" => State(otherType),
                "no-order" => State(taskType) with { OrderId = null },
                _ => State(taskType)
            };

            foreach (var lastDelivery in new[] { false, true })
            {
                var (handler, rejections, calls) = taskType == ProcessingTaskType.Invoice
                    ? Rejecting(new FakeInvoices { TaskState = state })
                    : Rejecting(new FakeEmails { TaskState = state });

                if (lastDelivery)
                {
                    // The delivery-limit guard: no HandleAsync before, the task is identified from the message.
                    var toDlq = await handler.RecordFinalFailureAsync(
                        Delivery(eventType, message), new FinalFailure(null, "delivery_limit_reached", "crash loop", "queue"), CancellationToken.None);
                    Assert.True(toDlq);
                }
                else
                {
                    Assert.IsType<HandlerResult.Reject>(await handler.HandleAsync(Delivery(eventType, message), CancellationToken.None));
                }

                Assert.Equal(code, Assert.Single(rejections).Code);
                Assert.DoesNotContain("start", calls);
            }
        }
    }

    [Fact]
    public void Every_code_of_these_workers_is_a_known_rejection_code()
    {
        Assert.All(Rejections().Select(row => (string)row[0]), code => Assert.Contains(code, MessageRejectionCodes.All));
    }

    [Fact]
    public async Task The_message_of_one_worker_is_poison_on_the_queue_of_the_other()
    {
        var invoices = new FakeInvoices();
        var emails = new FakeEmails();

        await InvoiceHandler(invoices, new FakeProvider(invoices.Calls)).HandleAsync(Delivery(EventTypes.EmailRequested), CancellationToken.None);
        await EmailHandler(emails, new FakeSender(emails.Calls)).HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        Assert.Equal(MessageRejectionCodes.UnexpectedEventType, Assert.Single(invoices.Rejections).Code);
        Assert.Equal(MessageRejectionCodes.UnexpectedEventType, Assert.Single(emails.Rejections).Code);
    }

    // ---- both: the last delivery the queue allows ---------------------------------------------------------------

    [Fact]
    public async Task The_final_failure_is_recorded_on_the_attempt_of_this_delivery()
    {
        var invoices = new FakeInvoices { Attempt = new AttemptStart(Guid.NewGuid(), 5, 0) };
        var provider = new FakeProvider(invoices.Calls) { Failure = new InvoiceProviderException(InvoiceErrorCodes.ProviderUnavailable, "down", true) };
        var handler = InvoiceHandler(invoices, provider);
        await handler.HandleAsync(Delivery(EventTypes.InvoiceRequested), CancellationToken.None);

        var toDlq = await handler.RecordFinalFailureAsync(
            Delivery(EventTypes.InvoiceRequested),
            new FinalFailure(5, InvoiceErrorCodes.ProviderUnavailable, "down", "invoice.processing"),
            CancellationToken.None);

        Assert.True(toDlq);
        Assert.Equal(
            (TaskId, (Guid?)invoices.Attempt.AttemptId, InvoiceErrorCodes.ProviderUnavailable, InvoiceMessageHandler.WorkerName, "invoice.processing"),
            Assert.Single(invoices.FinalFailures));
    }

    [Fact]
    public async Task Without_a_handled_delivery_the_final_failure_finds_the_task_in_the_message_and_a_finished_task_is_acknowledged()
    {
        var emails = new FakeEmails();
        var finished = new FakeEmails { FinalFailureResult = null };
        var failure = new FinalFailure(null, "delivery_limit_reached", "crash loop", "notification.email");

        var toDlq = await EmailHandler(emails, new FakeSender(emails.Calls)).RecordFinalFailureAsync(Delivery(EventTypes.EmailRequested), failure, CancellationToken.None);
        var acknowledged = await EmailHandler(finished, new FakeSender(finished.Calls)).RecordFinalFailureAsync(Delivery(EventTypes.EmailRequested), failure, CancellationToken.None);

        Assert.True(toDlq);
        // No attempt of this delivery: the failure becomes an attempt of its own.
        Assert.Equal((TaskId, (Guid?)null, "delivery_limit_reached", EmailMessageHandler.WorkerName, "notification.email"), Assert.Single(emails.FinalFailures));
        Assert.False(acknowledged);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private const string QueueEventType = "the-event-type-of-the-queue";

    private static TaskRequestState State(ProcessingTaskType type, ProcessingTaskStatus status = ProcessingTaskStatus.Pending) =>
        new(TaskId, type, status, "corr-1", OrderId);

    private static InvoiceOrder InvoiceOrder(bool hasInvoice = false) =>
        new(
            OrderId,
            "WEB-1001",
            "Nguyen Van A",
            PaymentStatus.Paid,
            PaidAt,
            211_501.5m,
            [new InvoiceLine("TEA-1", "Green tea", 2, 75_000m), new InvoiceLine("TEA-2", "Oolong", 3, 20_500.5m)],
            hasInvoice);

    private static PaidOrderNotice Notice(string? email = "  anna.nguyen@example.com ") =>
        new(
            OrderId,
            "WEB-1001",
            "Nguyen Van A",
            email,
            PaymentStatus.Paid,
            PaidAt,
            211_501.5m,
            [new PaidOrderLine("TEA-1", "Green tea", 2, 75_000m), new PaidOrderLine("TEA-2", "Oolong", 3, 20_500.5m)]);

    private static InvoiceMessageHandler InvoiceHandler(
        FakeInvoices invoices, FakeProvider provider, IHostApplicationLifetime? lifetime = null, CapturingLoggerProvider? logs = null) =>
        new(
            invoices,
            provider,
            new WorkerCurrentUser(),
            new WorkerCorrelationContext(),
            Options.Create(new WorkerOptions()),
            lifetime ?? new ReportExportHandlerTests.FakeLifetime(),
            new Logger<InvoiceMessageHandler>(new LoggerFactory(logs is null ? [] : [logs])));

    private static EmailMessageHandler EmailHandler(
        FakeEmails emails,
        FakeSender sender,
        NotificationsOptions? notifications = null,
        IHostApplicationLifetime? lifetime = null,
        CapturingLoggerProvider? logs = null) =>
        new(
            emails,
            sender,
            Options.Create(notifications ?? new NotificationsOptions()),
            new WorkerCurrentUser(),
            new WorkerCorrelationContext(),
            Options.Create(new WorkerOptions()),
            lifetime ?? new ReportExportHandlerTests.FakeLifetime(),
            new Logger<EmailMessageHandler>(new LoggerFactory(logs is null ? [] : [logs])));

    private static (IQueueMessageHandler Handler, List<MessageRejection> Rejections, List<string> Calls) Rejecting(FakeInvoices invoices) =>
        (InvoiceHandler(invoices, new FakeProvider(invoices.Calls)), invoices.Rejections, invoices.Calls);

    private static (IQueueMessageHandler Handler, List<MessageRejection> Rejections, List<string> Calls) Rejecting(FakeEmails emails) =>
        (EmailHandler(emails, new FakeSender(emails.Calls)), emails.Rejections, emails.Calls);

    private static ReceivedMessage Delivery(string eventType, byte[]? body = null) =>
        new(
            body ?? Envelope(eventType, TaskId, new { taskId = TaskId }),
            "amqp-message-id", "amqp-correlation-id", Redelivered: false, eventType, DeliveryCount: 0, new BasicProperties());

    /// <param name="eventType">Null = the event type of the queue the message is delivered on.</param>
    private static byte[] Envelope(string? eventType, Guid? taskId, object payload, string messageId = "msg-1", string correlationId = "corr-1") =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            messageId, correlationId, taskId, eventType ?? QueueEventType, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, MessageJson.Options)));

    /// <summary>The shared part of both database doubles: answers as configured and records what was called, in order.</summary>
    private abstract class FakeProcessing : ITaskRequestProcessing
    {
        public List<string> Calls { get; } = [];

        public abstract TaskRequestState? TaskState { get; init; }

        public AttemptStart? Attempt { get; init; } = new(Guid.NewGuid(), 1, 0);

        public Exception? CompleteFailure { get; init; }

        public Exception? RecordFailure { get; init; }

        public int? FinalFailureResult { get; init; } = 1;

        public (string WorkerName, string InstanceId) Started { get; private set; }

        public List<(Guid TaskId, Guid AttemptId, string ErrorCode, string ErrorMessage)> TransientFailures { get; } = [];

        public List<(Guid TaskId, Guid? AttemptId, string ErrorCode, string WorkerName, string Queue)> FinalFailures { get; } = [];

        public List<MessageRejection> Rejections { get; } = [];

        public Task<TaskRequestState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
        {
            Calls.Add("get");
            return Task.FromResult(TaskState);
        }

        public Task<AttemptStart?> StartAttemptAsync(Guid taskId, string messageId, string workerName, string workerInstanceId, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            Started = (workerName, workerInstanceId);
            return Task.FromResult(Attempt);
        }

        public Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken)
        {
            Calls.Add("transient");
            if (RecordFailure is not null)
            {
                throw RecordFailure;
            }

            TransientFailures.Add((taskId, attemptId, errorCode, errorMessage));
            return Task.CompletedTask;
        }

        public Task<int?> RecordFinalFailureAsync(
            Guid taskId, Guid? attemptId, string errorCode, string errorMessage, string workerName, string workerInstanceId, string queue,
            CancellationToken cancellationToken)
        {
            FinalFailures.Add((taskId, attemptId, errorCode, workerName, queue));
            return Task.FromResult(FinalFailureResult);
        }

        public Task RecordRejectedMessageAsync(
            string? messageId, string? correlationId, Guid? taskId, MessageRejection rejection, string workerName, CancellationToken cancellationToken)
        {
            Rejections.Add(rejection);
            return Task.CompletedTask;
        }

        protected void Completing()
        {
            Calls.Add("complete");
            if (CompleteFailure is not null)
            {
                throw CompleteFailure;
            }
        }
    }

    private sealed class FakeInvoices : FakeProcessing, IInvoiceProcessingService
    {
        public override TaskRequestState? TaskState { get; init; } = State(ProcessingTaskType.Invoice);

        public InvoiceOrder Order { get; init; } = InvoiceOrder();

        public List<(Guid TaskId, Guid AttemptId, Invoice? Invoice)> Completed { get; } = [];

        public Task<InvoiceOrder> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Calls.Add("read");
            Assert.Equal(OrderId, orderId);
            return Task.FromResult(Order);
        }

        public Task CompleteAsync(Guid taskId, Guid attemptId, Invoice? invoice, CancellationToken cancellationToken)
        {
            Completing();
            Completed.Add((taskId, attemptId, invoice));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmails : FakeProcessing, IEmailProcessingService
    {
        public override TaskRequestState? TaskState { get; init; } = State(ProcessingTaskType.Email);

        public PaidOrderNotice Order { get; init; } = Notice();

        public List<(Guid TaskId, Guid AttemptId)> Completed { get; } = [];

        public Task<PaidOrderNotice> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Calls.Add("read");
            Assert.Equal(OrderId, orderId);
            return Task.FromResult(Order);
        }

        public Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
        {
            Completing();
            Completed.Add((taskId, attemptId));
            return Task.CompletedTask;
        }
    }

    /// <summary>The invoice provider: records its requests into the same call list as the database side.</summary>
    private sealed class FakeProvider(List<string> calls) : IInvoiceProviderClient
    {
        public DateTimeOffset IssuedAt { get; } = new(2026, 10, 9, 8, 31, 0, TimeSpan.Zero);

        public List<InvoiceRequest> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public Action? OnCall { get; init; }

        public Action? AfterCall { get; init; }

        public Task<IssuedInvoice> IssueAsync(InvoiceRequest request, CancellationToken cancellationToken)
        {
            calls.Add("issue");
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Requests.Add(request);
            AfterCall?.Invoke();
            return Task.FromResult(new IssuedInvoice("INV-0001", IssuedAt));
        }
    }

    private sealed class FakeSender(List<string> calls) : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public Exception? Failure { get; init; }

        public Action? OnCall { get; init; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            calls.Add("send");
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}

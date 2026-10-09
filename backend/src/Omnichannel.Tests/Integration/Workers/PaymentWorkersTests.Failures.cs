using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Invoices;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using Omnichannel.Workers.InvoiceWorker;
using Omnichannel.Workers.NotificationWorker;

namespace Omnichannel.Tests.Integration.Workers;

// Retries, final failures, independence of the two tasks, poison messages and shutdown.
public sealed partial class PaymentWorkersTests
{
    // ---- independence (doc 8.4) --------------------------------------------------------------------------------

    [Fact]
    public async Task A_rejected_e_mail_fails_its_own_task_while_the_invoice_of_the_same_order_succeeds_and_the_order_is_unchanged()
    {
        var order = await SeedOrderAsync(email: "anna.nguyen@sim-reject.test");
        var paid = await PayAsync(order.OrderId);
        var emailTaskId = paid.EmailTaskId!.Value;
        var probe = new PaymentProbe();
        var logs = new CapturingLoggerProvider();

        await RunWorkersAsync(
            "pay-email-rejected",
            async () =>
            {
                await WaitForStatusAsync(emailTaskId, ProcessingTaskStatus.Failed);
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
            },
            probe,
            logs);

        // The e-mail: refused at once - one attempt, no retry, its message in its own DLQ.
        var emailTask = await TaskAsync(emailTaskId);
        var attempt = Assert.Single(emailTask.Attempts);
        Assert.Equal(
            (1, ProcessingAttemptStatus.Failed, EmailErrorCodes.Rejected, EmailMessageHandler.WorkerName),
            (attempt.AttemptNo, attempt.Status, attempt.ErrorCode, attempt.WorkerName));
        Assert.DoesNotContain("anna.nguyen", attempt.ErrorMessage, StringComparison.Ordinal);
        var deadLetter = Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 1));
        Assert.Equal(emailTask.MessageId, deadLetter.BasicProperties.MessageId);

        // Made visible like every final failure: the event and the alert of the task, both pointing at the order.
        var failure = Assert.Single(await ProcessingFailedLogsAsync(emailTaskId));
        var payload = JsonNode.Parse(failure.Payload!)!;
        Assert.Equal(
            (nameof(ProcessingTaskType.Email), EmailErrorCodes.Rejected, EmailQueue, 1, (Guid?)order.OrderId, EmailMessageHandler.WorkerName),
            (payload["taskType"]!.GetValue<string>(), payload["errorCode"]!.GetValue<string>(), payload["queue"]!.GetValue<string>(),
                payload["attemptNo"]!.GetValue<int>(), failure.OrderId, failure.WorkerName));
        Assert.DoesNotContain("anna.nguyen", failure.Payload, StringComparison.Ordinal);
        var alert = await AlertOfAsync(emailTaskId);
        Assert.Equal(
            (AlertType.ProcessingFailed, AlertStatus.Open, EmailErrorCodes.Rejected, (Guid?)order.OrderId, (Guid?)emailTaskId),
            (alert!.Type, alert.Status, alert.ErrorCode, alert.OrderId, alert.TaskId));

        // The invoice of the same order: issued and stored, its task untouched by the failure next to it.
        var invoiceTask = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal((ProcessingTaskStatus.Succeeded, ProcessingAttemptStatus.Succeeded), (invoiceTask.Status, Assert.Single(invoiceTask.Attempts).Status));
        Assert.NotNull(await InvoiceAsync(order.OrderId));
        Assert.Null(await AlertOfAsync(paid.InvoiceTaskId));
        Assert.Empty(await ProcessingFailedLogsAsync(paid.InvoiceTaskId));
        Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue)));

        // And the order: still Reserved, still Paid, its stock where it was. One failed e-mail fails nothing else.
        await AssertOrderUntouchedAsync(order, paid);
        AssertNothingPersonalLogged(logs, "anna.nguyen", Assert.Single(probe.EmailsOf(order.OrderId)).TextBody);
    }

    [Fact]
    public async Task A_refused_invoice_fails_its_own_task_while_the_e_mail_of_the_same_order_is_sent()
    {
        var order = await SeedOrderAsync(email: "customer@example.com", orderPrefix: "SIM-INV-PERM-");
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();

        await RunWorkersAsync(
            "pay-invoice-rejected",
            async () =>
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Failed);
                await WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded);
            },
            probe);

        // The invoice: refused at once - one attempt, the event, the alert, the DLQ, and no invoice row.
        var invoiceTask = await TaskAsync(paid.InvoiceTaskId);
        var attempt = Assert.Single(invoiceTask.Attempts);
        Assert.Equal(
            (1, ProcessingAttemptStatus.Failed, InvoiceErrorCodes.ProviderRejected, InvoiceMessageHandler.WorkerName),
            (attempt.AttemptNo, attempt.Status, attempt.ErrorCode, attempt.WorkerName));
        var failure = Assert.Single(await ProcessingFailedLogsAsync(paid.InvoiceTaskId));
        var payload = JsonNode.Parse(failure.Payload!)!;
        Assert.Equal(
            (nameof(ProcessingTaskType.Invoice), InvoiceErrorCodes.ProviderRejected, InvoiceQueue, (Guid?)order.OrderId),
            (payload["taskType"]!.GetValue<string>(), payload["errorCode"]!.GetValue<string>(), payload["queue"]!.GetValue<string>(), failure.OrderId));
        var alert = await AlertOfAsync(paid.InvoiceTaskId);
        Assert.Equal((AlertType.ProcessingFailed, AlertStatus.Open, InvoiceErrorCodes.ProviderRejected), (alert!.Type, alert.Status, alert.ErrorCode));
        Assert.Equal(invoiceTask.MessageId, Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue), 1)).BasicProperties.MessageId);
        Assert.Null(await InvoiceAsync(order.OrderId));
        Assert.Single(probe.InvoiceRequestsOf(order.OrderId));

        // The e-mail of the same order went out: it neither waits for the invoice nor reads it.
        var emailTask = await TaskAsync(paid.EmailTaskId!.Value);
        Assert.Equal((ProcessingTaskStatus.Succeeded, ProcessingAttemptStatus.Succeeded), (emailTask.Status, Assert.Single(emailTask.Attempts).Status));
        Assert.Single(probe.EmailsOf(order.OrderId));
        Assert.Null(await AlertOfAsync(emailTask.TaskId));
        Assert.Equal(0u, await MessageCountAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue)));
        await AssertOrderUntouchedAsync(order, paid);
    }

    // ---- retries -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_is_unavailable_once_is_retried_with_delay_and_the_second_attempt_issues_the_invoice()
    {
        var order = await SeedOrderAsync(email: null, orderPrefix: "SIM-INV-TEMP-");
        var paid = await PayAsync(order.OrderId);
        var probe = new PaymentProbe();

        await RunWorkersAsync(
            "pay-invoice-retry",
            async () =>
            {
                var waiting = await WaitForTaskAsync(paid.InvoiceTaskId, t => t.Attempts.Count >= 1 && t.Status != ProcessingTaskStatus.InProgress, "first attempt over");
                if (waiting.Status != ProcessingTaskStatus.Succeeded)
                {
                    Assert.Equal((ProcessingTaskStatus.RetryScheduled, 1), (waiting.Status, waiting.RetryCount));
                }

                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
                await WaitForEmptyAsync(InvoiceQueue);
            },
            probe);

        var task = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Failed, InvoiceErrorCodes.ProviderUnavailable), (2, ProcessingAttemptStatus.Succeeded, null)],
            task.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));
        Assert.Equal(1, task.RetryCount);
        Assert.Equal([1, 2], probe.InvoiceRequestsOf(order.OrderId).Select(r => r.AttemptNo));
        Assert.NotNull(await InvoiceAsync(order.OrderId));
        // A retry is no failure: no event, no alert, nothing dead-lettered.
        Assert.Empty(await ProcessingFailedLogsAsync(paid.InvoiceTaskId));
        Assert.Null(await AlertOfAsync(paid.InvoiceTaskId));
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task A_mail_server_that_is_unavailable_once_is_retried_and_the_e_mail_is_sent_by_the_second_attempt()
    {
        var order = await SeedOrderAsync(email: "later@sim-temp.test");
        var paid = await PayAsync(order.OrderId);
        var emailTaskId = paid.EmailTaskId!.Value;
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-email-retry", () => WaitForStatusAsync(emailTaskId, ProcessingTaskStatus.Succeeded), probe);

        var task = await TaskAsync(emailTaskId);
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Failed, EmailErrorCodes.Unavailable), (2, ProcessingAttemptStatus.Succeeded, null)],
            task.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));
        Assert.Equal([1, 2], probe.EmailsOf(order.OrderId).Select(m => m.AttemptNo));
        Assert.Null(await AlertOfAsync(emailTaskId));
    }

    [Fact]
    public async Task A_provider_that_stays_down_fails_the_invoice_after_the_last_attempt_and_a_manual_retry_issues_it_and_resolves_the_alert()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);

        await RunWorkersAsync(
            "pay-invoice-down",
            () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Failed),
            settings: new Dictionary<string, string?> { ["InvoiceSimulation:Mode"] = "TransientError", ["InvoiceWorker:MaxAttempts"] = "2" });

        var failed = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal(
            [(1, ProcessingAttemptStatus.Failed, InvoiceErrorCodes.ProviderUnavailable), (2, ProcessingAttemptStatus.Failed, InvoiceErrorCodes.ProviderUnavailable)],
            failed.Attempts.OrderBy(a => a.AttemptNo).Select(a => (a.AttemptNo, a.Status, a.ErrorCode)));
        Assert.Equal(failed.MessageId, Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue), 1)).BasicProperties.MessageId);
        Assert.Equal(AlertStatus.Open, (await AlertOfAsync(paid.InvoiceTaskId))!.Status);
        Assert.Equal(2, JsonNode.Parse(Assert.Single(await ProcessingFailedLogsAsync(paid.InvoiceTaskId)).Payload!)!["attemptNo"]!.GetValue<int>());
        Assert.Null(await InvoiceAsync(order.OrderId));

        // The provider is back; an operator retries the task: the generic copy of its message, a fresh attempt budget.
        var retry = await fixture.WithServicesAsync(services =>
        {
            services.GetRequiredService<WorkerCurrentUser>().UserId = Payer;
            return services.GetRequiredService<IProcessingTaskService>().RetryAsync(paid.InvoiceTaskId, CancellationToken.None);
        });
        Assert.Equal(ProcessingTaskStatus.Pending, retry.Status);
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-invoice-back", () => WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded), probe);

        var done = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal((3, 2, ProcessingAttemptStatus.Succeeded), (done.Attempts.Count, done.AttemptBudgetStart, done.Attempts.Single(a => a.AttemptNo == 3).Status));
        Assert.NotEqual(failed.MessageId, done.MessageId);
        // The same key as before the failure: the invoice the provider would have issued then.
        Assert.Equal($"invoice:{order.OrderId}", Assert.Single(probe.InvoiceRequestsOf(order.OrderId)).Key);
        Assert.NotNull(await InvoiceAsync(order.OrderId));
        // The task succeeded: its alert is over, in the transaction that stored the invoice.
        Assert.Equal(AlertStatus.Resolved, (await AlertOfAsync(paid.InvoiceTaskId))!.Status);
    }

    [Fact]
    public async Task A_mail_server_that_stays_down_fails_the_e_mail_after_the_last_attempt_and_a_manual_retry_sends_it()
    {
        var order = await SeedOrderAsync(email: "patient@example.com");
        var paid = await PayAsync(order.OrderId);
        var emailTaskId = paid.EmailTaskId!.Value;

        await RunWorkersAsync(
            "pay-email-down",
            () => WaitForStatusAsync(emailTaskId, ProcessingTaskStatus.Failed),
            settings: new Dictionary<string, string?> { ["EmailSimulation:Mode"] = "TransientError", ["NotificationWorker:MaxAttempts"] = "2" });

        var failed = await TaskAsync(emailTaskId);
        Assert.All(failed.Attempts, a => Assert.Equal((ProcessingAttemptStatus.Failed, EmailErrorCodes.Unavailable), (a.Status, a.ErrorCode)));
        Assert.Equal(2, failed.Attempts.Count);
        Assert.Equal(AlertStatus.Open, (await AlertOfAsync(emailTaskId))!.Status);
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 1);

        await fixture.WithServicesAsync(services =>
        {
            services.GetRequiredService<WorkerCurrentUser>().UserId = Payer;
            return services.GetRequiredService<IProcessingTaskService>().RetryAsync(emailTaskId, CancellationToken.None);
        });
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-email-back", () => WaitForStatusAsync(emailTaskId, ProcessingTaskStatus.Succeeded), probe);

        Assert.Single(probe.EmailsOf(order.OrderId));
        Assert.Equal(AlertStatus.Resolved, (await AlertOfAsync(emailTaskId))!.Status);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_is_a_timeout_of_the_attempt()
    {
        var order = await SeedOrderAsync(email: "slow@example.com");
        var paid = await PayAsync(order.OrderId);

        await RunWorkersAsync(
            "pay-timeout",
            async () =>
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Failed);
                await WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Failed);
            },
            settings: new Dictionary<string, string?>
            {
                ["InvoiceSimulation:Mode"] = "Timeout",
                ["EmailSimulation:Mode"] = "Timeout",
                ["InvoiceWorker:CallTimeout"] = "00:00:00.300",
                ["NotificationWorker:CallTimeout"] = "00:00:00.300",
                ["InvoiceWorker:MaxAttempts"] = "1",
                ["NotificationWorker:MaxAttempts"] = "1"
            });

        Assert.Equal(InvoiceErrorCodes.ProviderTimeout, Assert.Single((await TaskAsync(paid.InvoiceTaskId)).Attempts).ErrorCode);
        Assert.Equal(EmailErrorCodes.Timeout, Assert.Single((await TaskAsync(paid.EmailTaskId!.Value)).Attempts).ErrorCode);
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue), 1);
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 1);
    }

    // ---- what cannot be done -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_address_removed_after_the_payment_fails_the_e_mail_for_good_and_nothing_is_sent()
    {
        var order = await SeedOrderAsync(email: "gone@example.com");
        var paid = await PayAsync(order.OrderId);
        var emailTaskId = paid.EmailTaskId!.Value;
        // The customer is edited before the worker runs: the address is gone.
        await fixture.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""UPDATE "Customers" SET "Email" = NULL WHERE "CustomerId" = {order.CustomerId}"""));
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-no-recipient", () => WaitForStatusAsync(emailTaskId, ProcessingTaskStatus.Failed), probe);

        var attempt = Assert.Single((await TaskAsync(emailTaskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Failed, EmailErrorCodes.NoRecipient), (attempt.Status, attempt.ErrorCode));
        Assert.Empty(probe.EmailsOf(order.OrderId));
        Assert.Equal((AlertStatus.Open, EmailErrorCodes.NoRecipient), ((await AlertOfAsync(emailTaskId))!.Status, (await AlertOfAsync(emailTaskId))!.ErrorCode));
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 1);
    }

    [Fact]
    public async Task An_address_changed_after_the_payment_gets_the_e_mail()
    {
        var order = await SeedOrderAsync(email: "old@example.com");
        var paid = await PayAsync(order.OrderId);
        await fixture.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "Customers" SET "Email" = 'new.address@example.org' WHERE "CustomerId" = {order.CustomerId}"""));
        var probe = new PaymentProbe();

        await RunWorkersAsync("pay-new-address", () => WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded), probe);

        Assert.Equal("new.address@example.org", Assert.Single(probe.EmailsOf(order.OrderId)).To);
    }

    [Fact]
    public async Task Tasks_of_an_order_that_is_not_paid_fail_for_good_without_a_call()
    {
        // Cannot come from the payment step (it writes the tasks with the payment); the workers refuse them all the same.
        var order = await SeedOrderAsync(email: "unpaid@example.com");
        var invoiceTask = await SeedTaskAsync(ProcessingTaskType.Invoice, order.OrderId, EventTypes.InvoiceRequested);
        var emailTask = await SeedTaskAsync(ProcessingTaskType.Email, order.OrderId, EventTypes.EmailRequested);
        var probe = new PaymentProbe();

        await RunWorkersAsync(
            "pay-unpaid",
            async () =>
            {
                await WaitForStatusAsync(invoiceTask.TaskId, ProcessingTaskStatus.Failed);
                await WaitForStatusAsync(emailTask.TaskId, ProcessingTaskStatus.Failed);
            },
            probe);

        Assert.Equal(InvoiceErrorCodes.OrderNotPaid, Assert.Single((await TaskAsync(invoiceTask.TaskId)).Attempts).ErrorCode);
        Assert.Equal(EmailErrorCodes.OrderNotPaid, Assert.Single((await TaskAsync(emailTask.TaskId)).Attempts).ErrorCode);
        Assert.Empty(probe.InvoiceRequestsOf(order.OrderId));
        Assert.Empty(probe.EmailsOf(order.OrderId));
        Assert.Null(await InvoiceAsync(order.OrderId));
        Assert.Equal(PaymentStatus.Unpaid, (await OrderAsync(order.OrderId)).PaymentStatus);
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue), 1);
        await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 1);
    }

    // ---- poison ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Messages_that_can_never_be_processed_are_logged_with_their_code_and_dead_lettered_on_both_queues()
    {
        var unknownTask = Guid.CreateVersion7();
        var order = await SeedOrderAsync(email: "poison@example.com");
        // Tasks that exist, but: of the other worker, or without an order.
        var emailTask = await SeedTaskAsync(ProcessingTaskType.Email, order.OrderId, EventTypes.EmailRequested);
        var orphan = await SeedTaskAsync(ProcessingTaskType.Invoice, orderId: null, EventTypes.InvoiceRequested);
        // Their own (seeded) messages must not interfere: only what this test publishes counts.
        await fixture.WithDbAsync(db => db.OutboxEvents
            .Where(e => e.AggregateId == emailTask.TaskId.ToString() || e.AggregateId == orphan.TaskId.ToString())
            .ExecuteDeleteAsync());

        var messages = new (string MessageId, string RoutingKey, byte[] Body, string Code, Guid? TaskId, string EventType)[]
        {
            ($"inv-json-{_tag}", EventTypes.InvoiceRequested, Encoding.UTF8.GetBytes("{ not json"), MessageRejectionCodes.InvalidJson, null, InvoiceEvents.MessageRejected),
            ($"inv-unknown-{_tag}", EventTypes.InvoiceRequested, Envelope($"inv-unknown-{_tag}", EventTypes.InvoiceRequested, unknownTask), MessageRejectionCodes.UnknownTask, unknownTask, InvoiceEvents.MessageRejected),
            ($"inv-other-{_tag}", EventTypes.InvoiceRequested, Envelope($"inv-other-{_tag}", EventTypes.InvoiceRequested, emailTask.TaskId), MessageRejectionCodes.TaskMismatch, emailTask.TaskId, InvoiceEvents.MessageRejected),
            ($"inv-orphan-{_tag}", EventTypes.InvoiceRequested, Envelope($"inv-orphan-{_tag}", EventTypes.InvoiceRequested, orphan.TaskId), MessageRejectionCodes.TaskMismatch, orphan.TaskId, InvoiceEvents.MessageRejected),
            ($"mail-json-{_tag}", EventTypes.EmailRequested, Encoding.UTF8.GetBytes("null"), MessageRejectionCodes.EmptyBody, null, NotificationEvents.MessageRejected),
            ($"mail-type-{_tag}", EventTypes.EmailRequested, Envelope($"mail-type-{_tag}", EventTypes.InvoiceRequested, emailTask.TaskId), MessageRejectionCodes.UnexpectedEventType, null, NotificationEvents.MessageRejected),
            ($"mail-unknown-{_tag}", EventTypes.EmailRequested, Envelope($"mail-unknown-{_tag}", EventTypes.EmailRequested, unknownTask), MessageRejectionCodes.UnknownTask, unknownTask, NotificationEvents.MessageRejected)
        };
        var probe = new PaymentProbe();

        await RunWorkersAsync(
            "pay-poison",
            async () =>
            {
                foreach (var message in messages)
                {
                    await fixture.PublishAsync(message.RoutingKey, message.Body, message.MessageId);
                }

                var deadInvoices = await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(InvoiceQueue), 4);
                var deadEmails = await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(EmailQueue), 3);
                Assert.Equal(
                    messages.Select(m => m.MessageId).Order(StringComparer.Ordinal),
                    deadInvoices.Concat(deadEmails).Select(m => m.BasicProperties.MessageId!).Order(StringComparer.Ordinal));
            },
            probe);

        var rejected = (await fixture.WithDbAsync(db => db.SystemEventLogs.AsNoTracking()
                .Where(l => l.EventType == InvoiceEvents.MessageRejected || l.EventType == NotificationEvents.MessageRejected)
                .ToListAsync()))
            .Where(l => l.Payload!.Contains(_tag, StringComparison.Ordinal))
            .ToList();
        Assert.Equal(
            messages.Select(m => (m.MessageId, m.Code, m.TaskId, m.EventType)).OrderBy(m => m.MessageId, StringComparer.Ordinal),
            rejected
                .Select(l => (JsonNode.Parse(l.Payload!)!["messageId"]!.GetValue<string>(), JsonNode.Parse(l.Payload!)!["reasonCode"]!.GetValue<string>(), l.TaskId, l.EventType))
                .OrderBy(m => m.Item1, StringComparer.Ordinal));
        Assert.All(rejected, l =>
        {
            Assert.Equal(SystemEventLevel.Error, l.Level);
            Assert.Equal(l.EventType == InvoiceEvents.MessageRejected ? InvoiceMessageHandler.WorkerName : EmailMessageHandler.WorkerName, l.WorkerName);
            // The text for operators is stored next to the code.
            Assert.False(string.IsNullOrWhiteSpace(JsonNode.Parse(l.Payload!)!["reason"]!.GetValue<string>()));
        });

        // The tasks the messages named were not touched, and nobody was called.
        Assert.Equal((ProcessingTaskStatus.Pending, 0), ((await TaskAsync(emailTask.TaskId)).Status, (await TaskAsync(emailTask.TaskId)).Attempts.Count));
        Assert.Equal(ProcessingTaskStatus.Pending, (await TaskAsync(orphan.TaskId)).Status);
        Assert.Empty(probe.InvoiceRequestsOf(order.OrderId));
        Assert.Empty(probe.EmailsOf(order.OrderId));
    }

    // ---- shutdown ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Host_stopping_during_a_call_to_the_provider_cancels_it_and_the_message_is_redelivered()
    {
        var order = await SeedOrderAsync(email: null);
        var paid = await PayAsync(order.OrderId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new PaymentProbe
        {
            OnInvoiceRequest = async (request, token) =>
            {
                if (request.OrderId != order.OrderId)
                {
                    return;
                }

                started.TrySetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
        };

        // A long call timeout: only the host stopping can end this call.
        var host = StartWorkers("pay-stopping", slow, settings: new Dictionary<string, string?> { ["InvoiceWorker:CallTimeout"] = "00:02:00" });
        try
        {
            await started.Task.WaitAsync(Timeout);
        }
        finally
        {
            await StopAsync(host);
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Not acknowledged, not failed: the attempt is still Running and the message is back in the queue.
        var interrupted = await TaskAsync(paid.InvoiceTaskId);
        Assert.Equal(ProcessingTaskStatus.InProgress, interrupted.Status);
        Assert.Equal(ProcessingAttemptStatus.Running, Assert.Single(interrupted.Attempts).Status);
        await Eventually.WaitAsync(() => MessageCountAsync(InvoiceQueue), count => count == 1, Timeout, "message back in invoice.processing");
        Assert.Empty(await ProcessingFailedLogsAsync(paid.InvoiceTaskId));
        Assert.Null(await InvoiceAsync(order.OrderId));

        var probe = new PaymentProbe();
        await RunWorkersAsync(
            "pay-restarted",
            async () =>
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
                await WaitForEmptyAsync(InvoiceQueue);
            },
            probe);

        var attempts = (await TaskAsync(paid.InvoiceTaskId)).Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ProcessingAttempt.AbandonedErrorCode), (attempts[0].Status, attempts[0].ErrorCode));
        Assert.Equal((ProcessingAttemptStatus.Succeeded, "pay-restarted"), (attempts[1].Status, attempts[1].WorkerInstanceId));
        Assert.Equal(2, Assert.Single(probe.InvoiceRequestsOf(order.OrderId)).AttemptNo);
        Assert.NotNull(await InvoiceAsync(order.OrderId));
        await AssertNoDeadLettersAsync();
    }

    // ---- several instances -------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_instances_working_the_same_queues_leave_one_invoice_and_one_e_mail_per_order()
    {
        var orders = new List<(Seeded Order, Paid Paid)>();
        for (var n = 0; n < 6; n++)
        {
            var order = await SeedOrderAsync(email: $"customer{n}@example.com");
            orders.Add((order, await PayAsync(order.OrderId)));
        }

        var probe = new PaymentProbe();
        var first = StartWorkers("pay-a", probe);
        var second = StartWorkers("pay-b", probe);
        try
        {
            foreach (var (_, paid) in orders)
            {
                await WaitForStatusAsync(paid.InvoiceTaskId, ProcessingTaskStatus.Succeeded);
                await WaitForStatusAsync(paid.EmailTaskId!.Value, ProcessingTaskStatus.Succeeded);
            }

            await WaitForEmptyAsync(InvoiceQueue, EmailQueue);
        }
        finally
        {
            await StopAsync(first);
            await StopAsync(second);
        }

        foreach (var (order, paid) in orders)
        {
            Assert.Equal(1, await fixture.WithDbAsync(db => db.Invoices.CountAsync(i => i.OrderId == order.OrderId)));
            Assert.Single((await TaskAsync(paid.InvoiceTaskId)).Attempts);
            Assert.Single(probe.EmailsOf(order.OrderId));
        }

        // Six different orders: six different numbers.
        var orderIds = orders.Select(o => o.Order.OrderId).ToList();
        var numbers = await fixture.WithDbAsync(db => db.Invoices.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .Select(i => i.InvoiceNumber)
            .ToListAsync());
        Assert.Equal(6, numbers.Distinct(StringComparer.Ordinal).Count());
        await AssertNoDeadLettersAsync();
    }
}

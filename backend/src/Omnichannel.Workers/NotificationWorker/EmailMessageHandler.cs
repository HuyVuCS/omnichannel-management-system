using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Notifications;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Enums;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.NotificationWorker;

/// <summary>
/// Handles one <c>email.requested</c> delivery (doc §8.4): sends the payment confirmation of an order to its customer.
/// The delivery, attempt and failure handling is <see cref="TaskRequestMessageHandler"/>'s; the work is:
/// <list type="bullet">
/// <item>the order with its lines and the customer <b>as it is now</b> is read without a lock;</item>
/// <item>an order that is not paid (<c>email_order_not_paid</c>) and a customer without an address
/// (<c>email_no_recipient</c> - it was removed after the payment) fail for good;</item>
/// <item>the text is built when it is sent, in the language and time zone of the <c>Notifications</c> settings;</item>
/// <item>the sender is called outside any transaction, with the key <c>email:payment:{orderId}</c>;</item>
/// <item>one transaction completes attempt and task. Nothing of the e-mail is stored.</item>
/// </list>
/// Nothing here reads or writes an invoice or looks at the invoice task of the order: a confirmation that cannot be
/// sent leaves the invoice and the order as they are, and the other way round.
/// </summary>
public sealed class EmailMessageHandler(
    IEmailProcessingService emails,
    IEmailSender sender,
    IOptions<NotificationsOptions> notifications,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<EmailMessageHandler> logger)
    : TaskRequestMessageHandler(Worker, emails, currentUser, correlationContext, workerOptions, lifetime, logger)
{
    public const string WorkerName = "NotificationWorker";
    public const string Actor = "system:notification-worker";

    private static readonly TaskRequestWorker Worker = new(WorkerName, Actor, EventTypes.EmailRequested, ProcessingTaskType.Email);

    private readonly IEmailProcessingService _emails = emails;
    private readonly IEmailSender _sender = sender;
    private readonly NotificationsOptions _notifications = notifications.Value;
    private readonly ILogger _logger = logger;

    protected override async Task<PermanentFailure?> ProcessAsync(
        Guid taskId,
        Guid orderId,
        AttemptStart attempt,
        CancellationToken stopping,
        CancellationToken cancellationToken)
    {
        var order = await _emails.ReadOrderAsync(orderId, cancellationToken);

        if (order.PaymentStatus != PaymentStatus.Paid || order.PaidAt is not { } paidAt)
        {
            return new PermanentFailure(EmailErrorCodes.OrderNotPaid, "The order of the e-mail task is not paid.");
        }

        if (string.IsNullOrWhiteSpace(order.CustomerEmail))
        {
            return new PermanentFailure(EmailErrorCodes.NoRecipient, "The customer of the order has no e-mail address.");
        }

        // The validator found the zone at start-up.
        ReportTimeZones.TryFind(_notifications.TimeZone, out var timeZone);
        var content = PaymentConfirmationEmail.Build(order, paidAt, _notifications.Language, timeZone);
        var recipient = order.CustomerEmail.Trim();

        await _sender.SendAsync(
            new EmailMessage(PaymentTaskKeys.ForEmail(order.OrderId), recipient, content.Subject, content.TextBody, attempt.AttemptNo),
            stopping);
        await _emails.CompleteAsync(taskId, attempt.AttemptId, cancellationToken);

        // The address only masked, the text never.
        _logger.LogInformation(
            "Payment confirmation sent to {Recipient} (attempt {AttemptNo})", EmailAddress.Mask(recipient), attempt.AttemptNo);
        return null;
    }

    protected override bool IsProviderAnswer(Exception exception) => exception is EmailSendException;
}

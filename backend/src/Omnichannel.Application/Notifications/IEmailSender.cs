namespace Omnichannel.Application.Notifications;

/// <summary>
/// Sends one e-mail (doc §8.4). One implementation per mail provider; the Notification Worker only knows this
/// interface, so a real SMTP or API adapter replaces the simulated one without touching it.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. <see cref="EmailMessage.Key"/> is the idempotency key of the e-mail, for a
    /// provider that can drop a message it has seen before.
    /// </summary>
    /// <exception cref="EmailSendException">The e-mail was not accepted.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <param name="Key">Idempotency key of the e-mail: <c>email:payment:{orderId}</c>.</param>
/// <param name="To">The recipient's address. Never logged in full (<see cref="EmailAddress.Mask"/>).</param>
/// <param name="TextBody">Plain text. Never logged.</param>
/// <param name="AttemptNo">The attempt of the e-mail task that sends (for the provider's own diagnostics).</param>
public sealed record EmailMessage(string Key, string To, string Subject, string TextBody, int AttemptNo);

/// <summary>
/// Sending an e-mail failed. <see cref="Exception.Message"/> is sanitised (safe for the attempt history and the
/// dashboard: no credentials, no response bodies, the recipient only masked).
/// </summary>
/// <param name="errorCode">One of the sender codes of <see cref="EmailErrorCodes"/>.</param>
/// <param name="isTransient">True when trying again later can succeed (retry with delay); false = permanent failure.</param>
public sealed class EmailSendException(string errorCode, string message, bool isTransient, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public bool IsTransient { get; } = isTransient;
}

/// <summary><c>ErrorCode</c> values of a failed e-mail attempt.</summary>
public static class EmailErrorCodes
{
    /// <summary>No answer within the send timeout (transient).</summary>
    public const string Timeout = "email_timeout";

    /// <summary>The mail provider is down or answered 5xx (transient).</summary>
    public const string Unavailable = "email_unavailable";

    /// <summary>The mail provider refused the message or the recipient (permanent).</summary>
    public const string Rejected = "email_rejected";

    /// <summary>Not called: the sender's circuit breaker is open after repeated transient failures (transient).</summary>
    public const string CircuitOpen = "email_circuit_open";

    /// <summary>The customer has no e-mail address (any more): nobody to send to (permanent).</summary>
    public const string NoRecipient = "email_no_recipient";

    /// <summary>The order of the task is not paid: there is no payment to confirm (permanent).</summary>
    public const string OrderNotPaid = "email_order_not_paid";
}

/// <summary>SystemEventLog event types written by the Notification Worker.</summary>
public static class NotificationEvents
{
    /// <summary>A message of <c>notification.email</c> was sent to the DLQ without processing (Error).</summary>
    public const string MessageRejected = "email.message_rejected";
}

public static class EmailAddress
{
    /// <summary>
    /// What of an address may be logged or stored in an error message: the first character of the local part and the
    /// domain (<c>a***@example.com</c>). Anything that is not <c>local@domain</c> is hidden completely.
    /// </summary>
    public static string Mask(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at > 0 && at < address!.Length - 1 ? $"{address[0]}***{address[at..]}" : "***";
    }

    /// <summary>The part after the last <c>@</c>, lower-cased; empty when the address has none.</summary>
    public static string DomainOf(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at >= 0 ? address![(at + 1)..].ToLowerInvariant() : string.Empty;
    }
}

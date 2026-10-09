namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Payload of <see cref="EventTypes.InvoiceRequested"/>: the invoice task to run. The order is not part of the message;
/// the Invoice Worker reads it from the database, through the task.
/// </summary>
public sealed record InvoiceRequestedPayload(Guid TaskId);

/// <summary>
/// Payload of <see cref="EventTypes.EmailRequested"/>: the e-mail task to run. Neither the recipient nor the text is
/// part of the message; the Notification Worker reads the order and the customer's current address when it sends.
/// </summary>
public sealed record EmailRequestedPayload(Guid TaskId);

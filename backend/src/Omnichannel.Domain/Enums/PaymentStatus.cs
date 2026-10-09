namespace Omnichannel.Domain.Enums;

/// <summary>
/// Whether an order was paid. Its own field on purpose: payment never changes <see cref="OrderStatus"/>, and the work a
/// payment starts (invoice, confirmation e-mail) is tracked by its own tasks.
/// </summary>
public enum PaymentStatus
{
    Unpaid,
    Paid
}

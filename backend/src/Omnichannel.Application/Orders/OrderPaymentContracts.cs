using FluentValidation;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Orders;

/// <summary>Body of <c>POST /api/orders/{orderId}/payment</c>: the order was paid in full, by this method.</summary>
/// <param name="PaymentReference">Optional receipt or transfer number; blank = none.</param>
public sealed record PayOrderRequest(PaymentMethod? PaymentMethod, string? PaymentReference);

/// <summary>
/// The payment is recorded and its follow-up work is queued: the invoice always, the confirmation e-mail only when the
/// customer has an e-mail address.
/// </summary>
/// <param name="EmailTaskId">Null: the customer has no e-mail address, so no e-mail was queued.</param>
public sealed record PayOrderResponse(
    Guid OrderId,
    PaymentStatus PaymentStatus,
    DateTimeOffset PaidAt,
    Guid InvoiceTaskId,
    Guid? EmailTaskId);

public sealed class PayOrderRequestValidator : AbstractValidator<PayOrderRequest>
{
    public PayOrderRequestValidator()
    {
        RuleFor(x => x.PaymentMethod).NotNull().IsInEnum();

        RuleFor(x => x.PaymentReference).TrimmedMaximumLength(Order.MaxPaymentReferenceLength);
    }
}

/// <summary>Idempotency keys of the two tasks a payment creates: one invoice and one confirmation per order.</summary>
public static class PaymentTaskKeys
{
    /// <summary><c>invoice:{orderId}</c> - also the key the invoice provider is called with.</summary>
    public static string ForInvoice(Guid orderId) => Invoice.ProviderKeyOf(orderId);

    /// <summary><c>email:payment:{orderId}</c> - also the key the e-mail sender is called with.</summary>
    public static string ForEmail(Guid orderId) => $"email:payment:{orderId}";
}

/// <summary>SystemEventLog event types written by the payment step.</summary>
public static class OrderPaymentEvents
{
    /// <summary>
    /// An order was marked as paid (Information). Payload: <c>{orderId, paymentMethod, amount, paidBy}</c> - never the
    /// payment reference.
    /// </summary>
    public const string Paid = "order.paid";
}

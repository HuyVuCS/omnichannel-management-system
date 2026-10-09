using Omnichannel.Application.Common.Exceptions;

namespace Omnichannel.Application.Orders;

/// <summary>
/// The payment step (doc §8.4 "Payment Succeeded"): a Manager or Admin records that a reserved order was paid.
/// </summary>
public interface IOrderPaymentService
{
    /// <summary>
    /// One transaction, under the lock of the order row: the payment fields, the audit event <c>order.paid</c>, the
    /// invoice task with its <c>invoice.requested</c> message and - when the customer has an e-mail address - the e-mail
    /// task with its <c>email.requested</c> message. The order's status and every stock stay as they are.
    /// </summary>
    /// <exception cref="NotFoundException">No such order (<c>order_not_found</c>).</exception>
    /// <exception cref="ConflictException">
    /// The order is paid already (<c>order_already_paid</c>), or it is not <c>Reserved</c> (<c>order_not_payable</c>).
    /// </exception>
    Task<PayOrderResponse> PayAsync(Guid orderId, PayOrderRequest request, CancellationToken cancellationToken);
}

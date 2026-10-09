using Omnichannel.Application.Processing;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Notifications;

/// <summary>
/// Persistence side of the Notification Worker. The e-mail is sent between <see cref="ReadOrderAsync"/> and
/// <see cref="CompleteAsync"/>, in the worker, outside any transaction. Nothing here reads or writes an invoice: the
/// confirmation of a payment does not depend on the invoice of the order, nor the invoice on it (doc §8.4).
/// </summary>
public interface IEmailProcessingService : ITaskRequestProcessing
{
    /// <summary>The order of an e-mail task with its lines and the customer as it is now, read without a lock.</summary>
    Task<PaidOrderNotice> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// One transaction: attempt and task Succeeded, and the alert of an earlier final failure resolved. When another
    /// delivery completed the task meanwhile, only the attempt is closed.
    /// </summary>
    Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken);
}

/// <param name="CustomerEmail">The customer's current address; null when there is none (any more).</param>
/// <param name="PaidAt">Null while the order is unpaid.</param>
/// <param name="Lines">Ordered by SKU.</param>
public sealed record PaidOrderNotice(
    Guid OrderId,
    string ExternalOrderId,
    string CustomerName,
    string? CustomerEmail,
    PaymentStatus PaymentStatus,
    DateTimeOffset? PaidAt,
    decimal TotalAmount,
    IReadOnlyList<PaidOrderLine> Lines);

/// <param name="UnitPrice">The price snapshot of the order line.</param>
public sealed record PaidOrderLine(string Sku, string Name, int Quantity, decimal UnitPrice);

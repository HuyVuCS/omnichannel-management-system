using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Invoices;

/// <summary>
/// Persistence side of the Invoice Worker. The call to the provider happens between <see cref="ReadOrderAsync"/> and
/// <see cref="CompleteAsync"/>, in the worker, outside any transaction.
/// </summary>
public interface IInvoiceProcessingService : ITaskRequestProcessing
{
    /// <summary>The order of an invoice task with its lines and customer, read without a lock.</summary>
    Task<InvoiceOrder> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// One transaction: the invoice row, attempt and task Succeeded, and the alert of an earlier final failure resolved.
    /// An order that has its invoice already keeps it (the row is inserted only when none exists), and
    /// <paramref name="invoice"/> is null when the worker found the invoice stored and called nobody. When another
    /// delivery completed the task meanwhile, only the attempt is closed.
    /// </summary>
    Task CompleteAsync(Guid taskId, Guid attemptId, Invoice? invoice, CancellationToken cancellationToken);
}

/// <param name="PaidAt">Null while the order is unpaid.</param>
/// <param name="Lines">Ordered by SKU.</param>
/// <param name="HasInvoice">An invoice of the order is stored already (by an earlier delivery).</param>
public sealed record InvoiceOrder(
    Guid OrderId,
    string ExternalOrderId,
    string CustomerName,
    PaymentStatus PaymentStatus,
    DateTimeOffset? PaidAt,
    decimal TotalAmount,
    IReadOnlyList<InvoiceLine> Lines,
    bool HasInvoice);

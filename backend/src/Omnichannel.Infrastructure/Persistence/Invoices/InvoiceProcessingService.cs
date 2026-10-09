using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Invoices;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Persistence.Alerts;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Invoices;

/// <summary>
/// Persistence for the Invoice Worker: the order of an invoice task (read without a lock) and the completion that
/// stores the invoice. The provider is called between the two, in the worker, outside any transaction.
/// </summary>
internal sealed class InvoiceProcessingService(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    AlertStore alerts,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
    : TaskRequestProcessingService(dbContext, attempts, alerts, timeProvider, InvoiceEvents.MessageRejected), IInvoiceProcessingService
{
    private const string SystemActor = "system";

    public async Task<InvoiceOrder> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await DbContext.Orders
            .AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => new InvoiceOrder(
                o.OrderId,
                o.ExternalOrderId,
                o.Customer.Name,
                o.PaymentStatus,
                o.PaidAt,
                o.TotalAmount,
                // No line number is stored: SKU gives a stable order.
                o.Items
                    .OrderBy(i => i.Product.Sku)
                    .Select(i => new InvoiceLine(i.Product.Sku, i.Product.Name, i.Stock, i.UnitPrice))
                    .ToList(),
                DbContext.Invoices.Any(i => i.OrderId == o.OrderId)))
            .SingleAsync(cancellationToken);

    public Task CompleteAsync(Guid taskId, Guid attemptId, Invoice? invoice, CancellationToken cancellationToken) =>
        CompleteTaskAsync(
            taskId,
            attemptId,
            invoice is null ? null : (now, token) => StoreAsync(invoice, now, token),
            cancellationToken);

    /// <summary>
    /// One statement, and the order's own key decides: an invoice that another delivery stored meanwhile stays, and
    /// nothing fails in the transaction. Raw SQL bypasses the auditing interceptor, hence CreatedAt / CreatedBy.
    /// </summary>
    private async Task StoreAsync(Invoice invoice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var actor = currentUser.UserId ?? SystemActor;
        // Npgsql only writes UTC DateTimeOffset values to timestamptz.
        var issuedAt = invoice.IssuedAt.ToUniversalTime();
        await DbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Invoices" ("OrderId", "InvoiceNumber", "IssuedAt", "TotalAmount", "ProviderReference", "CreatedAt", "CreatedBy")
            VALUES ({invoice.OrderId}, {invoice.InvoiceNumber}, {issuedAt}, {invoice.TotalAmount}, {invoice.ProviderReference}, {now}, {actor})
            ON CONFLICT ("OrderId") DO NOTHING
            """,
            cancellationToken);
    }
}

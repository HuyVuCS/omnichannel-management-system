using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Notifications;
using Omnichannel.Infrastructure.Persistence.Alerts;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Notifications;

/// <summary>
/// Persistence for the Notification Worker: the order of an e-mail task with the customer as it is now, and the
/// completion of the task. The e-mail is sent between the two, in the worker, outside any transaction. Nothing of an
/// e-mail is stored besides the task and its attempts, and no invoice is read or written here.
/// </summary>
internal sealed class EmailProcessingService(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    AlertStore alerts,
    TimeProvider timeProvider)
    : TaskRequestProcessingService(dbContext, attempts, alerts, timeProvider, NotificationEvents.MessageRejected), IEmailProcessingService
{
    public async Task<PaidOrderNotice> ReadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await DbContext.Orders
            .AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => new PaidOrderNotice(
                o.OrderId,
                o.ExternalOrderId,
                o.Customer.Name,
                o.Customer.Email,
                o.PaymentStatus,
                o.PaidAt,
                o.TotalAmount,
                // No line number is stored: SKU gives a stable order.
                o.Items
                    .OrderBy(i => i.Product.Sku)
                    .Select(i => new PaidOrderLine(i.Product.Sku, i.Product.Name, i.Stock, i.UnitPrice))
                    .ToList()))
            .SingleAsync(cancellationToken);

    public Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken) =>
        CompleteTaskAsync(taskId, attemptId, storeResult: null, cancellationToken);
}

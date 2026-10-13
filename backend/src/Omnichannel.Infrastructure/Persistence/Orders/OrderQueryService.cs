using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// Orders for the dashboard. The list is one COUNT plus one page query (channel/customer joined, line count as a subquery);
/// the detail is a single SQL statement (lines with product, tasks with attempts, the payment, the invoice) plus - for a
/// paid order - the lookup of the e-mail address of who recorded the payment.
/// </summary>
internal sealed class OrderQueryService(OmnichannelDbContext dbContext) : IOrderQueryService
{
    public async Task<PagedResult<OrderSummaryResponse>> ListAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        var orders = OrderFilters.Apply(dbContext.Orders.AsNoTracking(), query.ToFilter());

        var totalCount = await orders.CountAsync(cancellationToken);

        IOrderedQueryable<Order> ordered;
        bool descending;
        if (query.IsSortedBy(OrderListQuery.SortByTotalAmount))
        {
            descending = query.IsDescending;
            ordered = orders.OrderBy(o => o.TotalAmount, descending);
        }
        else if (query.IsSortedBy(OrderListQuery.SortByExternalOrderId))
        {
            descending = query.IsDescending;
            ordered = orders.OrderBy(o => o.ExternalOrderId, descending);
        }
        else
        {
            // Newest first unless the client asks for ascending order explicitly.
            descending = query.SortDir is null || query.IsDescending;
            ordered = orders.OrderBy(o => o.CreatedAt, descending);
        }

        var items = await (descending ? ordered.ThenByDescending(o => o.OrderId) : ordered.ThenBy(o => o.OrderId))
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(o => new OrderSummaryResponse(
                o.OrderId,
                o.ExternalOrderId,
                o.Status,
                o.TotalAmount,
                o.Items.Count,
                new OrderChannelResponse(o.ChannelId, o.Channel.Slug, o.Channel.Name),
                new OrderCustomerResponse(o.CustomerId, o.Customer.Name),
                o.CreatedAt,
                o.PaymentStatus))
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderSummaryResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<OrderDetailResponse> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => new
            {
                o.PaidBy,
                Detail = new OrderDetailResponse(
                    o.OrderId,
                    o.ExternalOrderId,
                    o.Status,
                    o.TotalAmount,
                    o.Items.Count,
                    new OrderChannelResponse(o.ChannelId, o.Channel.Slug, o.Channel.Name),
                    new OrderCustomerResponse(o.CustomerId, o.Customer.Name),
                    o.CreatedAt,
                    // No line number is stored (UUIDv7 ids are not ordered within a millisecond): SKU gives a stable order.
                    o.Items
                        .OrderBy(i => i.Product.Sku)
                        .Select(i => new OrderItemResponse(
                            i.ProductId,
                            i.Product.Sku,
                            i.Product.Name,
                            i.Stock,
                            i.UnitPrice,
                            i.Stock * i.UnitPrice))
                        .ToList(),
                    dbContext.ProcessingTasks
                        .Where(t => t.OrderId == o.OrderId)
                        .OrderBy(t => t.CreatedAt)
                        .ThenBy(t => t.TaskId)
                        .Select(t => new OrderTaskResponse(
                            t.TaskId,
                            t.TaskType,
                            t.Status,
                            t.ChannelId,
                            t.ProductId,
                            t.RetryCount,
                            t.CorrelationId,
                            t.MessageId,
                            t.CreatedAt,
                            t.UpdatedAt,
                            t.Attempts.AsQueryable().OrderBy(a => a.AttemptNo).Select(ProcessingProjections.ToAttempt).ToList()))
                        .ToList(),
                    new OrderPaymentResponse(
                        o.PaymentStatus,
                        o.PaidAt,
                        null,
                        o.PaymentMethod,
                        o.PaymentReference),
                    dbContext.Invoices
                        .Where(i => i.OrderId == o.OrderId)
                        .Select(i => new OrderInvoiceResponse(i.InvoiceNumber, i.IssuedAt))
                        .FirstOrDefault())
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Order), orderId, OrderErrorCodes.OrderNotFound);

        // PaidBy is text, like the actor of a stock movement: looked up only when it is a user id.
        var emails = await dbContext.UserEmailsAsync([order.PaidBy], cancellationToken);
        return order.Detail with { Payment = order.Detail.Payment with { PaidByEmail = emails.EmailOf(order.PaidBy) } };
    }
}

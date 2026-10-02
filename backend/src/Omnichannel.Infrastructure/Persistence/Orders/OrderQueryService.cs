using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// Orders for the dashboard. The list is one COUNT plus one page query (channel/customer joined, line count as a subquery);
/// the detail is a single SQL statement (lines with product, tasks with attempts).
/// </summary>
internal sealed class OrderQueryService(OmnichannelDbContext dbContext) : IOrderQueryService
{
    public async Task<PagedResult<OrderSummaryResponse>> ListAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Order> orders = dbContext.Orders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Channel))
        {
            var slug = query.Channel.Trim().ToLowerInvariant();
            orders = orders.Where(o => o.Channel.Slug == slug);
        }

        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            orders = orders.Where(o => o.CreatedAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            orders = orders.Where(o => o.CreatedAt <= toUtc);
        }

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            orders = orders.Where(o =>
                EF.Functions.ILike(o.ExternalOrderId, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(o.Customer.Name, pattern, LikePattern.EscapeCharacter));
        }

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
                o.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderSummaryResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<OrderDetailResponse> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => new OrderDetailResponse(
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
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(Order), orderId, OrderErrorCodes.OrderNotFound);
}

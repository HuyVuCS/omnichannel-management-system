using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// The filters of the order list as one query fragment, used by the list (<c>GET /api/orders</c>) and by the orders
/// export: both select the same orders for the same filters.
/// </summary>
internal static class OrderFilters
{
    public static IQueryable<Order> Apply(IQueryable<Order> orders, OrderFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Channel))
        {
            var slug = filter.Channel.Trim().ToLowerInvariant();
            orders = orders.Where(o => o.Channel.Slug == slug);
        }

        if (filter.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (filter.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (filter.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            orders = orders.Where(o => o.CreatedAt >= fromUtc);
        }

        if (filter.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            orders = orders.Where(o => o.CreatedAt <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = LikePattern.Contains(filter.Search.Trim());
            orders = orders.Where(o =>
                EF.Functions.ILike(o.ExternalOrderId, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(o.Customer.Name, pattern, LikePattern.EscapeCharacter));
        }

        return orders;
    }
}

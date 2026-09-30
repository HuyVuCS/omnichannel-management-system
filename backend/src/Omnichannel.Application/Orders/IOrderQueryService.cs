using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Orders;

/// <summary>Read side of orders (doc §11): list and detail with processing history.</summary>
public interface IOrderQueryService
{
    Task<PagedResult<OrderSummaryResponse>> ListAsync(OrderListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException"><c>order_not_found</c>.</exception>
    Task<OrderDetailResponse> GetAsync(Guid orderId, CancellationToken cancellationToken);
}

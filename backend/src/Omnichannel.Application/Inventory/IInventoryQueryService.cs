using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Inventory;

/// <summary>Read side of inventory. Stock changes go through inventory movements only.</summary>
public interface IInventoryQueryService
{
    Task<PagedResult<InventoryItemResponse>> ListAsync(InventoryListQuery query, CancellationToken cancellationToken);
}

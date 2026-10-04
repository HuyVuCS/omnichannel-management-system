using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Inventory;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Inventories;

/// <summary>
/// The filters of the inventory list as one query fragment, used by the list (<c>GET /api/inventory</c>) and by the
/// inventory export: both select the same products for the same filters.
/// </summary>
internal static class InventoryFilters
{
    public static IQueryable<Inventory> Apply(IQueryable<Inventory> inventories, InventoryFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = LikePattern.Contains(filter.Search.Trim());
            inventories = inventories.Where(i =>
                EF.Functions.ILike(i.Product.Sku, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(i.Product.Name, pattern, LikePattern.EscapeCharacter));
        }

        if (filter.LowStockOnly)
        {
            inventories = inventories.Where(InventoryPredicates.IsLowStock);
        }

        return inventories;
    }
}

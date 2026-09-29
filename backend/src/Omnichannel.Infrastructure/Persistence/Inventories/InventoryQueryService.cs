using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Inventory;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Inventories;

// Namespace is "Inventories" (the table name): a namespace called "Inventory" under Persistence would
// shadow the Inventory entity for every class in Omnichannel.Infrastructure.Persistence.
internal sealed class InventoryQueryService(OmnichannelDbContext dbContext) : IInventoryQueryService
{
    private static readonly Expression<Func<Inventory, InventoryItemResponse>> ToItem =
        InventoryPredicates.Inline<Inventory, InventoryItemResponse>(
            (i, isLowStock) => new InventoryItemResponse(
                i.ProductId,
                i.Product.Sku,
                i.Product.Name,
                i.TotalStock,
                i.ReservedStock,
                i.AvailableStock,
                i.Product.LowStockThreshold,
                isLowStock,
                i.UpdatedAt ?? i.CreatedAt),
            InventoryPredicates.IsLowStock);

    public async Task<PagedResult<InventoryItemResponse>> ListAsync(InventoryListQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Inventory> inventories = dbContext.Inventories.AsNoTracking();

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            inventories = inventories.Where(i =>
                EF.Functions.ILike(i.Product.Sku, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(i.Product.Name, pattern, LikePattern.EscapeCharacter));
        }

        if (query.LowStockOnly)
        {
            inventories = inventories.Where(InventoryPredicates.IsLowStock);
        }

        var totalCount = await inventories.CountAsync(cancellationToken);

        IOrderedQueryable<Inventory> ordered;
        if (query.IsSortedBy(InventoryListQuery.SortByName))
        {
            ordered = inventories.OrderBy(i => i.Product.Name, query.IsDescending);
        }
        else if (query.IsSortedBy(InventoryListQuery.SortByAvailableStock))
        {
            ordered = inventories.OrderBy(i => i.AvailableStock, query.IsDescending);
        }
        else if (query.IsSortedBy(InventoryListQuery.SortByUpdatedAt))
        {
            ordered = inventories.OrderBy(i => i.UpdatedAt ?? i.CreatedAt, query.IsDescending);
        }
        else
        {
            ordered = inventories.OrderBy(i => i.Product.Sku, query.IsDescending);
        }

        var items = await ordered
            .ThenBy(i => i.ProductId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(ToItem)
            .ToListAsync(cancellationToken);

        return new PagedResult<InventoryItemResponse>(items, query.Page, query.PageSize, totalCount);
    }
}

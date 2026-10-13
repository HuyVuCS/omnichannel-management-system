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
        var inventories = InventoryFilters.Apply(dbContext.Inventories.AsNoTracking(), query.ToFilter());

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

    public async Task<PagedResult<InventoryMovementResponse>> ListMovementsAsync(
        InventoryMovementListQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryMovement> movements = dbContext.InventoryMovements.AsNoTracking();

        if (query.ProductId is { } productId)
        {
            movements = movements.Where(m => m.ProductId == productId);
        }

        if (query.Type is { } type)
        {
            movements = movements.Where(m => m.Type == type);
        }

        if (query.ReferenceType is { } referenceType)
        {
            movements = movements.Where(m => m.ReferenceType == referenceType);
        }

        if (!string.IsNullOrWhiteSpace(query.ReferenceId))
        {
            var referenceId = query.ReferenceId.Trim();
            movements = movements.Where(m => m.ReferenceId == referenceId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            movements = movements.Where(m => m.CreatedAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            movements = movements.Where(m => m.CreatedAt <= toUtc);
        }

        var totalCount = await movements.CountAsync(cancellationToken);

        var page = await movements
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.InventoryMovementId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(m => new InventoryMovementResponse(
                m.InventoryMovementId,
                m.ProductId,
                m.Product.Sku,
                m.Product.Name,
                m.Type,
                m.Stock,
                m.ReferenceType,
                m.ReferenceId,
                m.DocumentNumber,
                m.DocumentSeries,
                m.CreatedAt,
                m.CreatedBy,
                null))
            .ToListAsync(cancellationToken);

        // CreatedBy also holds "system:{worker}" and "webhook:{slug}": only the user ids of the page are looked up.
        var emails = await dbContext.UserEmailsAsync(page.Select(m => m.CreatedBy), cancellationToken);
        var items = page.Select(m => m with { CreatedByEmail = emails.EmailOf(m.CreatedBy) }).ToList();

        return new PagedResult<InventoryMovementResponse>(items, query.Page, query.PageSize, totalCount);
    }
}

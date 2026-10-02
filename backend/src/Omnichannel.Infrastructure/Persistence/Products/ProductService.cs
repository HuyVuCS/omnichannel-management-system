using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Persistence.Inventories;

namespace Omnichannel.Infrastructure.Persistence.Products;

internal sealed class ProductService(OmnichannelDbContext dbContext) : IProductService
{
    // Every product gets its inventory row on creation; the null branch only guards the LEFT JOIN.
    // IsLowStock is the shared rule (InventoryPredicates), inlined so it runs in SQL: there is no second definition.
    private static readonly Expression<Func<Product, ProductResponse>> ToResponse =
        InventoryPredicates.Inline<Product, ProductResponse>(
            (p, isLowStock) => new ProductResponse(
                p.ProductId,
                p.Sku,
                p.Name,
                p.Price,
                p.LowStockThreshold,
                p.ExpiryDate,
                p.Inventory == null ? 0 : p.Inventory.TotalStock,
                p.Inventory == null ? 0 : p.Inventory.ReservedStock,
                p.Inventory == null ? 0 : p.Inventory.AvailableStock,
                isLowStock,
                p.CreatedAt,
                p.UpdatedAt),
            InventoryPredicates.IsLowStockProduct);

    public async Task<PagedResult<ProductResponse>> ListAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        var products = dbContext.Products.AsNoTracking();

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            products = products.Where(p =>
                EF.Functions.ILike(p.Sku, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(p.Name, pattern, LikePattern.EscapeCharacter));
        }

        var totalCount = await products.CountAsync(cancellationToken);

        IOrderedQueryable<Product> ordered;
        if (query.IsSortedBy(ProductListQuery.SortByName))
        {
            ordered = products.OrderBy(p => p.Name, query.IsDescending);
        }
        else if (query.IsSortedBy(ProductListQuery.SortByPrice))
        {
            ordered = products.OrderBy(p => p.Price, query.IsDescending);
        }
        else if (query.IsSortedBy(ProductListQuery.SortByCreatedAt))
        {
            ordered = products.OrderBy(p => p.CreatedAt, query.IsDescending);
        }
        else
        {
            ordered = products.OrderBy(p => p.Sku, query.IsDescending);
        }

        var items = await ordered
            .ThenBy(p => p.ProductId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(ToResponse)
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public Task<ProductResponse> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        LoadAsync(productId, cancellationToken);

    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = new Product(request.Sku, request.Name, request.Price, request.LowStockThreshold, request.ExpiryDate);

        // One SaveChanges = one transaction: a product never exists without its inventory row.
        dbContext.Products.Add(product);
        dbContext.Inventories.Add(new Inventory(product.ProductId, totalStock: 0));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ProductSku))
        {
            throw new ConflictException(ProductErrorCodes.SkuExists, $"A product with SKU '{product.Sku}' already exists.");
        }

        return await LoadAsync(product.ProductId, cancellationToken);
    }

    public async Task<ProductResponse> UpdateAsync(Guid productId, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.SingleOrDefaultAsync(p => p.ProductId == productId, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);

        product.Update(request.Name, request.Price, request.LowStockThreshold, request.ExpiryDate);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await LoadAsync(productId, cancellationToken);
    }

    private async Task<ProductResponse> LoadAsync(Guid productId, CancellationToken cancellationToken) =>
        await dbContext.Products
            .AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(ToResponse)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(Product), productId);
}

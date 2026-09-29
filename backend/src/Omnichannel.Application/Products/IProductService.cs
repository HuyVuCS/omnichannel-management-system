using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Products;

public interface IProductService
{
    Task<PagedResult<ProductResponse>> ListAsync(ProductListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The product does not exist.</exception>
    Task<ProductResponse> GetAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Creates the product and its inventory row (all zero) atomically.</summary>
    /// <exception cref="Common.Exceptions.ConflictException">The SKU is already used (<see cref="ProductErrorCodes.SkuExists"/>).</exception>
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The product does not exist.</exception>
    Task<ProductResponse> UpdateAsync(Guid productId, UpdateProductRequest request, CancellationToken cancellationToken);
}

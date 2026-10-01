using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Products;

/// <summary>Product with its current stock. Used by the list (order-entry UI) and the detail endpoint.</summary>
/// <param name="IsLowStock">
/// The low-stock rule evaluated by the server (<c>AvailableStock &lt;= LowStockThreshold</c>, the same expression as the
/// inventory list and the alerts). Clients show it; they never re-implement the rule.
/// </param>
public sealed record ProductResponse(
    Guid ProductId,
    string Sku,
    string Name,
    decimal Price,
    int LowStockThreshold,
    DateOnly? ExpiryDate,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    bool IsLowStock,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Stock is never set here: a new product starts with an empty inventory row.</summary>
public sealed record CreateProductRequest(
    string Sku,
    string Name,
    decimal Price,
    int LowStockThreshold,
    DateOnly? ExpiryDate);

/// <summary>SKU is immutable.</summary>
public sealed record UpdateProductRequest(string Name, decimal Price, int LowStockThreshold, DateOnly? ExpiryDate);

/// <summary>Search matches SKU or name.</summary>
public sealed record ProductListQuery : PageQuery
{
    public const string SortBySku = "sku";
    public const string SortByName = "name";
    public const string SortByPrice = "price";
    public const string SortByCreatedAt = "createdAt";

    public static readonly IReadOnlyList<string> SortFields = [SortBySku, SortByName, SortByPrice, SortByCreatedAt];
}

public static class ProductErrorCodes
{
    public const string SkuExists = "product_sku_exists";
}

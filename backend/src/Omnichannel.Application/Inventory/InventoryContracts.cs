using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Inventory;

/// <param name="IsLowStock">See <see cref="InventoryListQuery.LowStockOnly"/> for the rule.</param>
/// <param name="UpdatedAt">Last change of the inventory row (its creation time until the first change).</param>
public sealed record InventoryItemResponse(
    Guid ProductId,
    string Sku,
    string Name,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    int LowStockThreshold,
    bool IsLowStock,
    DateTimeOffset UpdatedAt);

/// <summary>Search matches SKU or product name.</summary>
public sealed record InventoryListQuery : PageQuery
{
    public const string SortBySku = "sku";
    public const string SortByName = "name";
    public const string SortByAvailableStock = "availableStock";
    public const string SortByUpdatedAt = "updatedAt";

    public static readonly IReadOnlyList<string> SortFields =
        [SortBySku, SortByName, SortByAvailableStock, SortByUpdatedAt];

    /// <summary>
    /// Only products that are low on stock: <c>LowStockThreshold &gt; 0 AND AvailableStock &lt;= LowStockThreshold</c>.
    /// A threshold of 0 turns the low-stock warning off for that product.
    /// </summary>
    public bool LowStockOnly { get; init; }
}

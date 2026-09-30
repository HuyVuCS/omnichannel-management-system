namespace Omnichannel.Application.Inventory;

/// <summary>Goods-receipt document. Applied at most once: the same document (series + number) cannot be re-imported.</summary>
public sealed record ImportInventoryRequest(
    string DocumentNumber,
    string? DocumentSeries,
    IReadOnlyList<ImportInventoryItem> Items);

public sealed record ImportInventoryItem(Guid ProductId, int Stock);

/// <param name="ReferenceId"><c>{documentSeries}/{documentNumber}</c>, or just the number without a series.</param>
/// <param name="Items">In request order.</param>
public sealed record ImportInventoryResponse(string ReferenceId, IReadOnlyList<ImportedInventoryItem> Items);

/// <summary>Stock of the product right after this import.</summary>
public sealed record ImportedInventoryItem(
    Guid ProductId,
    Guid MovementId,
    int TotalStock,
    int ReservedStock,
    int AvailableStock);

/// <summary>SystemEventLog event types written by inventory operations (audit, doc §6).</summary>
public static class InventoryEvents
{
    /// <summary>A goods-receipt document was applied. Payload: <c>{referenceId, itemCount, totalQuantity}</c>.</summary>
    public const string Imported = "inventory.imported";
}

public static class InventoryErrorCodes
{
    public const string ImportAlreadyApplied = "stock_import_already_applied";

    /// <summary>The import would push TotalStock beyond the column's maximum (PostgreSQL integer).</summary>
    public const string StockLimitExceeded = "stock_limit_exceeded";
}

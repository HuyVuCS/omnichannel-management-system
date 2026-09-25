namespace Omnichannel.Domain.Entities;

/// <summary>
/// Current stock of a product. 1:1 with <see cref="Product"/>; ProductId is both PK and FK
/// (there is intentionally no InventoryId).
/// </summary>
/// <remarks>
/// Invariant: AvailableStock = TotalStock - ReservedStock, 0 &lt;= ReservedStock &lt;= TotalStock.
/// AvailableStock is a PostgreSQL STORED generated column and CHECK constraints enforce the bounds,
/// so the invariant holds even for set-based SQL updates (used for atomic reservation).
/// </remarks>
public class Inventory : AuditableEntity
{
    private Inventory()
    {
    }

    public Inventory(Guid productId, int totalStock)
    {
        if (productId == Guid.Empty) throw new ArgumentException("ProductId is required.", nameof(productId));
        ArgumentOutOfRangeException.ThrowIfNegative(totalStock);

        ProductId = productId;
        TotalStock = totalStock;
        ReservedStock = 0;
        AvailableStock = totalStock;
    }

    public Guid ProductId { get; private set; }
    public int TotalStock { get; private set; }
    public int ReservedStock { get; private set; }

    /// <summary>Computed by the database; read-only for the application.</summary>
    public int AvailableStock { get; private set; }

    /// <summary>Optimistic concurrency token mapped to PostgreSQL's xmin system column.</summary>
    public uint RowVersion { get; private set; }

    public Product Product { get; private set; } = null!;
}

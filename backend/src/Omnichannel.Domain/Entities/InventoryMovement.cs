using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>Append-only history of every change to <see cref="Inventory"/>.</summary>
public class InventoryMovement : AuditableEntity
{
    private InventoryMovement()
    {
    }

    public InventoryMovement(
        Guid productId,
        InventoryMovementType type,
        int stock,
        InventoryReferenceType referenceType,
        string? referenceId,
        string? documentNumber,
        string? documentSeries)
    {
        if (productId == Guid.Empty) throw new ArgumentException("ProductId is required.", nameof(productId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stock);

        InventoryMovementId = Guid.CreateVersion7();
        ProductId = productId;
        Type = type;
        Stock = stock;
        ReferenceType = referenceType;
        ReferenceId = referenceId?.Trim();
        DocumentNumber = documentNumber?.Trim();
        DocumentSeries = documentSeries?.Trim();
    }

    public Guid InventoryMovementId { get; private set; }
    public Guid ProductId { get; private set; }
    public InventoryMovementType Type { get; private set; }

    /// <summary>Quantity moved, always positive; direction is given by <see cref="Type"/>.</summary>
    public int Stock { get; private set; }

    public InventoryReferenceType ReferenceType { get; private set; }
    public string? ReferenceId { get; private set; }
    public string? DocumentNumber { get; private set; }
    public string? DocumentSeries { get; private set; }

    public Product Product { get; private set; } = null!;
}

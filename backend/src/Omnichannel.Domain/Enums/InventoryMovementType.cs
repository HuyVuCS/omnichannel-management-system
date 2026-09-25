namespace Omnichannel.Domain.Enums;

public enum InventoryMovementType
{
    /// <summary>Goods received: TotalStock increases.</summary>
    Import,
    /// <summary>Stock held for an order: ReservedStock increases.</summary>
    Reserve,
    /// <summary>Reservation cancelled: ReservedStock decreases.</summary>
    Release,
    /// <summary>Reserved goods leave the warehouse: TotalStock and ReservedStock decrease.</summary>
    Deduct,
    /// <summary>Manual correction of TotalStock.</summary>
    Adjustment
}

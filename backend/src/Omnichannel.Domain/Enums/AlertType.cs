namespace Omnichannel.Domain.Enums;

public enum AlertType
{
    /// <summary>A product's available stock is at or below its low-stock threshold.</summary>
    LowStock,
    /// <summary>An order was stored as <see cref="OrderStatus.InsufficientStock"/> because of this product.</summary>
    InsufficientStock,
    /// <summary>A stock-sync task of this channel and product failed for good.</summary>
    StockSyncFailed,
    /// <summary>A task of another type failed for good.</summary>
    ProcessingFailed
}

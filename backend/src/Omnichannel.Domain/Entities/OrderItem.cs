namespace Omnichannel.Domain.Entities;

public class OrderItem : AuditableEntity
{
    private OrderItem()
    {
    }

    internal OrderItem(Guid orderId, Guid productId, int stock, decimal unitPrice)
    {
        if (productId == Guid.Empty) throw new ArgumentException("ProductId is required.", nameof(productId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stock);
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);

        OrderItemId = Guid.CreateVersion7();
        OrderId = orderId;
        ProductId = productId;
        Stock = stock;
        UnitPrice = unitPrice;
    }

    public Guid OrderItemId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }

    /// <summary>Ordered quantity (named "Stock" per the project data dictionary).</summary>
    public int Stock { get; private set; }

    public decimal UnitPrice { get; private set; }

    public Product Product { get; private set; } = null!;
}

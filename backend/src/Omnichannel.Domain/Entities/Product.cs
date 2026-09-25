namespace Omnichannel.Domain.Entities;

public class Product : AuditableEntity
{
    private Product()
    {
    }

    public Product(string sku, string name, decimal price, int lowStockThreshold, DateOnly? expiryDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        ArgumentOutOfRangeException.ThrowIfNegative(lowStockThreshold);

        ProductId = Guid.CreateVersion7();
        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Price = price;
        LowStockThreshold = lowStockThreshold;
        ExpiryDate = expiryDate;
    }

    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }
    public int LowStockThreshold { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    public Inventory? Inventory { get; private set; }
}

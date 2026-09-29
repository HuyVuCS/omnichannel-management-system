namespace Omnichannel.Domain.Entities;

public class Product : AuditableEntity
{
    private Product()
    {
    }

    public Product(string sku, string name, decimal price, int lowStockThreshold, DateOnly? expiryDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);

        ProductId = Guid.CreateVersion7();
        Sku = NormalizeSku(sku);
        Update(name, price, lowStockThreshold, expiryDate);
    }

    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }
    public int LowStockThreshold { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    public Inventory? Inventory { get; private set; }

    /// <summary>SKU is immutable; stock is changed only through inventory movements.</summary>
    public void Update(string name, decimal price, int lowStockThreshold, DateOnly? expiryDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        ArgumentOutOfRangeException.ThrowIfNegative(lowStockThreshold);

        Name = name.Trim();
        Price = price;
        LowStockThreshold = lowStockThreshold;
        ExpiryDate = expiryDate;
    }

    /// <summary>SKUs are case-insensitive: stored trimmed and upper-case so the unique index catches "abc" vs "ABC".</summary>
    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();
}

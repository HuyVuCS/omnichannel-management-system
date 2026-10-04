using System.Text.RegularExpressions;

namespace Omnichannel.Domain.Entities;

public class Product : AuditableEntity
{
    private Product()
    {
    }

    public const int MaxSkuLength = 64;

    /// <summary><c>products/{productId}/{objectId}</c>: 9 characters and two GUIDs of 36 with a slash between them.</summary>
    public const int MaxImageKeyLength = 82;

    public const int MaxImageContentTypeLength = 32;

    /// <summary>Format of a normalised SKU (after <see cref="NormalizeSku"/>). SKUs are sent to channel APIs and exports.</summary>
    public const string SkuPattern = "^[A-Z0-9][A-Z0-9._-]*$";

    private static readonly Regex SkuRegex = new(SkuPattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Product(string sku, string name, decimal price, int lowStockThreshold, DateOnly? expiryDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        var normalizedSku = NormalizeSku(sku);
        if (!IsValidSku(normalizedSku))
        {
            throw new ArgumentException($"SKU must match {SkuPattern} and be at most {MaxSkuLength} characters.", nameof(sku));
        }

        ProductId = Guid.CreateVersion7();
        Sku = normalizedSku;
        Update(name, price, lowStockThreshold, expiryDate);
    }

    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }
    public int LowStockThreshold { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    /// <summary>
    /// The product's image: the key of its object in the object storage and what was checked when the upload was
    /// confirmed. The image itself is never stored here. All four are null (no image) or all four are set.
    /// </summary>
    public string? ImageKey { get; private set; }
    public string? ImageContentType { get; private set; }
    public long? ImageSizeBytes { get; private set; }
    public DateTimeOffset? ImageUpdatedAt { get; private set; }

    public Inventory? Inventory { get; private set; }

    /// <summary>The object that is the product's image from now on. The caller has checked the object itself.</summary>
    public void SetImage(string key, string contentType, long sizeBytes, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, MaxImageKeyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(contentType.Length, MaxImageContentTypeLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        ImageKey = key;
        ImageContentType = contentType;
        ImageSizeBytes = sizeBytes;
        ImageUpdatedAt = updatedAt;
    }

    public void ClearImage()
    {
        ImageKey = null;
        ImageContentType = null;
        ImageSizeBytes = null;
        ImageUpdatedAt = null;
    }

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

    /// <summary>True when an already normalised SKU has the allowed format and length.</summary>
    public static bool IsValidSku(string normalizedSku) =>
        normalizedSku.Length <= MaxSkuLength && SkuRegex.IsMatch(normalizedSku);
}

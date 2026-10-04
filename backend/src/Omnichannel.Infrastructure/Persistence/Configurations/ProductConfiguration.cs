using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Products_Price_NonNegative", "\"Price\" >= 0");
            t.HasCheckConstraint("CK_Products_LowStockThreshold_NonNegative", "\"LowStockThreshold\" >= 0");
            // An image is its key and what was checked at the confirmation, or nothing at all.
            // (One line each: a line break inside the SQL would depend on the line endings of this file.)
            t.HasCheckConstraint(
                "CK_Products_Image_AllOrNone",
                """("ImageKey" IS NULL AND "ImageContentType" IS NULL AND "ImageSizeBytes" IS NULL AND "ImageUpdatedAt" IS NULL) """
                + """OR ("ImageKey" IS NOT NULL AND "ImageContentType" IS NOT NULL AND "ImageSizeBytes" IS NOT NULL AND "ImageUpdatedAt" IS NOT NULL)""");
        });

        builder.HasKey(x => x.ProductId);
        builder.Property(x => x.ProductId).ValueGeneratedNever();

        builder.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Price)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        builder.Property(x => x.ExpiryDate).HasColumnType("date");
        // No index: an image is only ever read with its product.
        builder.Property(x => x.ImageKey).HasMaxLength(Product.MaxImageKeyLength);
        builder.Property(x => x.ImageContentType).HasMaxLength(Product.MaxImageContentTypeLength);
        builder.ConfigureAuditColumns();

        builder.HasIndex(x => x.Sku).IsUnique().HasDatabaseName(ExpectedUniqueViolations.ProductSku);
    }
}

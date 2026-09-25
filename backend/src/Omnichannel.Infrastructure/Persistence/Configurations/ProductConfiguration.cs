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
        });

        builder.HasKey(x => x.ProductId);
        builder.Property(x => x.ProductId).ValueGeneratedNever();

        builder.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Price)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        builder.Property(x => x.ExpiryDate).HasColumnType("date");
        builder.ConfigureAuditColumns();

        builder.HasIndex(x => x.Sku).IsUnique();
    }
}

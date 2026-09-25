using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", t =>
        {
            t.HasCheckConstraint("CK_OrderItems_Stock_Positive", "\"Stock\" > 0");
            t.HasCheckConstraint("CK_OrderItems_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
        });

        builder.HasKey(x => x.OrderItemId);
        builder.Property(x => x.OrderItemId).ValueGeneratedNever();

        builder.Property(x => x.UnitPrice)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Lines are normalised per product, so one order reserves a product at most once.
        builder.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique();
        builder.HasIndex(x => x.ProductId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public const string IdempotencyIndexName = "UX_Orders_ChannelId_ExternalOrderId";

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", t =>
            t.HasCheckConstraint("CK_Orders_TotalAmount_NonNegative", "\"TotalAmount\" >= 0"));

        builder.HasKey(x => x.OrderId);
        builder.Property(x => x.OrderId).ValueGeneratedNever();

        builder.Property(x => x.ExternalOrderId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.TotalAmount)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Customer)
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Idempotency: the same external order from the same channel is stored once.
        builder.HasIndex(x => new { x.ChannelId, x.ExternalOrderId })
            .IsUnique()
            .HasDatabaseName(IdempotencyIndexName);
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
        builder.HasIndex(x => x.CreatedAt);
    }
}

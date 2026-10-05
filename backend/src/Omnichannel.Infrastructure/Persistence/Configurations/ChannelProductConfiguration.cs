using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ChannelProductConfiguration : IEntityTypeConfiguration<ChannelProduct>
{
    public void Configure(EntityTypeBuilder<ChannelProduct> builder)
    {
        builder.ToTable("ChannelProducts");
        builder.HasKey(x => x.ChannelProductId);
        builder.Property(x => x.ChannelProductId).ValueGeneratedNever();

        builder.Property(x => x.ExternalProductId).HasMaxLength(ChannelProduct.MaxExternalIdLength);
        builder.Property(x => x.ExternalSku).HasMaxLength(ChannelProduct.MaxExternalIdLength);
        builder.Property(x => x.Status).HasEnumAsString();
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // A product is mapped at most once per channel, and an external id identifies one product: the external product
        // id, or - for a mapping without one - the external SKU (the key a channel knows the product by,
        // SimulatedChannelStock.ExternalKeyOf). Inactive mappings count for both.
        builder.HasIndex(x => new { x.ChannelId, x.ProductId })
            .IsUnique()
            .HasDatabaseName(ExpectedUniqueViolations.ChannelProductMapping);
        builder.HasIndex(x => new { x.ChannelId, x.ExternalProductId })
            .IsUnique()
            .HasFilter("\"ExternalProductId\" IS NOT NULL")
            .HasDatabaseName(ExpectedUniqueViolations.ChannelProductExternalProductId);
        builder.HasIndex(x => new { x.ChannelId, x.ExternalSku })
            .IsUnique()
            .HasFilter("\"ExternalProductId\" IS NULL")
            .HasDatabaseName(ExpectedUniqueViolations.ChannelProductExternalSku);
        builder.HasIndex(x => x.ProductId);
    }
}

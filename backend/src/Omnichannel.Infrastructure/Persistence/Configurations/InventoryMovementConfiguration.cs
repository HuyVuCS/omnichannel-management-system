using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public const string ReferenceIndexName = "UX_InventoryMovements_Reference";

    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.ToTable("InventoryMovements", t =>
            t.HasCheckConstraint("CK_InventoryMovements_Stock_Positive", "\"Stock\" > 0"));

        builder.HasKey(x => x.InventoryMovementId);
        builder.Property(x => x.InventoryMovementId).ValueGeneratedNever();

        builder.Property(x => x.Type).HasEnumAsString();
        builder.Property(x => x.ReferenceType).HasEnumAsString();
        builder.Property(x => x.ReferenceId).HasMaxLength(128);
        builder.Property(x => x.DocumentNumber).HasMaxLength(64);
        builder.Property(x => x.DocumentSeries).HasMaxLength(32);
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductId, x.CreatedAt });
        builder.HasIndex(x => new { x.ReferenceType, x.ReferenceId });

        // Idempotency: a document can import/reserve/release a product only once. Stops a re-posted goods receipt
        // and, as defence in depth, a redelivered order message that slips past the order-level check.
        builder.HasIndex(x => new { x.ReferenceType, x.ReferenceId, x.ProductId, x.Type })
            .IsUnique()
            .HasFilter("\"ReferenceId\" IS NOT NULL AND \"Type\" IN ('Import', 'Reserve', 'Release')")
            .HasDatabaseName(ReferenceIndexName);
    }
}

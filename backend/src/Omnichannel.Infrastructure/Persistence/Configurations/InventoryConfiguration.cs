using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class InventoryConfiguration : IEntityTypeConfiguration<Inventory>
{
    public void Configure(EntityTypeBuilder<Inventory> builder)
    {
        // The CHECKs make oversell impossible at the database level: a reservation that would
        // push ReservedStock above TotalStock fails regardless of application code.
        builder.ToTable("Inventories", t =>
        {
            t.HasCheckConstraint("CK_Inventories_TotalStock_NonNegative", "\"TotalStock\" >= 0");
            t.HasCheckConstraint(
                "CK_Inventories_ReservedStock_Range",
                "\"ReservedStock\" >= 0 AND \"ReservedStock\" <= \"TotalStock\"");
        });

        // ProductId is PK and FK; there is intentionally no InventoryId.
        builder.HasKey(x => x.ProductId);
        builder.Property(x => x.ProductId).ValueGeneratedNever();

        builder.Property(x => x.TotalStock).IsRequired();
        builder.Property(x => x.ReservedStock).IsRequired();
        builder.Property(x => x.AvailableStock)
            .HasComputedColumnSql("\"TotalStock\" - \"ReservedStock\"", stored: true);

        // Maps to the PostgreSQL xmin system column (no physical column is created).
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Product)
            .WithOne(p => p.Inventory)
            .HasForeignKey<Inventory>(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

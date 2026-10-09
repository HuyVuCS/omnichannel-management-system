using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices", t =>
            t.HasCheckConstraint("CK_Invoices_TotalAmount_NonNegative", "\"TotalAmount\" >= 0"));

        // 1:1 with its order: the order id is the key, there is no separate id. The key is also what makes a second
        // invoice of one order impossible (the Invoice Worker inserts with ON CONFLICT on it).
        builder.HasKey(x => x.OrderId);
        builder.Property(x => x.OrderId).ValueGeneratedNever();

        builder.Property(x => x.InvoiceNumber).HasMaxLength(Invoice.MaxInvoiceNumberLength).IsRequired();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.TotalAmount)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        builder.Property(x => x.ProviderReference).HasMaxLength(Invoice.MaxProviderReferenceLength).IsRequired();
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Order)
            .WithOne()
            .HasForeignKey<Invoice>(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.InvoiceNumber).IsUnique();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", t =>
        {
            t.HasCheckConstraint("CK_Orders_TotalAmount_NonNegative", "\"TotalAmount\" >= 0");

            // Unpaid = no payment field at all; Paid = when, by whom and how (the reference is optional).
            // (One line each: a line break inside the SQL would depend on the line endings of this file.)
            t.HasCheckConstraint(
                "CK_Orders_Payment_Consistent",
                """("PaymentStatus" = 'Unpaid' AND "PaidAt" IS NULL AND "PaidBy" IS NULL AND "PaymentMethod" IS NULL AND "PaymentReference" IS NULL) """
                + """OR ("PaymentStatus" = 'Paid' AND "PaidAt" IS NOT NULL AND "PaidBy" IS NOT NULL AND "PaymentMethod" IS NOT NULL)""");
        });

        builder.HasKey(x => x.OrderId);
        builder.Property(x => x.OrderId).ValueGeneratedNever();

        builder.Property(x => x.ExternalOrderId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.TotalAmount)
            .HasPrecision(ConfigurationExtensions.MoneyPrecision, ConfigurationExtensions.MoneyScale);
        // The default fills the rows that existed before payments did, and every order the Order Worker stores.
        builder.Property(x => x.PaymentStatus).HasEnumAsString().HasDefaultValue(PaymentStatus.Unpaid);
        builder.Property(x => x.PaidBy).HasMaxLength(ConfigurationExtensions.ActorMaxLength);
        builder.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(ConfigurationExtensions.EnumMaxLength);
        builder.Property(x => x.PaymentReference).HasMaxLength(Order.MaxPaymentReferenceLength);
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
            .HasDatabaseName(ExpectedUniqueViolations.OrderIdempotency);
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
        builder.HasIndex(x => x.CreatedAt);
    }
}

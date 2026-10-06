using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ChannelCustomerConfiguration : IEntityTypeConfiguration<ChannelCustomer>
{
    public void Configure(EntityTypeBuilder<ChannelCustomer> builder)
    {
        builder.ToTable("ChannelCustomers");
        builder.HasKey(x => x.ChannelCustomerId);
        builder.Property(x => x.ChannelCustomerId).ValueGeneratedNever();

        builder.Property(x => x.ExternalCustomerId).HasMaxLength(ChannelCustomer.MaxExternalCustomerIdLength).IsRequired();
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Customer)
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // An external customer id names one customer per channel: the guard of "created on first sight" when two
        // webhooks of the same new customer arrive together.
        builder.HasIndex(x => new { x.ChannelId, x.ExternalCustomerId })
            .IsUnique()
            .HasDatabaseName(ExpectedUniqueViolations.ChannelCustomerExternalId);
        builder.HasIndex(x => x.CustomerId);
    }
}

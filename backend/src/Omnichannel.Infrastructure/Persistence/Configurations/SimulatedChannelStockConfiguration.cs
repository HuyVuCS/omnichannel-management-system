using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Infrastructure.ExternalChannels;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

/// <summary>
/// The table of the simulated channels' state. Deliberately no foreign key and no audit columns: the rows stand for data
/// that lives in an external system.
/// </summary>
internal sealed class SimulatedChannelStockConfiguration : IEntityTypeConfiguration<SimulatedChannelStock>
{
    public void Configure(EntityTypeBuilder<SimulatedChannelStock> builder)
    {
        builder.ToTable("SimulatedChannelStocks", t =>
            t.HasCheckConstraint("CK_SimulatedChannelStocks_AvailableStock_NonNegative", "\"AvailableStock\" >= 0"));

        builder.HasKey(x => new { x.ChannelSlug, x.ExternalKey });
        builder.Property(x => x.ChannelSlug).HasMaxLength(SimulatedChannelStock.MaxChannelSlugLength);
        builder.Property(x => x.ExternalKey).HasMaxLength(SimulatedChannelStock.MaxExternalKeyLength);
        builder.Property(x => x.AvailableStock).IsRequired();
        builder.Property(x => x.LastRequestId).HasMaxLength(SimulatedChannelStock.MaxRequestIdLength).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("Channels");
        builder.HasKey(x => x.ChannelId);
        builder.Property(x => x.ChannelId).ValueGeneratedNever();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Type).HasEnumAsString();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.ConfigureAuditColumns();

        builder.HasIndex(x => x.Slug).IsUnique().HasDatabaseName(ExpectedUniqueViolations.ChannelSlug);

        builder.HasData(SeedData.Channels);
    }
}

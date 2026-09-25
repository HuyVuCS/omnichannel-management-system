using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class SystemEventLogConfiguration : IEntityTypeConfiguration<SystemEventLog>
{
    public void Configure(EntityTypeBuilder<SystemEventLog> builder)
    {
        builder.ToTable("SystemEventLogs");
        builder.HasKey(x => x.LogId);
        builder.Property(x => x.LogId).UseIdentityAlwaysColumn();

        builder.Property(x => x.Level).HasEnumAsString();
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(ConfigurationExtensions.CorrelationIdMaxLength);
        builder.Property(x => x.WorkerName).HasMaxLength(100);
        builder.Property(x => x.Payload).HasColumnType("jsonb");

        // No FKs by design (see entity docs).
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.TaskId);
        builder.HasIndex(x => new { x.EventType, x.CreatedAt });
    }
}

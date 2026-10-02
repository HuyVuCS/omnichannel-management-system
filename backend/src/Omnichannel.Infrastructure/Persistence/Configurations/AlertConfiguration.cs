using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    /// <summary>
    /// At most one unresolved alert per key. Never hit as an error: every insert names it as its
    /// <c>ON CONFLICT</c> arbiter, so it is not an <see cref="ExpectedUniqueViolations"/> entry.
    /// </summary>
    public const string UnresolvedDedupKeyIndex = "UX_Alerts_DedupKey_Unresolved";

    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("Alerts", t =>
        {
            t.HasCheckConstraint("CK_Alerts_OccurrenceCount_Positive", "\"OccurrenceCount\" >= 1");
            t.HasCheckConstraint("CK_Alerts_Resolved_ResolvedAt", "(\"Status\" = 'Resolved') = (\"ResolvedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Alerts_Acknowledged_By", "(\"AcknowledgedAt\" IS NULL) = (\"AcknowledgedBy\" IS NULL)");
        });

        builder.HasKey(x => x.AlertId);
        builder.Property(x => x.AlertId).ValueGeneratedNever();

        builder.Property(x => x.Type).HasEnumAsString();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.DedupKey).HasMaxLength(Alert.MaxDedupKeyLength).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(Alert.MaxErrorCodeLength);
        builder.Property(x => x.AcknowledgedBy).HasMaxLength(ConfigurationExtensions.ActorMaxLength);

        // Nothing an alert refers to is ever deleted.
        builder.HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProcessingTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DedupKey)
            .IsUnique()
            .HasFilter("\"Status\" <> 'Resolved'")
            .HasDatabaseName(UnresolvedDedupKeyIndex);

        // The list's default filter and order: unresolved alerts, most recent first.
        builder.HasIndex(x => new { x.Status, x.LastOccurredAt });
        builder.HasIndex(x => x.ProductId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("OutboxEvents");
        builder.HasKey(x => x.OutboxEventId);
        builder.Property(x => x.OutboxEventId).ValueGeneratedNever();

        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.AggregateId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CorrelationId)
            .HasMaxLength(ConfigurationExtensions.CorrelationIdMaxLength)
            .IsRequired();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.PublishAttempts).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(OutboxEvent.MaxLastErrorLength);
        builder.Property(x => x.CreatedAt).IsRequired();

        // INVENTORY_MOVEMENT 1:0..1 OUTBOX_EVENT (unique FK index is created by WithOne).
        builder.HasOne(x => x.InventoryMovement)
            .WithOne()
            .HasForeignKey<OutboxEvent>(x => x.InventoryMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        // Small partial index the publisher polls: only rows still waiting to be published, in FIFO (CreatedAt) order.
        // NextAttemptAt is included so rows in backoff are skipped from the index without visiting the table.
        builder.HasIndex(x => x.CreatedAt)
            .HasFilter("\"Status\" = 'Pending'")
            .IncludeProperties(x => x.NextAttemptAt)
            .HasDatabaseName("IX_OutboxEvents_Pending_CreatedAt");
    }
}

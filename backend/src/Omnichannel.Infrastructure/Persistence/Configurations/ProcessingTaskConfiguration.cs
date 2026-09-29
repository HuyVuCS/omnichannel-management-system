using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ProcessingTaskConfiguration : IEntityTypeConfiguration<ProcessingTask>
{
    public const string IdempotencyKeyIndexName = "UX_ProcessingTasks_IdempotencyKey";

    public void Configure(EntityTypeBuilder<ProcessingTask> builder)
    {
        builder.ToTable("ProcessingTasks", t =>
            t.HasCheckConstraint("CK_ProcessingTasks_RetryCount_NonNegative", "\"RetryCount\" >= 0"));

        builder.HasKey(x => x.TaskId);
        builder.Property(x => x.TaskId).ValueGeneratedNever();

        builder.Property(x => x.TaskType).HasEnumAsString();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.CorrelationId)
            .HasMaxLength(ConfigurationExtensions.CorrelationIdMaxLength)
            .IsRequired();
        builder.Property(x => x.MessageId).HasMaxLength(100);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(ProcessingTask.MaxIdempotencyKeyLength);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Attempt history must survive; tasks are never hard-deleted while attempts exist.
        builder.HasMany(x => x.Attempts)
            .WithOne(a => a.ProcessingTask)
            .HasForeignKey(a => a.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => new { x.TaskType, x.Status, x.CreatedAt });

        // Intake idempotency: the same external order (key) can create only one task, before any Order exists.
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName(IdempotencyKeyIndexName);
    }
}

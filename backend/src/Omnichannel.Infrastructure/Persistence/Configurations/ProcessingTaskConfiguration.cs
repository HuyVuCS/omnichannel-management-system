using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ProcessingTaskConfiguration : IEntityTypeConfiguration<ProcessingTask>
{
    public void Configure(EntityTypeBuilder<ProcessingTask> builder)
    {
        builder.ToTable("ProcessingTasks", t =>
        {
            t.HasCheckConstraint("CK_ProcessingTasks_RetryCount_NonNegative", "\"RetryCount\" >= 0");
            t.HasCheckConstraint("CK_ProcessingTasks_AttemptBudgetStart_NonNegative", "\"AttemptBudgetStart\" >= 0");
            // Stock sync is per channel and product (ProcessingTask.ForStockSync); other task types leave both null.
            t.HasCheckConstraint(
                "CK_ProcessingTasks_StockSync_ChannelAndProduct",
                "\"TaskType\" <> 'StockSync' OR (\"ChannelId\" IS NOT NULL AND \"ProductId\" IS NOT NULL)");
        });

        builder.HasKey(x => x.TaskId);
        builder.Property(x => x.TaskId).ValueGeneratedNever();

        builder.Property(x => x.TaskType).HasEnumAsString();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.CorrelationId)
            .HasMaxLength(ConfigurationExtensions.CorrelationIdMaxLength)
            .IsRequired();
        builder.Property(x => x.MessageId).HasMaxLength(100);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(ProcessingTask.MaxIdempotencyKeyLength);
        builder.Property(x => x.RequestHash).HasMaxLength(ProcessingTask.RequestHashLength);
        builder.Property(x => x.AttemptBudgetStart).HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasOne(x => x.Order)
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Channel>()
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Attempt history must survive; tasks are never hard-deleted while attempts exist.
        builder.HasMany(x => x.Attempts)
            .WithOne(a => a.ProcessingTask)
            .HasForeignKey(a => a.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.ChannelId);
        builder.HasIndex(x => x.ProductId);
        builder.HasIndex(x => x.CorrelationId);
        builder.HasIndex(x => new { x.TaskType, x.Status, x.CreatedAt });

        // Intake idempotency: the same external order (key) can create only one task, before any Order exists.
        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName(ExpectedUniqueViolations.ProcessingTaskIdempotencyKey);
    }
}

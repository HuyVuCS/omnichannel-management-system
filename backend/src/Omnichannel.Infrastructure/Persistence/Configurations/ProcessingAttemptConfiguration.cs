using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ProcessingAttemptConfiguration : IEntityTypeConfiguration<ProcessingAttempt>
{
    public void Configure(EntityTypeBuilder<ProcessingAttempt> builder)
    {
        builder.ToTable("ProcessingAttempts", t =>
            t.HasCheckConstraint("CK_ProcessingAttempts_AttemptNo_Positive", "\"AttemptNo\" > 0"));

        builder.HasKey(x => x.AttemptId);
        builder.Property(x => x.AttemptId).ValueGeneratedNever();

        builder.Property(x => x.WorkerName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.WorkerInstanceId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasEnumAsString();
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);

        // Two workers can never record the same attempt number for a task.
        builder.HasIndex(x => new { x.TaskId, x.AttemptNo }).IsUnique();
        builder.HasIndex(x => new { x.WorkerName, x.WorkerInstanceId });
        builder.HasIndex(x => new { x.Status, x.StartedAt });
    }
}

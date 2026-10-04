using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ReportExportConfiguration : IEntityTypeConfiguration<ReportExport>
{
    public void Configure(EntityTypeBuilder<ReportExport> builder)
    {
        builder.ToTable("ReportExports", t =>
        {
            // The result is the stored file's key, size, row count and time - or nothing at all.
            // (One line each: a line break inside the SQL would depend on the line endings of this file.)
            t.HasCheckConstraint(
                "CK_ReportExports_Result_AllOrNone",
                """("ObjectKey" IS NULL AND "SizeBytes" IS NULL AND "RowCount" IS NULL AND "CompletedAt" IS NULL) """
                + """OR ("ObjectKey" IS NOT NULL AND "SizeBytes" IS NOT NULL AND "RowCount" IS NOT NULL AND "CompletedAt" IS NOT NULL)""");
        });

        // 1:1 with its task: the task id is the key, there is no separate id.
        builder.HasKey(x => x.TaskId);
        builder.Property(x => x.TaskId).ValueGeneratedNever();

        builder.Property(x => x.ReportType).HasEnumAsString();
        builder.Property(x => x.Parameters).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(ReportExport.MaxFileNameLength).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.ObjectKey).HasMaxLength(ReportExport.MaxObjectKeyLength);
        builder.ConfigureAuditColumns();

        builder.HasOne(x => x.ProcessingTask)
            .WithOne()
            .HasForeignKey<ReportExport>(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        // The requester's own list, newest first.
        builder.HasIndex(x => new { x.CreatedBy, x.CreatedAt }).IsDescending(false, true);

        // What the retention sweep looks for: exports past their time whose file was not deleted yet.
        builder.HasIndex(x => x.ExpiresAt).HasFilter("\"DeletedAt\" IS NULL");
    }
}

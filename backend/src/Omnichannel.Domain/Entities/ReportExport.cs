using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// An Excel export a user asked for, built in the background. 1:1 with its <see cref="ProcessingTask"/>
/// (<see cref="TaskId"/> is both primary and foreign key): the task holds the status and the attempts, this row holds what
/// the task cannot - what was asked for, where the file is, and until when it is kept. <c>CreatedBy</c> is the requester.
/// <para>
/// The file itself lives in the object storage under <see cref="ObjectKeyOf"/>, never here. The four result fields are
/// all null (no file yet, or the export failed) or all set.
/// </para>
/// </summary>
public class ReportExport : AuditableEntity
{
    public const int MaxFileNameLength = 100;

    /// <summary><c>exports/{taskId}.xlsx</c>: 8 characters, a GUID of 36 and the extension.</summary>
    public const int MaxObjectKeyLength = 49;

    private ReportExport()
    {
    }

    /// <param name="parameters">The normalised request (filters, time zone, language) as JSON.</param>
    public ReportExport(Guid taskId, ReportType reportType, string parameters, string fileName, DateTimeOffset expiresAt)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("An export belongs to a task.", nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fileName.Length, MaxFileNameLength, nameof(fileName));

        TaskId = taskId;
        ReportType = reportType;
        Parameters = parameters;
        FileName = fileName;
        ExpiresAt = expiresAt;
    }

    public Guid TaskId { get; private set; }
    public ReportType ReportType { get; private set; }

    /// <summary>JSON (jsonb): the normalised filters, <c>timeZone</c> and <c>language</c> of the request.</summary>
    public string Parameters { get; private set; } = null!;

    /// <summary>The name the file is downloaded as (ASCII).</summary>
    public string FileName { get; private set; } = null!;

    /// <summary>The file is kept until then; afterwards the retention sweep deletes it.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public string? ObjectKey { get; private set; }
    public long? SizeBytes { get; private set; }

    /// <summary>Data rows in the file: orders and item lines, or products.</summary>
    public int? RowCount { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Set by the retention sweep once the file is gone: the export is expired.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    public ProcessingTask ProcessingTask { get; private set; } = null!;

    /// <summary>
    /// The key of an export's file. It depends on the task alone, so a second upload of the same export replaces the
    /// first, and the file of an export can be deleted without a stored key.
    /// </summary>
    public static string ObjectKeyOf(Guid taskId) => $"exports/{taskId}.xlsx";

    /// <summary>The file was stored under this export's key.</summary>
    public void Complete(long sizeBytes, int rowCount, DateTimeOffset completedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (DeletedAt is not null)
        {
            throw new InvalidOperationException($"Export {TaskId} is expired; its file was deleted.");
        }

        ObjectKey = ObjectKeyOf(TaskId);
        SizeBytes = sizeBytes;
        RowCount = rowCount;
        CompletedAt = completedAt;
    }

    /// <summary>The file is gone (or never existed). The first deletion counts; a later one changes nothing.</summary>
    public void MarkDeleted(DateTimeOffset deletedAt) => DeletedAt ??= deletedAt;
}

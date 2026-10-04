using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Reports;

/// <summary>
/// Persistence side of the Report Export Worker. Every method commits its own work; none holds a transaction while the
/// worker talks to the object storage, and the caller ACKs the message only after the method returned.
/// </summary>
public interface IReportExportProcessingService
{
    /// <summary>The task a <c>report.export.requested</c> names, or null when no such task exists.</summary>
    Task<ReportExportTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the task, marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress
    /// (committed before any work). Null when the task is already terminal.
    /// </summary>
    Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the rows of the export in one <c>REPEATABLE READ, READ ONLY</c> transaction - every sheet from the same
    /// snapshot - and builds the workbook while reading. Nothing outside the database is called in that transaction.
    /// </summary>
    /// <exception cref="ReportExportRejectedException">
    /// Trying again cannot help: more rows than <c>Reports:MaxRows</c> (<c>export_too_large</c>), or parameters this host
    /// cannot read (<c>export_parameters_invalid</c>).
    /// </exception>
    Task<GeneratedReport> GenerateAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// The file is stored: result of the export, attempt and task Succeeded, and the alert of an earlier final failure
    /// resolved - one transaction. When another delivery completed the task meanwhile, only the attempt is closed.
    /// </summary>
    Task CompleteAsync(Guid taskId, Guid attemptId, long sizeBytes, int rowCount, CancellationToken cancellationToken);

    /// <summary>Attempt Failed with a sanitised error; task RetryScheduled (unless completed meanwhile).</summary>
    Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken);

    /// <summary>
    /// Final failure, in one transaction before the message goes to the DLQ: attempt Failed (<paramref name="attemptId"/>, or
    /// a new attempt when null), task Failed, SystemEventLog Error <c>processing.failed</c> and the <c>ProcessingFailed</c> alert.
    /// </summary>
    /// <returns>The failed attempt number; null when the task was already completed (nothing to fail).</returns>
    Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken);

    /// <summary>
    /// SystemEventLog Error <see cref="ReportEvents.MessageRejected"/> for a message sent to the DLQ, with the rejection's
    /// <c>reasonCode</c> and <c>reason</c>.
    /// </summary>
    Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        string workerName,
        CancellationToken cancellationToken);
}

/// <param name="HasExport">The task has its <c>ReportExports</c> row (every export task does).</param>
/// <param name="IsExpired">The export's file was deleted, or its retention time is over: nothing may be built any more.</param>
public sealed record ReportExportTaskState(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    string CorrelationId,
    bool HasExport,
    bool IsExpired);

/// <summary>A finished workbook, ready to be stored. <see cref="Content"/> is seekable and positioned at its start.</summary>
/// <param name="RowCount">Data rows in the file: orders and item lines, or products.</param>
public sealed record GeneratedReport(Stream Content, string FileName, int RowCount) : IDisposable
{
    public long SizeBytes => Content.Length;

    public void Dispose() => Content.Dispose();
}

/// <summary>An export that cannot be built, however often it is tried: a permanent failure of its task.</summary>
public sealed class ReportExportRejectedException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

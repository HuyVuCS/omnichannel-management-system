using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.ReportExportWorker;

/// <summary>
/// Handles one <c>report.export.requested</c> delivery: builds the Excel file of an export task and stores it in the
/// object storage. Scoped: one instance per delivery, so the task/attempt of this delivery are remembered for
/// <see cref="RecordFinalFailureAsync"/>.
/// <list type="bullet">
/// <item>Poison (unparseable, unknown task, a task that is no export) → <c>Reject</c> after logging
/// <c>report_export.message_rejected</c>.</item>
/// <item>Completed task (duplicate or late delivery) → <c>Ack</c>, nothing is built.</item>
/// <item>Otherwise an attempt, committed first. An export whose file was deleted or whose retention time is over fails
/// for good (<c>export_expired</c>). Then: read the rows from one read-only snapshot while the workbook is built - upload
/// the file, after that transaction - complete export, attempt and task in one transaction - <c>Ack</c>. The key of the
/// file depends on the task alone, so a redelivery after the upload and before the commit stores the same object again.</item>
/// <item>Storage or temporary database error → attempt Failed + task RetryScheduled, then <c>Retry</c> (delayed copy or
/// final failure: the consumer decides). More rows than an export may hold, or parameters this host cannot read →
/// <c>Fail</c>, no retry.</item>
/// <item>Host stopping during the work → the delivery is abandoned (not settled): the broker redelivers it and the next
/// delivery marks this attempt abandoned.</item>
/// </list>
/// </summary>
public sealed class ReportExportMessageHandler(
    IReportExportProcessingService exports,
    IObjectStorage storage,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<ReportExportMessageHandler> logger) : IQueueMessageHandler
{
    public const string WorkerName = "ReportExportWorker";
    public const string Actor = "system:report-export-worker";

    private Guid? _taskId;
    private Guid? _attemptId;

    public async Task<HandlerResult> HandleAsync(ReceivedMessage delivery, CancellationToken cancellationToken)
    {
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;
        var stopping = lifetime.ApplicationStopping;

        if (stopping.IsCancellationRequested)
        {
            // Nothing was started: leave the message for the next consumer instead of beginning an attempt that cannot finish.
            return HandlerResult.Abandon;
        }

        if (!ReportExportMessageParser.TryParse(delivery.Body, out var message, out var rejection))
        {
            return await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
        }

        correlationContext.CorrelationId = message!.Envelope.CorrelationId;
        using var logScope = BeginScope(message, instanceId);

        var task = await exports.GetTaskAsync(message.TaskId, cancellationToken);
        if (RejectionOf(message.TaskId, task) is { } taskRejection)
        {
            return await RejectAsync(
                message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, taskRejection, cancellationToken);
        }

        if (task!.Status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed)
        {
            logger.LogInformation(
                "Duplicate delivery of message {MessageId}: task already {Status}; acknowledged", message.Envelope.MessageId, task.Status);
            return HandlerResult.Ack;
        }

        var attempt = await exports.StartAttemptAsync(message.TaskId, WorkerName, instanceId, cancellationToken);
        if (attempt is null)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task completed meanwhile; acknowledged", message.Envelope.MessageId);
            return HandlerResult.Ack;
        }

        _taskId = message.TaskId;
        _attemptId = attempt.AttemptId;

        using var attemptScope = logger.BeginScope(new Dictionary<string, object> { ["AttemptNo"] = attempt.AttemptNo });

        if (task.IsExpired)
        {
            // The requester could not keep the file: nothing is built, and no retry can change that.
            logger.LogWarning("Attempt {AttemptNo}: the export has expired; nothing is built", attempt.AttemptNo);
            return new HandlerResult.Fail(attempt.AttemptNo, ReportErrorCodes.ExportExpired, "The retention time of the export is over.");
        }

        try
        {
            // Only the building and the upload are tied to the host stopping; the bookkeeping of this delivery finishes.
            using var report = await exports.GenerateAsync(message.TaskId, stopping);
            await storage.PutAsync(
                ReportExport.ObjectKeyOf(message.TaskId),
                report.Content,
                ReportFiles.ContentType,
                ReportFiles.ContentDispositionOf(report.FileName),
                stopping);
            await exports.CompleteAsync(message.TaskId, attempt.AttemptId, report.SizeBytes, report.RowCount, cancellationToken);

            logger.LogInformation(
                "Export stored: {RowCount} rows, {SizeBytes} bytes (attempt {AttemptNo})", report.RowCount, report.SizeBytes, attempt.AttemptNo);
            return HandlerResult.Ack;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The attempt stays Running; the redelivery marks it abandoned and starts a new one.
            logger.LogInformation("Attempt {AttemptNo} cancelled: the host is stopping; the message is left for redelivery", attempt.AttemptNo);
            return HandlerResult.Abandon;
        }
        catch (ReportExportRejectedException exception)
        {
            logger.LogError(
                "Attempt {AttemptNo} failed permanently ({ErrorCode}): {ErrorMessage}", attempt.AttemptNo, exception.ErrorCode, exception.Message);
            return new HandlerResult.Fail(attempt.AttemptNo, exception.ErrorCode, exception.Message);
        }
        catch (Exception exception)
        {
            var error = ProcessingErrorClassifier.Classify(exception);
            // A failure of the storage is fully described by its code (the storage logged the operation and the status);
            // its exception carries nothing more. Only other errors need the stack.
            var logged = exception is ObjectStorageException ? null : exception;
            if (error.IsPermanent)
            {
                logger.LogError(logged, "Attempt {AttemptNo} failed permanently ({ErrorCode})", attempt.AttemptNo, error.ErrorCode);
                return new HandlerResult.Fail(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
            }

            logger.LogWarning(logged, "Attempt {AttemptNo} failed ({ErrorCode})", attempt.AttemptNo, error.ErrorCode);
            try
            {
                await exports.RecordTransientFailureAsync(message.TaskId, attempt.AttemptId, error.ErrorCode, error.ErrorMessage, cancellationToken);
            }
            catch (Exception recordException)
            {
                // Database unavailable: the next delivery marks this attempt abandoned and starts a new one.
                logger.LogError(recordException, "Could not record the failure of attempt {AttemptNo}", attempt.AttemptNo);
            }

            return new HandlerResult.Retry(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage, attempt.AttemptBudgetStart);
        }
    }

    public async Task<bool> RecordFinalFailureAsync(ReceivedMessage delivery, FinalFailure failure, CancellationToken cancellationToken)
    {
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;

        var taskId = _taskId;
        if (taskId is null)
        {
            // Called before HandleAsync (delivery-limit guard): identify the task from the message itself.
            if (!ReportExportMessageParser.TryParse(delivery.Body, out var message, out var rejection))
            {
                await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
                return true;
            }

            correlationContext.CorrelationId = message!.Envelope.CorrelationId;
            taskId = message.TaskId;
            if (RejectionOf(message.TaskId, await exports.GetTaskAsync(message.TaskId, cancellationToken)) is { } taskRejection)
            {
                await RejectAsync(message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, taskRejection, cancellationToken);
                return true;
            }
        }

        var attemptNo = await exports.RecordFinalFailureAsync(
            taskId.Value,
            _attemptId,
            failure.ErrorCode,
            failure.ErrorMessage,
            WorkerName,
            instanceId,
            failure.Queue,
            cancellationToken);

        // Null: the task was completed by another delivery meanwhile; nothing failed, the message can be acknowledged.
        return attemptNo is not null;
    }

    /// <summary>Why the task of a well-formed message cannot be processed by this queue; null when it can.</summary>
    private static MessageRejection? RejectionOf(Guid taskId, ReportExportTaskState? task) => task switch
    {
        null => new(MessageRejectionCodes.UnknownTask, $"No processing task {taskId} exists."),
        { TaskType: not ProcessingTaskType.ReportExport } or { HasExport: false } =>
            new(MessageRejectionCodes.TaskMismatch, $"Task {taskId} is not an export task with its export row."),
        _ => null
    };

    private async Task<HandlerResult> RejectAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        CancellationToken cancellationToken)
    {
        // If even the log row cannot be written (database down), the exception makes the consumer retry the message with delay.
        await exports.RecordRejectedMessageAsync(messageId, correlationId, taskId, rejection, WorkerName, cancellationToken);
        return new HandlerResult.Reject(rejection.Reason);
    }

    private IDisposable? BeginScope(ReportExportMessage message, string instanceId) => logger.BeginScope(new Dictionary<string, object>
    {
        ["MessageId"] = message.Envelope.MessageId,
        ["TaskId"] = message.TaskId,
        ["CorrelationId"] = message.Envelope.CorrelationId,
        ["WorkerName"] = WorkerName,
        ["WorkerInstanceId"] = instanceId
    });
}

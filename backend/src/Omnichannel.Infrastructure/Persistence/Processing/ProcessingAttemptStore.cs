using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Alerts;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// The attempt bookkeeping every worker shares (doc §5.3): start an attempt, record a transient failure, record the final
/// failure. Each method locks the task row (<c>FOR UPDATE</c>), so concurrent deliveries of the same message are serialised,
/// and commits its own transaction. What an attempt does in between belongs to the worker's own service.
/// </summary>
internal sealed class ProcessingAttemptStore(
    OmnichannelDbContext dbContext,
    AlertStore alerts,
    TimeProvider timeProvider,
    ILogger<ProcessingAttemptStore> logger)
{
    /// <summary>
    /// Marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress (committed before any
    /// business work). Null when there is nothing to start: the task is already terminal, or
    /// <paramref name="messageId"/> is not the message the task waits for.
    /// </summary>
    /// <param name="messageId">
    /// The id of the delivered message. A manual retry gives the task a new message; a copy of the one before it that is
    /// still on its way (a retry queue, a redelivery after the task was failed as stale) must not run next to the new one.
    /// </param>
    public async Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
        string messageId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var task = await LockTaskAsync(taskId, cancellationToken);
        if (task.IsTerminal)
        {
            return null;
        }

        // Under the row lock: a manual retry that commits after this check waits for this attempt's bookkeeping.
        if (task.MessageId is { } current && !string.Equals(current, messageId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Message {MessageId} is not the message task {TaskId} waits for ({CurrentMessageId}): replaced by a manual retry; no attempt is started",
                messageId, taskId, current);
            return null;
        }

        var attempt = task.StartAttempt(workerName, workerInstanceId, timeProvider.GetUtcNow());
        dbContext.ProcessingAttempts.Add(attempt);

        // The unique (TaskId, AttemptNo) index is the last guard against two workers numbering the same attempt.
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AttemptStart(attempt.AttemptId, attempt.AttemptNo, task.AttemptBudgetStart);
    }

    /// <summary>Attempt Failed with a sanitised error; task RetryScheduled (unless completed meanwhile).</summary>
    public async Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // The failed unit of work may have left tracked entities behind in this scope.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var task = await LockTaskAsync(taskId, cancellationToken);
        task.ScheduleRetry(attemptId, errorCode, errorMessage, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Attempt Failed (<paramref name="attemptId"/>, or a new attempt when null), task Failed, SystemEventLog Error
    /// <c>processing.failed</c> and the alert of the failure (per channel and product for stock sync, per task otherwise),
    /// in one transaction.
    /// </summary>
    /// <returns>The failed attempt number; null when the task was already completed (nothing to fail).</returns>
    public Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken) =>
        FailAsync(taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, unchangedSince: null, cancellationToken);

    /// <summary>
    /// The final failure of a task nothing has touched since <paramref name="unchangedSince"/> (the stale-task sweep): a
    /// new attempt of <paramref name="workerName"/> that fails with <see cref="AttemptErrorCodes.StaleNoProgress"/>, and
    /// everything <see cref="RecordFinalFailureAsync"/> writes. Status and age are checked again under the row lock, so a
    /// task a worker started, finished or rescheduled meanwhile is left alone.
    /// </summary>
    /// <returns>The failed attempt number; null when the task is no longer stale (nothing was written).</returns>
    public Task<int?> FailStaleAsync(
        Guid taskId,
        DateTimeOffset unchangedSince,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken) =>
        FailAsync(
            taskId, attemptId: null, AttemptErrorCodes.StaleNoProgress, errorMessage, workerName, workerInstanceId, queue: null,
            unchangedSince, cancellationToken);

    private async Task<int?> FailAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string? queue,
        DateTimeOffset? unchangedSince,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await LockTaskAsync(taskId, cancellationToken);
        if (task.IsTerminal || (unchangedSince is { } since && (task.UpdatedAt ?? task.CreatedAt) >= since))
        {
            return null;
        }

        // No attempt yet for this delivery (delivery-limit guard): record the failure as its own attempt.
        if (attemptId is not { } id)
        {
            var attempt = task.StartAttempt(workerName, workerInstanceId, now);
            dbContext.ProcessingAttempts.Add(attempt);
            id = attempt.AttemptId;
        }

        task.Fail(id, errorCode, errorMessage, now);
        var attemptNo = task.Attempts.Single(a => a.AttemptId == id).AttemptNo;

        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Error,
            ProcessingEvents.Failed,
            task.CorrelationId,
            task.TaskId,
            task.OrderId,
            workerName,
            MessageJson.Serialize(new
            {
                taskId = task.TaskId,
                taskType = task.TaskType,
                attemptNo,
                errorCode,
                errorMessage,
                queue
            }),
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        // Stock sync: one alert per channel and product (only the newest task of a pair matters, every push sends the
        // current stock). Every other task type: one alert per task.
        var failure = task is { TaskType: ProcessingTaskType.StockSync, ChannelId: { } channelId, ProductId: { } productId }
            ? AlertOccurrence.StockSyncFailed(channelId, productId, task.TaskId, errorCode)
            : AlertOccurrence.ProcessingFailed(task.TaskId, task.OrderId, errorCode);
        await alerts.OpenOrRefreshAsync([failure], cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (queue is null)
        {
            logger.LogError(
                "Task {TaskId} failed at attempt {AttemptNo} ({ErrorCode}): no progress, no message is expected any more",
                task.TaskId, attemptNo, errorCode);
        }
        else
        {
            logger.LogError(
                "Task {TaskId} failed permanently at attempt {AttemptNo} ({ErrorCode}); message goes to the DLQ",
                task.TaskId, attemptNo, errorCode);
        }

        return attemptNo;
    }

    /// <summary>Locks the task row for the rest of the transaction and loads it with all attempts.</summary>
    public async Task<ProcessingTask> LockTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "ProcessingTasks" WHERE "TaskId" = {taskId} FOR UPDATE""",
            cancellationToken);

        return await dbContext.ProcessingTasks
            .Include(t => t.Attempts)
            .SingleAsync(t => t.TaskId == taskId, cancellationToken);
    }
}

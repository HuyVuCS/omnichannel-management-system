using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// The attempt bookkeeping every worker shares (doc §5.3): start an attempt, record a transient failure, record the final
/// failure. Each method locks the task row (<c>FOR UPDATE</c>), so concurrent deliveries of the same message are serialised,
/// and commits its own transaction. What an attempt does in between belongs to the worker's own service.
/// </summary>
internal sealed class ProcessingAttemptStore(
    OmnichannelDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ProcessingAttemptStore> logger)
{
    /// <summary>
    /// Marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress (committed before any
    /// business work). Null when the task is already terminal.
    /// </summary>
    public async Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
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
    /// Attempt Failed (<paramref name="attemptId"/>, or a new attempt when null), task Failed and SystemEventLog Error
    /// <c>processing.failed</c>, in one transaction.
    /// </summary>
    /// <returns>The failed attempt number; null when the task was already completed (nothing to fail).</returns>
    public async Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await LockTaskAsync(taskId, cancellationToken);
        if (task.IsTerminal)
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
        await transaction.CommitAsync(cancellationToken);

        logger.LogError(
            "Task {TaskId} failed permanently at attempt {AttemptNo} ({ErrorCode}); message goes to the DLQ",
            task.TaskId, attemptNo, errorCode);
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

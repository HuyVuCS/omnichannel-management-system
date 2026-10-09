using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Alerts;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// What the persistence of the Invoice Worker and of the Notification Worker have in common
/// (<see cref="ITaskRequestProcessing"/>): the task of a message, the shared attempt bookkeeping, the event of a rejected
/// message, and the completion of a task in one transaction.
/// </summary>
/// <param name="rejectedEventType">The <c>…message_rejected</c> event type of the worker.</param>
internal abstract class TaskRequestProcessingService(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    AlertStore alerts,
    TimeProvider timeProvider,
    string rejectedEventType) : ITaskRequestProcessing
{
    protected OmnichannelDbContext DbContext { get; } = dbContext;

    public async Task<TaskRequestState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await DbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new TaskRequestState(t.TaskId, t.TaskType, t.Status, t.CorrelationId, t.OrderId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken) =>
        attempts.StartAttemptAsync(taskId, workerName, workerInstanceId, cancellationToken);

    public Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken) =>
        attempts.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

    public Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken) =>
        attempts.RecordFinalFailureAsync(
            taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

    public async Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        string workerName,
        CancellationToken cancellationToken)
    {
        DbContext.ChangeTracker.Clear();
        DbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Error,
            rejectedEventType,
            correlationId,
            taskId,
            orderId: null,
            workerName,
            MessageJson.Serialize(new { messageId, reasonCode = rejection.Code, reason = rejection.Reason }),
            timeProvider.GetUtcNow()));
        await DbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One transaction under the lock of the task row: <paramref name="storeResult"/> (what the work produced, if it is
    /// stored), attempt and task Succeeded, and the alert of the task's earlier final failure resolved (it was retried
    /// manually). When another delivery completed the task meanwhile, only the attempt is closed and nothing is stored.
    /// </summary>
    protected async Task CompleteTaskAsync(
        Guid taskId,
        Guid attemptId,
        Func<DateTimeOffset, CancellationToken, Task>? storeResult,
        CancellationToken cancellationToken)
    {
        DbContext.ChangeTracker.Clear();

        await using var transaction = await DbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await attempts.LockTaskAsync(taskId, cancellationToken);
        var completed = !task.IsTerminal;
        if (completed)
        {
            if (storeResult is not null)
            {
                await storeResult(now, cancellationToken);
            }

            task.Succeed(orderId: null, attemptId, now);
        }
        else
        {
            task.CloseAttemptOfCompletedTask(attemptId, now);
        }

        await DbContext.SaveChangesAsync(cancellationToken);

        if (completed)
        {
            await alerts.ResolveAsync([AlertDedupKey.ProcessingFailed(taskId)], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

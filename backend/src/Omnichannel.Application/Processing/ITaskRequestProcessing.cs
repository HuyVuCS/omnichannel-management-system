using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Processing;

/// <summary>
/// The persistence every worker of a <c>{ taskId }</c> message about an order needs (Invoice Worker, Notification
/// Worker): the task a message names, and the attempt bookkeeping all workers share. Every method commits its own
/// work; none holds a transaction while the worker calls a provider, and the caller ACKs the message only after the
/// method returned.
/// </summary>
public interface ITaskRequestProcessing
{
    /// <summary>The task a message names, or null when no such task exists.</summary>
    Task<TaskRequestState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the task, marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress
    /// (committed before any work). Null when the task is already terminal.
    /// </summary>
    Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken);

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
    /// SystemEventLog Error <c>…message_rejected</c> of the worker for a message sent to the DLQ, with the rejection's
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

/// <param name="OrderId">The order the task belongs to; an invoice or e-mail task without one cannot be processed.</param>
public sealed record TaskRequestState(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    string CorrelationId,
    Guid? OrderId);

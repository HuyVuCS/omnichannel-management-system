namespace Omnichannel.Application.Processing;

/// <summary>SystemEventLog event types shared by every asynchronous task type.</summary>
public static class ProcessingEvents
{
    /// <summary>
    /// A processing task failed for good (retry limit, permanent error, delivery limit) and its message went to the DLQ.
    /// Payload: <c>{taskId, taskType, attemptNo, errorCode, errorMessage, queue}</c>.
    /// </summary>
    public const string Failed = "processing.failed";

    /// <summary>
    /// An operator retried a Failed task manually (Information, task's correlation id). Payload:
    /// <c>{taskId, taskType, requestedBy, previousMessageId, newMessageId, attemptBudgetStart}</c>.
    /// </summary>
    public const string RetryRequested = "processing.retry_requested";

    /// <summary>
    /// A run of the stale-task sweep that failed at least one task (Warning, the run's correlation id; every task has
    /// its own <see cref="Failed"/> event as well). Payload: <c>{runId, staleAfterSeconds, failed, taskIds, notListed}</c>
    /// - <c>taskIds</c> holds at most <see cref="StaleTasksFailedMaxListed"/> ids, <c>notListed</c> counts the rest.
    /// </summary>
    public const string StaleTasksFailed = "processing.stale_tasks_failed";

    public const int StaleTasksFailedMaxListed = 50;
}

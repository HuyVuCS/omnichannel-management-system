using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>
/// One logical unit of asynchronous work (order processing, stock sync, invoice, email).
/// Each task has its own status; a failed task never changes the order's status by itself.
/// </summary>
public class ProcessingTask : IHasTimestamps
{
    private readonly List<ProcessingAttempt> _attempts = [];

    private ProcessingTask()
    {
    }

    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>SHA-256 as lower-case hex.</summary>
    public const int RequestHashLength = 64;

    /// <param name="idempotencyKey">
    /// Optional business key (unique): a second task with the same key cannot be created, e.g. the same external order
    /// submitted twice before any order exists.
    /// </param>
    /// <param name="requestHash">
    /// Optional hash of the submitted content behind <paramref name="idempotencyKey"/>, used to tell an identical
    /// resubmission from a conflicting one.
    /// </param>
    public ProcessingTask(
        ProcessingTaskType taskType,
        Guid? orderId,
        string correlationId,
        string? messageId,
        string? idempotencyKey = null,
        string? requestHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (idempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(idempotencyKey.Length, MaxIdempotencyKeyLength, nameof(idempotencyKey));
        }

        if (requestHash is not null && requestHash.Length != RequestHashLength)
        {
            throw new ArgumentException($"Request hash must be {RequestHashLength} hex characters.", nameof(requestHash));
        }

        RequestHash = requestHash;

        TaskId = Guid.CreateVersion7();
        TaskType = taskType;
        OrderId = orderId;
        CorrelationId = correlationId;
        MessageId = messageId;
        IdempotencyKey = idempotencyKey;
        Status = ProcessingTaskStatus.Pending;
        RetryCount = 0;
    }

    public Guid TaskId { get; private set; }

    /// <summary>Null for tasks not tied to an order (e.g. reconciliation stock sync).</summary>
    public Guid? OrderId { get; private set; }

    public ProcessingTaskType TaskType { get; private set; }
    public ProcessingTaskStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public string CorrelationId { get; private set; } = null!;
    public string? MessageId { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string? RequestHash { get; private set; }

    /// <summary>
    /// Number of attempts that existed at the last manual retry (0 = never retried manually). The attempt limit applies to
    /// <c>AttemptNo - AttemptBudgetStart</c>, so a manually retried task gets a fresh budget while attempt numbers keep counting.
    /// </summary>
    public int AttemptBudgetStart { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public Order? Order { get; private set; }
    public IReadOnlyCollection<ProcessingAttempt> Attempts => _attempts;

    /// <summary>Succeeded or Failed: no further attempts.</summary>
    public bool IsTerminal => Status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed;

    /// <summary>
    /// Starts attempt <c>max(AttemptNo) + 1</c> and marks the task InProgress. Attempts still Running belong to a worker that
    /// died (the message was redelivered) and are marked abandoned. Requires all attempts to be loaded.
    /// </summary>
    public ProcessingAttempt StartAttempt(string workerName, string workerInstanceId, DateTimeOffset startedAt)
    {
        EnsureNotTerminal();

        foreach (var running in _attempts.Where(a => a.Status == ProcessingAttemptStatus.Running))
        {
            running.Abandon(startedAt);
        }

        var attemptNo = _attempts.Count == 0 ? 1 : _attempts.Max(a => a.AttemptNo) + 1;
        var attempt = new ProcessingAttempt(TaskId, workerName, workerInstanceId, attemptNo, startedAt);
        _attempts.Add(attempt);
        Status = ProcessingTaskStatus.InProgress;
        return attempt;
    }

    /// <summary>The work is done with a business outcome (for orders: the Order that was stored).</summary>
    public void Succeed(Guid? orderId, Guid attemptId, DateTimeOffset finishedAt)
    {
        EnsureNotTerminal();

        AttemptOf(attemptId).Succeed(finishedAt);
        OrderId = orderId ?? OrderId;
        Status = ProcessingTaskStatus.Succeeded;
    }

    /// <summary>Closes an attempt that found the task already completed by another delivery (nothing was done).</summary>
    public void CloseAttemptOfCompletedTask(Guid attemptId, DateTimeOffset finishedAt)
    {
        if (!IsTerminal)
        {
            throw new InvalidOperationException($"Task {TaskId} is {Status}, not completed.");
        }

        AttemptOf(attemptId).Succeed(finishedAt);
    }

    /// <summary>
    /// Temporary failure: the attempt fails and the task is scheduled for another attempt. If the task was completed meanwhile
    /// by a concurrent delivery, only the attempt is recorded.
    /// </summary>
    public void ScheduleRetry(Guid attemptId, string errorCode, string errorMessage, DateTimeOffset finishedAt)
    {
        AttemptOf(attemptId).Fail(errorCode, errorMessage, finishedAt);

        if (!IsTerminal)
        {
            Status = ProcessingTaskStatus.RetryScheduled;
            RetryCount++;
        }
    }

    /// <summary>
    /// Final failure (retry limit reached, permanent error, delivery limit): the task becomes Failed (terminal, needs an
    /// operator). The attempt is failed with the error unless its failure was already recorded (transient failure before).
    /// </summary>
    public void Fail(Guid attemptId, string errorCode, string errorMessage, DateTimeOffset finishedAt)
    {
        EnsureNotTerminal();

        var attempt = AttemptOf(attemptId);
        if (attempt.Status == ProcessingAttemptStatus.Running || attempt.IsAbandoned)
        {
            attempt.Fail(errorCode, errorMessage, finishedAt);
        }

        Status = ProcessingTaskStatus.Failed;
    }

    /// <summary>
    /// Manual retry of a Failed task by an operator: the task waits for <paramref name="newMessageId"/> (a new outbox row
    /// carrying the original message) and gets a fresh attempt budget. Attempts are kept. Requires all attempts to be loaded.
    /// </summary>
    /// <returns>The message id the task had before (the one that ended in the DLQ).</returns>
    public string? RequeueForManualRetry(string newMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newMessageId);
        if (Status != ProcessingTaskStatus.Failed)
        {
            throw new InvalidOperationException($"Task {TaskId} is {Status}; only a Failed task can be retried manually.");
        }

        var previousMessageId = MessageId;
        MessageId = newMessageId;
        Status = ProcessingTaskStatus.Pending;
        AttemptBudgetStart = _attempts.Count == 0 ? 0 : _attempts.Max(a => a.AttemptNo);
        return previousMessageId;
    }

    private ProcessingAttempt AttemptOf(Guid attemptId) =>
        _attempts.SingleOrDefault(a => a.AttemptId == attemptId)
        ?? throw new InvalidOperationException($"Attempt {attemptId} does not belong to task {TaskId} (or is not loaded).");

    private void EnsureNotTerminal()
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"Task {TaskId} is already {Status}.");
        }
    }
}

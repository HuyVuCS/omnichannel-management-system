using Omnichannel.Domain.Enums;

namespace Omnichannel.Domain.Entities;

/// <summary>A single execution of a <see cref="ProcessingTask"/> by a specific worker instance.</summary>
public class ProcessingAttempt
{
    private ProcessingAttempt()
    {
    }

    public ProcessingAttempt(Guid taskId, string workerName, string workerInstanceId, int attemptNo, DateTimeOffset startedAt)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId is required.", nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerInstanceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNo);

        AttemptId = Guid.CreateVersion7();
        TaskId = taskId;
        WorkerName = workerName;
        WorkerInstanceId = workerInstanceId;
        AttemptNo = attemptNo;
        Status = ProcessingAttemptStatus.Running;
        StartedAt = startedAt;
    }

    public Guid AttemptId { get; private set; }
    public Guid TaskId { get; private set; }
    public string WorkerName { get; private set; } = null!;
    public string WorkerInstanceId { get; private set; } = null!;
    public int AttemptNo { get; private set; }
    public ProcessingAttemptStatus Status { get; private set; }
    public string? ErrorCode { get; private set; }

    /// <summary>Sanitised error text, safe to show on the dashboard (no secrets, no stack traces).</summary>
    public string? ErrorMessage { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public ProcessingTask ProcessingTask { get; private set; } = null!;

    /// <summary>Error code of an attempt whose worker never finished it (crash, kill, lost connection).</summary>
    public const string AbandonedErrorCode = "abandoned";

    public const int MaxErrorCodeLength = 64;
    public const int MaxErrorMessageLength = 2000;

    public bool IsAbandoned => Status == ProcessingAttemptStatus.Failed && ErrorCode == AbandonedErrorCode;

    /// <remarks>
    /// Also allowed on an abandoned attempt: a concurrent duplicate delivery can mark a still-running attempt abandoned;
    /// if that attempt then completes, its real outcome wins.
    /// </remarks>
    internal void Succeed(DateTimeOffset finishedAt)
    {
        EnsureRunningOrAbandoned();

        Status = ProcessingAttemptStatus.Succeeded;
        ErrorCode = null;
        ErrorMessage = null;
        FinishedAt = finishedAt;
    }

    internal void Fail(string errorCode, string errorMessage, DateTimeOffset finishedAt)
    {
        EnsureRunningOrAbandoned();
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        Status = ProcessingAttemptStatus.Failed;
        ErrorCode = Truncate(errorCode, MaxErrorCodeLength);
        ErrorMessage = Truncate(errorMessage, MaxErrorMessageLength);
        FinishedAt = finishedAt;
    }

    internal void Abandon(DateTimeOffset finishedAt)
    {
        if (Status != ProcessingAttemptStatus.Running)
        {
            throw new InvalidOperationException($"Attempt {AttemptId} is {Status}, not Running.");
        }

        Status = ProcessingAttemptStatus.Failed;
        ErrorCode = AbandonedErrorCode;
        ErrorMessage = "The attempt did not finish (worker stopped or crashed); superseded by a new attempt.";
        FinishedAt = finishedAt;
    }

    private void EnsureRunningOrAbandoned()
    {
        if (Status != ProcessingAttemptStatus.Running && !IsAbandoned)
        {
            throw new InvalidOperationException($"Attempt {AttemptId} is already finished ({Status}).");
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}

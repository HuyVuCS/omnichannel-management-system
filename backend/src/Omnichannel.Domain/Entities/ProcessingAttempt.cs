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
}

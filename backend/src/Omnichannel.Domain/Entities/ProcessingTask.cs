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

    public ProcessingTask(ProcessingTaskType taskType, Guid? orderId, string correlationId, string? messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        TaskId = Guid.CreateVersion7();
        TaskType = taskType;
        OrderId = orderId;
        CorrelationId = correlationId;
        MessageId = messageId;
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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public Order? Order { get; private set; }
    public IReadOnlyCollection<ProcessingAttempt> Attempts => _attempts;
}

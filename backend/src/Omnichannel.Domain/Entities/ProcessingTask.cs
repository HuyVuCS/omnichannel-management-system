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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public Order? Order { get; private set; }
    public IReadOnlyCollection<ProcessingAttempt> Attempts => _attempts;
}

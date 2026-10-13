using Omnichannel.Application.Common.Paging;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Processing;

/// <summary>One execution of a task by a worker instance; <see cref="ErrorMessage"/> is sanitised (safe for the dashboard).</summary>
public sealed record ProcessingAttemptResponse(
    int AttemptNo,
    ProcessingAttemptStatus Status,
    string WorkerName,
    string WorkerInstanceId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <param name="ChannelId">The channel of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="ProductId">The product of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="ProductSku">The SKU of <paramref name="ProductId"/>; null when the task has no product.</param>
/// <param name="AttemptBudgetStart">Attempts that existed at the last manual retry (0 = never retried manually).</param>
/// <param name="LastAttempt">The attempt with the highest number; null when none was started yet.</param>
public sealed record ProcessingTaskSummaryResponse(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    Guid? OrderId,
    Guid? ChannelId,
    Guid? ProductId,
    string? ProductSku,
    int RetryCount,
    int AttemptBudgetStart,
    string CorrelationId,
    string? MessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    int AttemptCount,
    ProcessingAttemptResponse? LastAttempt);

/// <param name="ChannelId">The channel of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="ProductId">The product of a <c>StockSync</c> task; null for other task types.</param>
/// <param name="ProductSku">The SKU of <paramref name="ProductId"/>; null when the task has no product.</param>
/// <param name="Attempts">Ordered by <c>attemptNo</c>.</param>
/// <param name="Message">
/// The outbox row that carries the task's current message (<see cref="MessageId"/>); null when the task has no message or
/// the row does not exist. For a stock-sync task created from <c>inventory.changed</c> it is that event, shared by the tasks
/// of the other channels.
/// </param>
public sealed record ProcessingTaskDetailResponse(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    Guid? OrderId,
    Guid? ChannelId,
    Guid? ProductId,
    string? ProductSku,
    int RetryCount,
    int AttemptBudgetStart,
    string CorrelationId,
    string? MessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProcessingAttemptResponse> Attempts,
    ProcessingMessageResponse? Message);

/// <summary>
/// Publish state of a task's message: <c>Pending</c> = not published yet (see <see cref="PublishAttempts"/>/<see cref="LastError"/>),
/// <c>Published</c> = handed to RabbitMQ (processing state is on the task), <c>Failed</c> = the publisher gave up.
/// </summary>
public sealed record ProcessingMessageResponse(
    Guid OutboxEventId,
    string EventType,
    OutboxEventStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    int PublishAttempts,
    string? LastError);

/// <summary>
/// 202 body of a manual retry: the task waits for <see cref="MessageId"/> - a new copy of its original message, or for a
/// stock-sync task a new <c>stock.sync.requested</c> directed at the task's channel.
/// </summary>
public sealed record RetryProcessingTaskResponse(Guid TaskId, ProcessingTaskStatus Status, string MessageId);

/// <summary>
/// Task search (doc §5.3). <see cref="WorkerName"/>, <see cref="WorkerInstanceId"/>, <see cref="ErrorCode"/> and
/// <see cref="AttemptNo"/> are attempt-level: a task matches when one of its attempts matches all of them. <see cref="From"/>/<see cref="To"/>
/// filter on the task's <c>CreatedAt</c> (inclusive). <c>search</c> is not supported (ignored). Default order: newest first
/// (<c>createdAt</c>; time fields sort descending unless <c>sortDir=asc</c>).
/// </summary>
public sealed record ProcessingTaskListQuery : PageQuery
{
    public const string SortByCreatedAt = "createdAt";
    public const string SortByUpdatedAt = "updatedAt";

    public static readonly IReadOnlyList<string> SortFields = [SortByCreatedAt, SortByUpdatedAt];

    public ProcessingTaskType? TaskType { get; init; }
    public ProcessingTaskStatus? Status { get; init; }
    public Guid? OrderId { get; init; }

    /// <summary>Tasks of this channel (stock sync).</summary>
    public Guid? ChannelId { get; init; }

    /// <summary>Tasks of this product (stock sync: the pushes of its stock to the channels).</summary>
    public Guid? ProductId { get; init; }

    public string? CorrelationId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? WorkerName { get; init; }
    public string? WorkerInstanceId { get; init; }
    public string? ErrorCode { get; init; }
    public int? AttemptNo { get; init; }

    public bool HasAttemptFilter =>
        !string.IsNullOrWhiteSpace(WorkerName)
        || !string.IsNullOrWhiteSpace(WorkerInstanceId)
        || !string.IsNullOrWhiteSpace(ErrorCode)
        || AttemptNo.HasValue;
}

public static class ProcessingErrorCodes
{
    public const string TaskNotFound = "task_not_found";

    /// <summary>Manual retry of a task that is not <c>Failed</c> (still running, waiting, succeeded, or already retried).</summary>
    public const string TaskNotFailed = "task_not_failed";

    /// <summary>
    /// Manual retry: the outbox row of the task's message does not exist, so there is nothing to republish; or the channel of
    /// a stock-sync task has no stock-sync queue.
    /// </summary>
    public const string RetrySourceMissing = "retry_source_missing";
}

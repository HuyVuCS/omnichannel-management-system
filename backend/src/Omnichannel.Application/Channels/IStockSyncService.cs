using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Channels;

/// <summary>
/// Persistence side of the Stock Sync Worker (doc §8.3). Every method commits its own work; no method holds a transaction
/// or a lock while the worker calls a channel, and the caller ACKs the message only after the method returned.
/// </summary>
public interface IStockSyncService
{
    /// <summary>
    /// The stock-sync task of one <c>inventory.changed</c> message on one channel: found or created by its idempotency key
    /// (<see cref="StockSyncIdempotencyKey"/>), so a redelivered message never creates a second task. No task is created when
    /// the channel does not exist, is Inactive, or has no Active mapping for the product.
    /// </summary>
    Task<StockSyncResolution> ResolveInventoryChangeAsync(
        string channelSlug,
        Guid productId,
        string messageId,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>The task a directed <c>stock.sync.requested</c> names, or null when no such task exists.</summary>
    Task<StockSyncTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the task, marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress
    /// (committed before the push). Null when the task is already terminal.
    /// </summary>
    Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// What to push right now: the mapping, the SKU and the <b>current</b> available stock, in one query without a lock.
    /// Null when the product is not mapped on the channel.
    /// </summary>
    Task<StockSyncTarget?> ReadTargetAsync(Guid channelId, Guid productId, CancellationToken cancellationToken);

    /// <summary>Attempt and task Succeeded (unless another delivery completed the task meanwhile: then only the attempt is closed).</summary>
    Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken);

    /// <summary>Attempt Failed with a sanitised error; task RetryScheduled (unless completed meanwhile).</summary>
    Task RecordTransientFailureAsync(Guid taskId, Guid attemptId, string errorCode, string errorMessage, CancellationToken cancellationToken);

    /// <summary>
    /// Final failure, in one transaction before the message goes to the DLQ: attempt Failed (<paramref name="attemptId"/>, or a
    /// new attempt when null), task Failed, and SystemEventLog Error <c>processing.failed</c>.
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

    /// <summary>SystemEventLog Error <see cref="StockSyncEvents.MessageRejected"/> for a message sent to the DLQ.</summary>
    Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        string workerName,
        string queue,
        CancellationToken cancellationToken);
}

public enum StockSyncResolutionKind
{
    /// <summary>No channel with this slug: nothing to do.</summary>
    ChannelNotFound,

    /// <summary>The channel is Inactive or has no Active mapping for the product: nothing to do, no task.</summary>
    NotMapped,

    /// <summary>The task of this message on this channel (new or existing).</summary>
    Task
}

/// <param name="TaskId">Set when <paramref name="Kind"/> is <see cref="StockSyncResolutionKind.Task"/>.</param>
/// <param name="ChannelId">The channel of that task.</param>
/// <param name="Status">The task's status at that moment.</param>
public sealed record StockSyncResolution(
    StockSyncResolutionKind Kind,
    Guid? TaskId = null,
    Guid? ChannelId = null,
    ProcessingTaskStatus? Status = null)
{
    public static StockSyncResolution ChannelNotFound { get; } = new(StockSyncResolutionKind.ChannelNotFound);

    public static StockSyncResolution NotMapped { get; } = new(StockSyncResolutionKind.NotMapped);
}

/// <param name="ChannelSlug">Slug of the task's channel; null when the task has no channel (not a stock-sync task).</param>
public sealed record StockSyncTaskState(
    Guid TaskId,
    ProcessingTaskType TaskType,
    ProcessingTaskStatus Status,
    string CorrelationId,
    Guid? ChannelId,
    string? ChannelSlug,
    Guid? ProductId);

/// <param name="IsActive">Channel and mapping are both Active; otherwise nothing is pushed.</param>
public sealed record StockSyncTarget(
    string ChannelSlug,
    bool IsActive,
    string Sku,
    string? ExternalProductId,
    string? ExternalSku,
    int AvailableStock);

/// <summary>
/// <c>stocksync:{channelId:N}:{messageId}</c>: one task per <c>inventory.changed</c> message and channel
/// (<c>UX_ProcessingTasks_IdempotencyKey</c>).
/// </summary>
public static class StockSyncIdempotencyKey
{
    public static string For(Guid channelId, string messageId) => $"stocksync:{channelId:N}:{messageId}";
}

/// <summary>SystemEventLog event types written by stock sync.</summary>
public static class StockSyncEvents
{
    /// <summary>A message on a stock-sync queue could never be processed and went to the DLQ. Payload: <c>{messageId, reason, queue}</c>.</summary>
    public const string MessageRejected = "stock_sync.message_rejected";
}

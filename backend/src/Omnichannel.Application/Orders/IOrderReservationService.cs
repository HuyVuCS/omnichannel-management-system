using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Orders;

/// <summary>
/// Persistence side of the Order Worker (doc §8.2). Every method commits its own transaction; the caller ACKs the message
/// only after the method returned.
/// </summary>
public interface IOrderReservationService
{
    /// <summary>Current task status, or null when no such task exists.</summary>
    Task<ProcessingTaskStatus?> GetTaskStatusAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the task, marks stale Running attempts abandoned, inserts the next attempt and sets the task InProgress
    /// (committed before any business work). Null when the task is already terminal.
    /// </summary>
    Task<AttemptStart?> StartAttemptAsync(Guid taskId, string workerName, string workerInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves stock for every item atomically (all or nothing) and stores the Order, movements and <c>inventory.changed</c>
    /// outbox rows in one transaction; or stores an <c>InsufficientStock</c> order without touching stock.
    /// Either way the task and attempt end Succeeded.
    /// </summary>
    Task<ReservationResult> ReserveAsync(
        OrderCreatedPayload order,
        Guid attemptId,
        string workerName,
        CancellationToken cancellationToken);

    /// <summary>Attempt Failed with a sanitised error; task RetryScheduled (unless completed meanwhile).</summary>
    Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Final failure, in one transaction before the message goes to the DLQ: attempt Failed (<paramref name="attemptId"/>, or a new
    /// attempt when null), task Failed, and SystemEventLog Error <c>processing.failed</c>.
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

    /// <summary>SystemEventLog Error <see cref="OrderProcessingEvents.MessageRejected"/> for a message sent to the DLQ.</summary>
    Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        string workerName,
        CancellationToken cancellationToken);
}

public sealed record AttemptStart(Guid AttemptId, int AttemptNo);

public enum ReservationOutcome
{
    /// <summary>Stock reserved; Order Reserved.</summary>
    Reserved,

    /// <summary>Not enough stock for at least one item; Order InsufficientStock, nothing reserved.</summary>
    InsufficientStock,

    /// <summary>The Order already existed (final idempotency guard); the task now points to it.</summary>
    OrderAlreadyExists,

    /// <summary>The task was completed by another delivery while this one waited for the lock; nothing done.</summary>
    TaskAlreadyCompleted
}

public sealed record ReservationResult(ReservationOutcome Outcome, Guid? OrderId);

/// <summary>SystemEventLog event types written by order processing.</summary>
public static class OrderProcessingEvents
{
    public const string MessageRejected = "order.message_rejected";
    public const string InsufficientStock = "order.insufficient_stock";
    public const string OrderAlreadyExists = "order.already_exists";
}

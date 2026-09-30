using RabbitMQ.Client;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>A delivered message. <see cref="Body"/> is a copy, valid after the callback.</summary>
/// <param name="DeliveryCount">
/// Previous unsuccessful deliveries reported by the broker (<c>x-delivery-count</c>, quorum queues); 0 on the first delivery.
/// </param>
/// <param name="Properties">Original AMQP properties (used to build retry copies).</param>
public sealed record ReceivedMessage(
    ReadOnlyMemory<byte> Body,
    string? MessageId,
    string? CorrelationId,
    bool Redelivered,
    string RoutingKey,
    long DeliveryCount,
    IReadOnlyBasicProperties Properties);

/// <summary>Outcome of handling one message; the consumer settles the delivery accordingly.</summary>
public abstract record HandlerResult
{
    /// <summary>Done (success, duplicate, business outcome): acknowledge.</summary>
    public static HandlerResult Ack { get; } = new AckResult();

    public sealed record AckResult : HandlerResult;

    /// <summary>
    /// Temporary failure of attempt <paramref name="AttemptNo"/> (from the database). The consumer publishes a delayed retry
    /// copy, or treats it as the final failure when the attempt limit is reached.
    /// </summary>
    /// <param name="AttemptBudgetStart">
    /// Attempts that existed at the task's last manual retry; only the attempts after it count toward the limit.
    /// </param>
    public sealed record Retry(int AttemptNo, string ErrorCode, string ErrorMessage, int AttemptBudgetStart = 0) : HandlerResult
    {
        /// <summary>Position of this attempt in the current budget (1 = first attempt after intake or after a manual retry).</summary>
        public int AttemptInBudget => RetryPolicy.AttemptInBudget(AttemptNo, AttemptBudgetStart);
    }

    /// <summary>Permanent failure: no retry; final-failure handling, then the DLQ.</summary>
    public sealed record Fail(int? AttemptNo, string ErrorCode, string ErrorMessage) : HandlerResult;

    /// <summary>Poison (unparseable, unknown task): straight to the DLQ.</summary>
    public sealed record Reject(string Reason) : HandlerResult;
}

/// <summary>Why a message ends in the DLQ after processing was attempted.</summary>
/// <param name="AttemptNo">The failed attempt, or null when the message never reached a new attempt (delivery-limit guard).</param>
public sealed record FinalFailure(int? AttemptNo, string ErrorCode, string ErrorMessage, string Queue);

/// <summary>
/// Handles messages of one queue. Resolved from a fresh DI scope per delivery (instance state is per message); both methods
/// run in that scope.
/// </summary>
public interface IQueueMessageHandler
{
    Task<HandlerResult> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a final failure visible (e.g. task Failed + <c>processing.failed</c>) before the message is rejected to the DLQ.
    /// Called after <see cref="HandleAsync"/> returned <see cref="HandlerResult.Fail"/> or a <see cref="HandlerResult.Retry"/>
    /// at the attempt limit, or instead of <see cref="HandleAsync"/> on the last delivery the queue allows.
    /// </summary>
    /// <returns>True to reject the message to the DLQ; false when there was nothing to fail (e.g. already completed) → ACK.</returns>
    Task<bool> RecordFinalFailureAsync(ReceivedMessage message, FinalFailure failure, CancellationToken cancellationToken);
}

/// <summary>Retry limits of one consumer.</summary>
/// <param name="MaxAttempts">
/// Attempts (from the database) after which a transient failure becomes final; also the limit for deliveries on which the
/// handler threw before it could record an attempt.
/// </param>
/// <param name="DeliveryLimit">
/// The queue's <c>x-delivery-limit</c>; the delivery with this <c>x-delivery-count</c> is the last one. RabbitMQ 4 counts only
/// deliveries returned without settlement (consumer/channel died), not <c>basic.nack</c> with requeue.
/// </param>
public sealed record RetryPolicy(int MaxAttempts, int DeliveryLimit)
{
    public bool IsLastAllowedDelivery(long deliveryCount) => deliveryCount >= DeliveryLimit;

    /// <param name="attemptNo">Attempt (or failure) count within the current budget, see <see cref="AttemptInBudget"/>.</param>
    public bool CanRetry(int attemptNo) => attemptNo < MaxAttempts;

    /// <summary>
    /// Attempt <paramref name="attemptNo"/> counted from the last manual retry: a manual retry resets the budget while attempt
    /// numbers keep increasing (the task's <c>AttemptBudgetStart</c> = attempts that existed at that moment).
    /// </summary>
    public static int AttemptInBudget(int attemptNo, int attemptBudgetStart)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNo);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptBudgetStart);

        // Clamped to 1: an attempt numbered at or before the budget start cannot exist; if it did, it counts as the first one.
        return Math.Max(1, attemptNo - attemptBudgetStart);
    }
}

/// <summary>Settings of a consumer started by <see cref="ResilientQueueConsumer"/>.</summary>
public sealed record QueueConsumerSettings(
    string Queue,
    ushort Concurrency,
    RetryPolicy RetryPolicy,
    Func<IServiceProvider, IQueueMessageHandler> ResolveHandler,
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff);

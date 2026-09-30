using Microsoft.Extensions.Options;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>
/// Handles one <c>order.created</c> delivery (doc §8.2). Scoped: one instance per delivery, created by the consumer, so the
/// task/attempt of this delivery are remembered for <see cref="RecordFinalFailureAsync"/>.
/// <list type="bullet">
/// <item>Poison (unparseable, unknown task) → <c>Reject</c> after logging <c>order.message_rejected</c>.</item>
/// <item>Completed task (duplicate/late delivery) → <c>Ack</c>.</item>
/// <item>Transient error → attempt Failed + task RetryScheduled, then <c>Retry</c> (delayed copy or final failure: the consumer decides).</item>
/// <item>Permanent error → <c>Fail</c> (final failure, no retry).</item>
/// </list>
/// </summary>
public sealed class OrderMessageHandler(
    IOrderReservationService reservations,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<WorkerOptions> workerOptions,
    ILogger<OrderMessageHandler> logger) : IQueueMessageHandler
{
    public const string WorkerName = "OrderWorker";
    public const string Actor = "system:order-worker";

    private Guid? _taskId;
    private Guid? _attemptId;

    public async Task<HandlerResult> HandleAsync(ReceivedMessage delivery, CancellationToken cancellationToken)
    {
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;

        if (!OrderMessageParser.TryParse(delivery.Body, out var message, out var poisonReason))
        {
            return await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, poisonReason!, cancellationToken);
        }

        correlationContext.CorrelationId = message!.Envelope.CorrelationId;
        using var logScope = BeginScope(message, instanceId);

        var status = await reservations.GetTaskStatusAsync(message.TaskId, cancellationToken);
        if (status is null)
        {
            return await RejectAsync(
                message.Envelope.MessageId,
                message.Envelope.CorrelationId,
                message.TaskId,
                $"No processing task {message.TaskId} exists.",
                cancellationToken);
        }

        if (status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task already {Status}; acknowledged", message.Envelope.MessageId, status);
            return HandlerResult.Ack;
        }

        var attempt = await reservations.StartAttemptAsync(message.TaskId, WorkerName, instanceId, cancellationToken);
        if (attempt is null)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task completed meanwhile; acknowledged", message.Envelope.MessageId);
            return HandlerResult.Ack;
        }

        _taskId = message.TaskId;
        _attemptId = attempt.AttemptId;

        using var attemptScope = logger.BeginScope(new Dictionary<string, object> { ["AttemptNo"] = attempt.AttemptNo });
        logger.LogInformation(
            "Processing order {ExternalOrderId} ({ItemCount} items), attempt {AttemptNo}",
            message.Order.ExternalOrderId,
            message.Order.Items.Count,
            attempt.AttemptNo);

        ReservationResult result;
        try
        {
            result = await reservations.ReserveAsync(message.Order, attempt.AttemptId, WorkerName, cancellationToken);
        }
        catch (Exception exception)
        {
            var error = ProcessingErrorClassifier.Classify(exception);
            if (error.IsPermanent)
            {
                logger.LogError(exception, "Attempt {AttemptNo} failed permanently ({ErrorCode})", attempt.AttemptNo, error.ErrorCode);
                return new HandlerResult.Fail(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
            }

            logger.LogWarning(exception, "Attempt {AttemptNo} failed ({ErrorCode})", attempt.AttemptNo, error.ErrorCode);
            try
            {
                await reservations.RecordTransientFailureAsync(message.TaskId, attempt.AttemptId, error.ErrorCode, error.ErrorMessage, cancellationToken);
            }
            catch (Exception recordException)
            {
                // Database still unavailable: the next delivery marks this attempt abandoned and starts a new one.
                logger.LogError(recordException, "Could not record the failure of attempt {AttemptNo}", attempt.AttemptNo);
            }

            return new HandlerResult.Retry(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
        }

        using var orderScope = logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = result.OrderId ?? Guid.Empty });
        logger.LogInformation("Order {ExternalOrderId} processed: {Outcome}", message.Order.ExternalOrderId, result.Outcome);
        return HandlerResult.Ack;
    }

    public async Task<bool> RecordFinalFailureAsync(ReceivedMessage delivery, FinalFailure failure, CancellationToken cancellationToken)
    {
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;

        var taskId = _taskId;
        if (taskId is null)
        {
            // Called before HandleAsync (delivery-limit guard): identify the task from the message itself.
            if (!OrderMessageParser.TryParse(delivery.Body, out var message, out var poisonReason))
            {
                await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, poisonReason!, cancellationToken);
                return true;
            }

            correlationContext.CorrelationId = message!.Envelope.CorrelationId;
            taskId = message.TaskId;
            if (await reservations.GetTaskStatusAsync(message.TaskId, cancellationToken) is null)
            {
                await RejectAsync(message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, $"No processing task {message.TaskId} exists.", cancellationToken);
                return true;
            }
        }

        var attemptNo = await reservations.RecordFinalFailureAsync(
            taskId.Value,
            _attemptId,
            failure.ErrorCode,
            failure.ErrorMessage,
            WorkerName,
            instanceId,
            failure.Queue,
            cancellationToken);

        // Null: the task was completed by another delivery meanwhile; nothing failed, the message can be acknowledged.
        return attemptNo is not null;
    }

    private async Task<HandlerResult> RejectAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        CancellationToken cancellationToken)
    {
        // If even the log row cannot be written (database down), the exception makes the consumer requeue the message.
        await reservations.RecordRejectedMessageAsync(messageId, correlationId, taskId, reason, WorkerName, cancellationToken);
        return new HandlerResult.Reject(reason);
    }

    private IDisposable? BeginScope(OrderMessage message, string instanceId) => logger.BeginScope(new Dictionary<string, object>
    {
        ["MessageId"] = message.Envelope.MessageId,
        ["TaskId"] = message.TaskId,
        ["CorrelationId"] = message.Envelope.CorrelationId,
        ["WorkerName"] = WorkerName,
        ["WorkerInstanceId"] = instanceId
    });
}

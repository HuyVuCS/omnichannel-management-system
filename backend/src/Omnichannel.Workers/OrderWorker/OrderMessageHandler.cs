using Microsoft.Extensions.Options;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>
/// Handles one <c>order.created</c> delivery (doc §8.2) in its own DI scope and decides how it is settled.
/// ACK is returned only after the outcome was committed; poison → reject (DLQ); temporary failure → requeue.
/// </summary>
public sealed class OrderMessageHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> workerOptions,
    ILogger<OrderMessageHandler> logger)
{
    public const string WorkerName = "OrderWorker";
    public const string Actor = "system:order-worker";

    public async Task<MessageDisposition> HandleAsync(ReceivedMessage delivery)
    {
        var instanceId = workerOptions.Value.InstanceId;
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<WorkerCurrentUser>().UserId = Actor;
        var reservations = services.GetRequiredService<IOrderReservationService>();

        // Handler work is not tied to host shutdown: an in-flight message finishes (bounded by the host shutdown timeout).
        var cancellationToken = CancellationToken.None;

        if (!OrderMessageParser.TryParse(delivery.Body, out var message, out var poisonReason))
        {
            return await RejectAsync(reservations, delivery.MessageId, delivery.CorrelationId, null, poisonReason!, cancellationToken);
        }

        services.GetRequiredService<WorkerCorrelationContext>().CorrelationId = message!.Envelope.CorrelationId;

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.Envelope.MessageId,
            ["TaskId"] = message.TaskId,
            ["CorrelationId"] = message.Envelope.CorrelationId,
            ["WorkerName"] = WorkerName,
            ["WorkerInstanceId"] = instanceId
        });

        var status = await reservations.GetTaskStatusAsync(message.TaskId, cancellationToken);
        if (status is null)
        {
            return await RejectAsync(
                reservations,
                message.Envelope.MessageId,
                message.Envelope.CorrelationId,
                message.TaskId,
                $"No processing task {message.TaskId} exists.",
                cancellationToken);
        }

        if (status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task already {Status}; acknowledged", message.Envelope.MessageId, status);
            return MessageDisposition.Ack;
        }

        var attempt = await reservations.StartAttemptAsync(message.TaskId, WorkerName, instanceId, cancellationToken);
        if (attempt is null)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task completed meanwhile; acknowledged", message.Envelope.MessageId);
            return MessageDisposition.Ack;
        }

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
            var (errorCode, errorMessage) = ProcessingErrorClassifier.Classify(exception);
            logger.LogWarning(
                exception,
                "Attempt {AttemptNo} failed ({ErrorCode}); message requeued for another attempt",
                attempt.AttemptNo,
                errorCode);

            try
            {
                await reservations.RecordTransientFailureAsync(message.TaskId, attempt.AttemptId, errorCode, errorMessage, cancellationToken);
            }
            catch (Exception recordException)
            {
                // Database still unavailable: the redelivery marks this attempt abandoned and starts a new one.
                logger.LogError(recordException, "Could not record the failure of attempt {AttemptNo}", attempt.AttemptNo);
            }

            return MessageDisposition.Requeue;
        }

        using var orderScope = logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = result.OrderId ?? Guid.Empty });
        logger.LogInformation("Order {ExternalOrderId} processed: {Outcome}", message.Order.ExternalOrderId, result.Outcome);
        return MessageDisposition.Ack;
    }

    private async Task<MessageDisposition> RejectAsync(
        IOrderReservationService reservations,
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            "Rejecting message {MessageId} to the dead-letter queue: {Reason}",
            messageId,
            reason);

        // If even the log row cannot be written (database down), requeue: the rejection is retried with the redelivery.
        await reservations.RecordRejectedMessageAsync(messageId, correlationId, taskId, reason, WorkerName, cancellationToken);
        return MessageDisposition.Reject;
    }
}

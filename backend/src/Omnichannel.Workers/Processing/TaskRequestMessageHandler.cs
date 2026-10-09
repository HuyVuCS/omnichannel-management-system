using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.Processing;

/// <summary>What tells one worker of <c>{ taskId }</c> messages from another.</summary>
/// <param name="Name">The worker's name in attempts, events and logs.</param>
/// <param name="Actor">The audit actor of what the worker writes (<c>system:{worker}</c>).</param>
/// <param name="EventType">The one event type its queue handles.</param>
/// <param name="TaskType">The kind of task its messages name.</param>
public sealed record TaskRequestWorker(string Name, string Actor, string EventType, ProcessingTaskType TaskType);

/// <summary>An attempt that cannot succeed however often it is tried: the task fails at once, without a retry.</summary>
public sealed record PermanentFailure(string ErrorCode, string ErrorMessage);

/// <summary>
/// Handles one delivery of a queue whose messages name a task of an order (Invoice Worker, Notification Worker); the
/// work itself is <see cref="ProcessAsync"/> of the worker. Scoped: one instance per delivery, so the task/attempt of
/// this delivery are remembered for <see cref="RecordFinalFailureAsync"/>.
/// <list type="bullet">
/// <item>Poison (unparseable, unknown task, a task of another kind or without an order) → <c>Reject</c> after logging the
/// worker's <c>…message_rejected</c> event.</item>
/// <item>Completed task (duplicate or late delivery) → <c>Ack</c>, nothing is done.</item>
/// <item>Otherwise an attempt, committed first, then the work: reading without a lock, the call to the provider outside
/// any transaction, the completion in one transaction - <c>Ack</c>.</item>
/// <item>Temporary error (provider timeout, unavailable, open circuit, temporary database error) → attempt Failed + task
/// RetryScheduled, then <c>Retry</c> (delayed copy or final failure: the consumer decides). A refusal by the provider or
/// a <see cref="PermanentFailure"/> → <c>Fail</c>, no retry.</item>
/// <item>Host stopping during the call → the call is cancelled and the delivery is abandoned (not settled): the broker
/// redelivers it and the next delivery marks this attempt abandoned.</item>
/// </list>
/// </summary>
public abstract class TaskRequestMessageHandler(
    TaskRequestWorker worker,
    ITaskRequestProcessing processing,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger logger) : IQueueMessageHandler
{
    private Guid? _taskId;
    private Guid? _attemptId;

    /// <summary>
    /// The work of one attempt, up to and including the completion of the task. Only the call to the provider takes
    /// <paramref name="stopping"/>; the database work of the delivery finishes.
    /// </summary>
    /// <returns>Null when the task is completed; otherwise why it can never be.</returns>
    protected abstract Task<PermanentFailure?> ProcessAsync(
        Guid taskId,
        Guid orderId,
        AttemptStart attempt,
        CancellationToken stopping,
        CancellationToken cancellationToken);

    /// <summary>True for an answer of the provider: its code and message describe it fully, the log needs no stack.</summary>
    protected abstract bool IsProviderAnswer(Exception exception);

    public async Task<HandlerResult> HandleAsync(ReceivedMessage delivery, CancellationToken cancellationToken)
    {
        currentUser.UserId = worker.Actor;
        var instanceId = workerOptions.Value.InstanceId;
        var stopping = lifetime.ApplicationStopping;

        if (stopping.IsCancellationRequested)
        {
            // Nothing was started: leave the message for the next consumer instead of beginning an attempt that cannot finish.
            return HandlerResult.Abandon;
        }

        if (!TaskRequestMessageParser.TryParse(delivery.Body, worker.EventType, out var message, out var rejection))
        {
            return await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
        }

        correlationContext.CorrelationId = message!.Envelope.CorrelationId;
        using var logScope = BeginScope(message, instanceId);

        var task = await processing.GetTaskAsync(message.TaskId, cancellationToken);
        if (RejectionOf(message.TaskId, task) is { } taskRejection)
        {
            return await RejectAsync(
                message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, taskRejection, cancellationToken);
        }

        var orderId = task!.OrderId!.Value;
        using var orderScope = logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = orderId });

        if (task.Status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed)
        {
            logger.LogInformation(
                "Duplicate delivery of message {MessageId}: task already {Status}; acknowledged", message.Envelope.MessageId, task.Status);
            return HandlerResult.Ack;
        }

        var attempt = await processing.StartAttemptAsync(message.TaskId, worker.Name, instanceId, cancellationToken);
        if (attempt is null)
        {
            logger.LogInformation("Duplicate delivery of message {MessageId}: task completed meanwhile; acknowledged", message.Envelope.MessageId);
            return HandlerResult.Ack;
        }

        _taskId = message.TaskId;
        _attemptId = attempt.AttemptId;

        using var attemptScope = logger.BeginScope(new Dictionary<string, object> { ["AttemptNo"] = attempt.AttemptNo });
        try
        {
            if (await ProcessAsync(message.TaskId, orderId, attempt, stopping, cancellationToken) is not { } failure)
            {
                return HandlerResult.Ack;
            }

            logger.LogError(
                "Attempt {AttemptNo} failed permanently ({ErrorCode}): {ErrorMessage}", attempt.AttemptNo, failure.ErrorCode, failure.ErrorMessage);
            return new HandlerResult.Fail(attempt.AttemptNo, failure.ErrorCode, failure.ErrorMessage);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The attempt stays Running; the redelivery marks it abandoned and starts a new one.
            logger.LogInformation("Attempt {AttemptNo} cancelled: the host is stopping; the message is left for redelivery", attempt.AttemptNo);
            return HandlerResult.Abandon;
        }
        catch (Exception exception)
        {
            var error = ProcessingErrorClassifier.Classify(exception);
            var logged = IsProviderAnswer(exception) ? null : exception;
            if (error.IsPermanent)
            {
                logger.LogError(
                    logged, "Attempt {AttemptNo} failed permanently ({ErrorCode}): {ErrorMessage}",
                    attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
                return new HandlerResult.Fail(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
            }

            logger.LogWarning(
                logged, "Attempt {AttemptNo} failed ({ErrorCode}): {ErrorMessage}", attempt.AttemptNo, error.ErrorCode, error.ErrorMessage);
            try
            {
                await processing.RecordTransientFailureAsync(message.TaskId, attempt.AttemptId, error.ErrorCode, error.ErrorMessage, cancellationToken);
            }
            catch (Exception recordException)
            {
                // Database unavailable: the next delivery marks this attempt abandoned and starts a new one.
                logger.LogError(recordException, "Could not record the failure of attempt {AttemptNo}", attempt.AttemptNo);
            }

            return new HandlerResult.Retry(attempt.AttemptNo, error.ErrorCode, error.ErrorMessage, attempt.AttemptBudgetStart);
        }
    }

    public async Task<bool> RecordFinalFailureAsync(ReceivedMessage delivery, FinalFailure failure, CancellationToken cancellationToken)
    {
        currentUser.UserId = worker.Actor;
        var instanceId = workerOptions.Value.InstanceId;

        var taskId = _taskId;
        if (taskId is null)
        {
            // Called before HandleAsync (delivery-limit guard): identify the task from the message itself.
            if (!TaskRequestMessageParser.TryParse(delivery.Body, worker.EventType, out var message, out var rejection))
            {
                await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
                return true;
            }

            correlationContext.CorrelationId = message!.Envelope.CorrelationId;
            taskId = message.TaskId;
            if (RejectionOf(message.TaskId, await processing.GetTaskAsync(message.TaskId, cancellationToken)) is { } taskRejection)
            {
                await RejectAsync(message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, taskRejection, cancellationToken);
                return true;
            }
        }

        var attemptNo = await processing.RecordFinalFailureAsync(
            taskId.Value,
            _attemptId,
            failure.ErrorCode,
            failure.ErrorMessage,
            worker.Name,
            instanceId,
            failure.Queue,
            cancellationToken);

        // Null: the task was completed by another delivery meanwhile; nothing failed, the message can be acknowledged.
        return attemptNo is not null;
    }

    /// <summary>Why the task of a well-formed message cannot be processed by this queue; null when it can.</summary>
    private MessageRejection? RejectionOf(Guid taskId, TaskRequestState? task)
    {
        if (task is null)
        {
            return new(MessageRejectionCodes.UnknownTask, $"No processing task {taskId} exists.");
        }

        return task.TaskType != worker.TaskType || task.OrderId is null
            ? new(MessageRejectionCodes.TaskMismatch, $"Task {taskId} is not a task of type {worker.TaskType} with its order.")
            : null;
    }

    private async Task<HandlerResult> RejectAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        CancellationToken cancellationToken)
    {
        // If even the log row cannot be written (database down), the exception makes the consumer retry the message with delay.
        await processing.RecordRejectedMessageAsync(messageId, correlationId, taskId, rejection, worker.Name, cancellationToken);
        return new HandlerResult.Reject(rejection.Reason);
    }

    private IDisposable? BeginScope(TaskRequestMessage message, string instanceId) => logger.BeginScope(new Dictionary<string, object>
    {
        ["MessageId"] = message.Envelope.MessageId,
        ["TaskId"] = message.TaskId,
        ["CorrelationId"] = message.Envelope.CorrelationId,
        ["WorkerName"] = worker.Name,
        ["WorkerInstanceId"] = instanceId
    });
}

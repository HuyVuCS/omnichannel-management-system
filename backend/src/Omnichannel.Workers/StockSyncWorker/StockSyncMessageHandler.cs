using Microsoft.Extensions.Options;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.Processing;

namespace Omnichannel.Workers.StockSyncWorker;

/// <summary>The channel one stock-sync consumer serves.</summary>
public sealed record StockSyncChannel(string Slug, string Queue);

/// <summary>
/// Handles one delivery of a channel's stock-sync queue (doc §8.3). One instance per delivery, created by the consumer in the
/// delivery's DI scope, so the task/attempt of this delivery are remembered for <see cref="RecordFinalFailureAsync"/>.
/// <list type="bullet">
/// <item>Poison (unparseable, unknown event type, unknown task, task of another channel) → <c>Reject</c> after logging
/// <c>stock_sync.message_rejected</c>.</item>
/// <item><c>inventory.changed</c> for a product the channel does not sell (no Active mapping, channel Inactive) → <c>Ack</c>, no task.</item>
/// <item>Completed task (duplicate or late delivery) → <c>Ack</c>.</item>
/// <item>Otherwise an attempt: the <b>current</b> available stock is read and pushed; after the push it is read again and
/// pushed again while it differs (bounded), so the channel ends with the central value even when messages arrive out of
/// order or the stock changes during a push. No transaction and no lock is held while the channel is called.</item>
/// <item>Transient error → attempt Failed + task RetryScheduled, then <c>Retry</c> (delayed copy or final failure: the consumer
/// decides). Permanent error → <c>Fail</c>.</item>
/// <item>Host stopping during a push → the push is cancelled and the delivery is abandoned (not settled): the broker
/// redelivers it and the next delivery marks this attempt abandoned.</item>
/// </list>
/// </summary>
public sealed class StockSyncMessageHandler(
    StockSyncChannel channel,
    IStockSyncService stockSync,
    IChannelStockClient channelClient,
    WorkerCurrentUser currentUser,
    WorkerCorrelationContext correlationContext,
    IOptions<StockSyncWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<StockSyncMessageHandler> logger) : IQueueMessageHandler
{
    public const string WorkerName = "StockSyncWorker";
    public const string Actor = "system:stock-sync-worker";

    private Guid? _taskId;
    private Guid? _attemptId;

    // The final-failure callback runs after HandleAsync returned (its scopes are gone): it opens the same scope again.
    private Dictionary<string, object>? _logScope;

    public async Task<HandlerResult> HandleAsync(ReceivedMessage delivery, CancellationToken cancellationToken)
    {
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;
        var stopping = lifetime.ApplicationStopping;

        if (stopping.IsCancellationRequested)
        {
            // Nothing was started: leave the message for the next consumer instead of beginning an attempt that cannot finish.
            return HandlerResult.Abandon;
        }

        if (!StockSyncMessageParser.TryParse(delivery.Body, out var message, out var rejection))
        {
            return await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
        }

        correlationContext.CorrelationId = message!.Envelope.CorrelationId;
        _logScope = ScopeOf(message, instanceId);
        using var messageScope = logger.BeginScope(_logScope);

        var target = await ResolveAsync(message, cancellationToken);
        if (target.Rejection is not null)
        {
            return await RejectAsync(
                message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, target.Rejection, cancellationToken);
        }

        if (target.TaskId is not { } taskId || target.ChannelId is not { } channelId)
        {
            return HandlerResult.Ack;
        }

        _logScope = new Dictionary<string, object>(_logScope) { ["TaskId"] = taskId };
        using var taskScope = logger.BeginScope(new Dictionary<string, object> { ["TaskId"] = taskId });

        if (target.Status is ProcessingTaskStatus.Succeeded or ProcessingTaskStatus.Failed)
        {
            logger.LogInformation(
                "Duplicate delivery of message {MessageId}: task already {Status}; acknowledged", message.Envelope.MessageId, target.Status);
            return HandlerResult.Ack;
        }

        var attempt = await stockSync.StartAttemptAsync(taskId, message.Envelope.MessageId, WorkerName, instanceId, cancellationToken);
        if (attempt is null)
        {
            logger.LogInformation("Delivery of message {MessageId} starts no attempt: the task was completed meanwhile, or a manual retry replaced the message; acknowledged", message.Envelope.MessageId);
            return HandlerResult.Ack;
        }

        _taskId = taskId;
        _attemptId = attempt.AttemptId;

        _logScope = new Dictionary<string, object>(_logScope) { ["AttemptNo"] = attempt.AttemptNo };
        using var attemptScope = logger.BeginScope(new Dictionary<string, object> { ["AttemptNo"] = attempt.AttemptNo });
        try
        {
            await PushCurrentStockAsync(taskId, channelId, message.ProductId, attempt.AttemptNo, stopping, cancellationToken);
            await stockSync.CompleteAsync(taskId, attempt.AttemptId, cancellationToken);
            return HandlerResult.Ack;
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
            // An answer of the channel is fully described by its code and message; only unexpected errors need the stack.
            var logged = exception is ChannelApiException ? null : exception;
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
                await stockSync.RecordTransientFailureAsync(taskId, attempt.AttemptId, error.ErrorCode, error.ErrorMessage, cancellationToken);
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
        currentUser.UserId = Actor;
        var instanceId = workerOptions.Value.InstanceId;
        using var failureScope = _logScope is null ? null : logger.BeginScope(_logScope);

        var taskId = _taskId;
        if (taskId is null)
        {
            // Called before HandleAsync (delivery-limit guard) or before a task was known: identify it from the message itself.
            if (!StockSyncMessageParser.TryParse(delivery.Body, out var message, out var rejection))
            {
                await RejectAsync(delivery.MessageId, delivery.CorrelationId, null, rejection!, cancellationToken);
                return true;
            }

            correlationContext.CorrelationId = message!.Envelope.CorrelationId;
            using var messageScope = _logScope is null ? logger.BeginScope(ScopeOf(message, instanceId)) : null;
            var target = await ResolveAsync(message, cancellationToken);
            if (target.Rejection is not null)
            {
                await RejectAsync(
                    message.Envelope.MessageId, message.Envelope.CorrelationId, message.TaskId, target.Rejection, cancellationToken);
                return true;
            }

            if (target.TaskId is null)
            {
                // The channel does not sell the product: there is no task that could fail.
                return false;
            }

            taskId = target.TaskId;
        }

        var attemptNo = await stockSync.RecordFinalFailureAsync(
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

    /// <summary>
    /// Pushes the stock as it is now, never the snapshot of the message, and repeats while the stock changed during the push
    /// (at most <see cref="StockSyncWorkerOptions.MaxPushesPerAttempt"/> pushes; a later change has its own event anyway).
    /// </summary>
    private async Task PushCurrentStockAsync(
        Guid taskId,
        Guid channelId,
        Guid productId,
        int attemptNo,
        CancellationToken stopping,
        CancellationToken cancellationToken)
    {
        var maxPushes = options.Value.MaxPushesPerAttempt;
        int? pushed = null;

        for (var push = 1; ; push++)
        {
            var current = await stockSync.ReadTargetAsync(channelId, productId, cancellationToken);
            if (current is not { IsActive: true })
            {
                logger.LogInformation(
                    "Mapping or channel {Channel} is no longer active for product {ProductId}: nothing pushed ({Pushes} pushes before)",
                    channel.Slug, productId, push - 1);
                return;
            }

            if (pushed == current.AvailableStock)
            {
                return;
            }

            if (push > maxPushes)
            {
                logger.LogInformation(
                    "Available stock of {Sku} changed again to {AvailableStock} after {Pushes} pushes to {Channel}; left to the event of that change",
                    current.Sku, current.AvailableStock, maxPushes, channel.Slug);
                return;
            }

            // Only the external call is tied to the host stopping; database work of this delivery finishes.
            await channelClient.PushAvailableStockAsync(
                new ChannelStockUpdate(
                    current.ChannelSlug,
                    productId,
                    current.Sku,
                    current.ExternalProductId,
                    current.ExternalSku,
                    current.AvailableStock,
                    taskId,
                    attemptNo,
                    ChannelStockUpdate.RequestIdOf(taskId, attemptNo, push)),
                stopping);

            pushed = current.AvailableStock;
            logger.LogInformation(
                "Pushed available stock {AvailableStock} of {Sku} to {Channel} (attempt {AttemptNo}, push {Push})",
                current.AvailableStock, current.Sku, channel.Slug, attemptNo, push);
        }
    }

    /// <summary>The task of a message on this channel: found or created for <c>inventory.changed</c>, looked up for a directed message.</summary>
    private async Task<ResolvedTask> ResolveAsync(StockSyncMessage message, CancellationToken cancellationToken)
    {
        if (!message.IsDirected)
        {
            var resolution = await stockSync.ResolveInventoryChangeAsync(
                channel.Slug, message.ProductId, message.Envelope.MessageId, message.Envelope.CorrelationId, cancellationToken);

            switch (resolution.Kind)
            {
                case StockSyncResolutionKind.ChannelNotFound:
                    logger.LogWarning("No channel '{Channel}' exists for queue {Queue}; message acknowledged", channel.Slug, channel.Queue);
                    return ResolvedTask.None;
                case StockSyncResolutionKind.NotMapped:
                    logger.LogDebug("Product {ProductId} is not sold on channel {Channel}; message acknowledged", message.ProductId, channel.Slug);
                    return ResolvedTask.None;
                default:
                    return new ResolvedTask(resolution.TaskId, resolution.ChannelId, resolution.Status, null);
            }
        }

        var task = await stockSync.GetTaskAsync(message.TaskId!.Value, cancellationToken);
        if (task is null)
        {
            return ResolvedTask.Poison(MessageRejectionCodes.UnknownTask, $"No processing task {message.TaskId} exists.");
        }

        if (task.TaskType != ProcessingTaskType.StockSync || task.ProductId != message.ProductId || task.ChannelId != message.ChannelId)
        {
            return ResolvedTask.Poison(
                MessageRejectionCodes.TaskMismatch, $"Task {task.TaskId} is not a stock-sync task for the product and channel of the message.");
        }

        if (!string.Equals(task.ChannelSlug, channel.Slug, StringComparison.Ordinal))
        {
            return ResolvedTask.Poison(
                MessageRejectionCodes.TaskOfOtherChannel,
                $"Task {task.TaskId} belongs to channel '{task.ChannelSlug}', not to '{channel.Slug}' ({channel.Queue}).");
        }

        return new ResolvedTask(task.TaskId, task.ChannelId, task.Status, null);
    }

    private async Task<HandlerResult> RejectAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        MessageRejection rejection,
        CancellationToken cancellationToken)
    {
        // If even the log row cannot be written (database down), the exception makes the consumer retry the message with delay.
        await stockSync.RecordRejectedMessageAsync(messageId, correlationId, taskId, rejection, WorkerName, channel.Queue, cancellationToken);
        return new HandlerResult.Reject(rejection.Reason);
    }

    private Dictionary<string, object> ScopeOf(StockSyncMessage message, string instanceId) => new()
    {
        ["MessageId"] = message.Envelope.MessageId,
        ["CorrelationId"] = message.Envelope.CorrelationId,
        ["Channel"] = channel.Slug,
        ["ProductId"] = message.ProductId,
        ["WorkerName"] = WorkerName,
        ["WorkerInstanceId"] = instanceId
    };

    /// <param name="TaskId">Null when there is nothing to do (the channel does not sell the product).</param>
    private sealed record ResolvedTask(Guid? TaskId, Guid? ChannelId, ProcessingTaskStatus? Status, MessageRejection? Rejection)
    {
        public static ResolvedTask None { get; } = new(null, null, null, null);

        public static ResolvedTask Poison(string code, string reason) => new(null, null, null, new MessageRejection(code, reason));
    }
}

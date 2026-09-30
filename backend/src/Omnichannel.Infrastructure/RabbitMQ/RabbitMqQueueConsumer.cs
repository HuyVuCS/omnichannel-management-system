using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>
/// Consumes one queue with manual acknowledgements: prefetch = concurrency, up to <c>concurrency</c> handlers in parallel
/// (channel dispatch concurrency), one DI scope per delivery. Settlement (doc §8.3, CLAUDE.md §10):
/// <list type="bullet">
/// <item><c>Ack</c> → ACK (only after the handler returned, i.e. after it committed).</item>
/// <item><c>Reject</c> → reject without requeue → <c>&lt;queue&gt;.dlq</c>.</item>
/// <item><c>Retry</c> below <see cref="RetryPolicy.MaxAttempts"/> → delayed retry copy published with confirm, then ACK; if the
/// copy cannot be published → NACK with requeue (the message is not lost) after a bounded backoff (1 s doubling to 30 s per
/// consecutive failure of this consumer, reset when a copy is published), so a persistently unroutable copy cannot spin.</item>
/// <item><c>Retry</c> at the limit or <c>Fail</c> → the handler's final-failure callback, then reject → DLQ.</item>
/// <item>Last delivery the queue allows (<c>x-delivery-count</c>) → final-failure callback before processing (crash loops).</item>
/// <item>A handler exception → delayed copy counting the failures in <c>x-handler-failures</c>; at the limit a final failure.
/// (RabbitMQ 4 does not count <c>basic.nack</c> with requeue toward <c>x-delivery-limit</c>, so a plain requeue could loop forever.)</item>
/// </list>
/// No exception escapes the delivery callback.
/// </summary>
public sealed class RabbitMqQueueConsumer : IAsyncDisposable
{
    private const string DeliveryCountHeader = "x-delivery-count";

    /// <summary>Wait before a NACK-requeue when the retry copy could not be published (RabbitMQ 4 does not bound such loops).</summary>
    private static readonly TimeSpan RequeueInitialBackoff = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RequeueMaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>At most one error log per interval for failed retry-copy publishes; the others are counted and logged at Debug.</summary>
    private static readonly TimeSpan RequeueErrorLogInterval = TimeSpan.FromSeconds(30);

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly QueueConsumerSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRetryPublisher _retryPublisher;
    private readonly ILogger _logger;
    private readonly object _inFlightLock = new();
    private readonly TaskCompletionSource<string> _interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopped = new();
    private int _inFlight;
    private int _consecutiveRequeues;
    private int _suppressedRequeueErrors;
    private long _lastRequeueErrorLogAt; // Environment.TickCount64 of the last error log; 0 = never
    private TaskCompletionSource? _drained;
    private string? _consumerTag;
    private volatile bool _stopping;

    private RabbitMqQueueConsumer(
        IConnection connection,
        IChannel channel,
        QueueConsumerSettings settings,
        IServiceScopeFactory scopeFactory,
        IRetryPublisher retryPublisher,
        ILogger logger)
    {
        _connection = connection;
        _channel = channel;
        _settings = settings;
        _scopeFactory = scopeFactory;
        _retryPublisher = retryPublisher;
        _logger = logger;
    }

    /// <summary>The consumer channel (diagnostics and tests).</summary>
    public IChannel Channel => _channel;

    /// <summary>
    /// Completes with a reason when the consumer stopped receiving although the connection is still open (channel closed by a
    /// channel-level error, or consumer cancelled by the broker). Connection loss is handled by automatic recovery instead.
    /// </summary>
    public Task<string> Interrupted => _interrupted.Task;

    /// <summary>Opens a channel on the shared connection and starts consuming.</summary>
    public static async Task<RabbitMqQueueConsumer> StartAsync(
        RabbitMqConnectionProvider connectionProvider,
        QueueConsumerSettings settings,
        IServiceScopeFactory scopeFactory,
        IRetryPublisher retryPublisher,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(settings.Concurrency);

        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: settings.Concurrency),
            cancellationToken);

        try
        {
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: settings.Concurrency, global: false, cancellationToken);

            var consumer = new RabbitMqQueueConsumer(connection, channel, settings, scopeFactory, retryPublisher, logger);
            var basicConsumer = new AsyncEventingBasicConsumer(channel);
            basicConsumer.ReceivedAsync += (_, delivery) => consumer.OnReceivedAsync(delivery);
            basicConsumer.UnregisteredAsync += (_, _) => consumer.OnStoppedReceiving("consumer cancelled by the broker");
            channel.ChannelShutdownAsync += (_, reason) =>
                consumer.OnStoppedReceiving($"channel closed ({reason.ReplyCode} {reason.ReplyText})");

            consumer._consumerTag = await channel.BasicConsumeAsync(settings.Queue, autoAck: false, basicConsumer, cancellationToken);
            return consumer;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops receiving new messages, then waits until the handlers in progress have finished and settled.
    /// Messages that were never acknowledged are redelivered by the broker.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;

        // Deliveries waiting before a requeue give their message back at once.
        await _stopped.CancelAsync();
        if (_consumerTag is not null && _channel.IsOpen)
        {
            try
            {
                await _channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not cancel the consumer on {Queue}", _settings.Queue);
            }
        }

        Task drained;
        lock (_inFlightLock)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight == 0)
            {
                _drained.TrySetResult();
            }

            drained = _drained.Task;
        }

        await drained.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _stopped.CancelAsync();
        await _channel.DisposeAsync();
    }

    private Task OnStoppedReceiving(string reason)
    {
        // Our own stop, or the whole connection went away (automatic recovery restores channel and consumer).
        if (!_stopping && _connection.IsOpen)
        {
            _interrupted.TrySetResult(reason);
        }

        return Task.CompletedTask;
    }

    private async Task OnReceivedAsync(BasicDeliverEventArgs delivery)
    {
        lock (_inFlightLock)
        {
            _inFlight++;
        }

        try
        {
            var message = new ReceivedMessage(
                delivery.Body.ToArray(),
                delivery.BasicProperties.MessageId,
                delivery.BasicProperties.CorrelationId,
                delivery.Redelivered,
                delivery.RoutingKey,
                DeliveryCountOf(delivery.BasicProperties),
                delivery.BasicProperties);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = _settings.ResolveHandler(scope.ServiceProvider);
            var settlement = await DecideAsync(handler, message);
            await SettleAsync(delivery.DeliveryTag, settlement, message.MessageId);
        }
        catch (Exception exception)
        {
            // Never let an exception escape the callback; an unsettled message is redelivered by the broker.
            _logger.LogError(exception, "Unexpected error while handling a delivery from {Queue}", _settings.Queue);
        }
        finally
        {
            lock (_inFlightLock)
            {
                _inFlight--;
                if (_inFlight == 0)
                {
                    _drained?.TrySetResult();
                }
            }
        }
    }

    private async Task<Settlement> DecideAsync(IQueueMessageHandler handler, ReceivedMessage message)
    {
        // Handler work is not tied to host shutdown: an in-flight message finishes (bounded by the host shutdown timeout).
        var cancellationToken = CancellationToken.None;
        var policy = _settings.RetryPolicy;

        if (policy.IsLastAllowedDelivery(message.DeliveryCount))
        {
            // Crash loop (handler never returned, process died, ...): make it visible now instead of letting the broker
            // dead-letter the message silently after this delivery.
            _logger.LogError(
                "Message {MessageId} reached the delivery limit ({DeliveryCount}) on {Queue}; recording a final failure",
                message.MessageId, message.DeliveryCount, _settings.Queue);
            return await FinalizeAsync(handler, message, new FinalFailure(
                null,
                "delivery_limit_reached",
                $"Delivered {message.DeliveryCount + 1} times without being settled (crash loop or handler error).",
                _settings.Queue));
        }

        HandlerResult result;
        try
        {
            result = await handler.HandleAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            // No attempt could be recorded. RabbitMQ 4 does not count basic.nack(requeue) toward x-delivery-limit, so a plain
            // requeue could loop forever: count the failures in a header and come back through the delay tiers instead.
            var handlerFailures = HandlerFailuresOf(message) + 1;
            if (!policy.CanRetry(handlerFailures))
            {
                _logger.LogError(exception, "Handler for {Queue} threw on {Failures} deliveries of message {MessageId}; giving up",
                    _settings.Queue, handlerFailures, message.MessageId);
                return await FinalizeAsync(handler, message, new FinalFailure(
                    null, "handler_error", Describe(exception), _settings.Queue));
            }

            _logger.LogError(exception, "Handler for {Queue} threw on message {MessageId} ({Failures}); retrying with delay",
                _settings.Queue, message.MessageId, handlerFailures);
            return await PublishDelayedAsync(message, handlerFailures, new Dictionary<string, object?>
            {
                [RabbitMqRetryPublisher.HandlerFailuresHeader] = handlerFailures
            });
        }

        switch (result)
        {
            case HandlerResult.AckResult:
                return Settlement.Ack;

            case HandlerResult.Reject reject:
                _logger.LogError("Message {MessageId} rejected to the DLQ: {Reason}", message.MessageId, reject.Reason);
                return Settlement.Reject;

            case HandlerResult.Retry retry when policy.CanRetry(retry.AttemptInBudget):
                _logger.LogWarning(
                    "Attempt {AttemptNo} of message {MessageId} failed ({ErrorCode}); scheduling a delayed retry",
                    retry.AttemptNo, message.MessageId, retry.ErrorCode);
                // Delay tier by the position in the current budget: after a manual retry the short tiers apply again.
                return await PublishDelayedAsync(message, retry.AttemptInBudget, new Dictionary<string, object?>
                {
                    [RabbitMqRetryPublisher.RetryAttemptHeader] = retry.AttemptNo,
                    [RabbitMqRetryPublisher.LastErrorCodeHeader] = retry.ErrorCode
                });

            case HandlerResult.Retry retry:
                return await FinalizeAsync(handler, message, new FinalFailure(
                    retry.AttemptNo, retry.ErrorCode, retry.ErrorMessage, _settings.Queue));

            case HandlerResult.Fail fail:
                return await FinalizeAsync(handler, message, new FinalFailure(
                    fail.AttemptNo, fail.ErrorCode, fail.ErrorMessage, _settings.Queue));

            default:
                throw new InvalidOperationException($"Unknown handler result {result.GetType().Name}.");
        }
    }

    /// <summary>
    /// Final failure: visible first (handler callback), then the DLQ. If recording fails (e.g. database down), the message comes
    /// back through the delay tiers (no hot loop) and the final failure is recorded on a later delivery.
    /// </summary>
    private async Task<Settlement> FinalizeAsync(IQueueMessageHandler handler, ReceivedMessage message, FinalFailure failure)
    {
        try
        {
            var reject = await handler.RecordFinalFailureAsync(message, failure, CancellationToken.None);
            if (reject)
            {
                _logger.LogError(
                    "Message {MessageId} failed permanently ({ErrorCode}); moved to the DLQ",
                    message.MessageId, failure.ErrorCode);
            }

            return reject ? Settlement.Reject : Settlement.Ack;
        }
        catch (Exception exception)
        {
            var handlerFailures = HandlerFailuresOf(message) + 1;
            _logger.LogError(exception, "Could not record the final failure of message {MessageId}; retrying with delay", message.MessageId);
            return await PublishDelayedAsync(message, handlerFailures, new Dictionary<string, object?>
            {
                [RabbitMqRetryPublisher.HandlerFailuresHeader] = handlerFailures
            });
        }
    }

    /// <summary>
    /// Delayed copy with confirm, then ACK of the original. If the copy cannot be published (broker trouble, missing delay
    /// exchange), NACK with requeue so the message is not lost — after a bounded backoff, because RabbitMQ 4 does not count
    /// NACK-requeues toward <c>x-delivery-limit</c> and a persistently unroutable copy would otherwise spin.
    /// </summary>
    private async Task<Settlement> PublishDelayedAsync(ReceivedMessage message, int retryNumber, IReadOnlyDictionary<string, object?> headers)
    {
        try
        {
            await _retryPublisher.PublishDelayedAsync(_settings.Queue, message, retryNumber, headers, CancellationToken.None);
            Interlocked.Exchange(ref _consecutiveRequeues, 0);
            return Settlement.Ack;
        }
        catch (Exception exception)
        {
            var failures = Interlocked.Increment(ref _consecutiveRequeues);
            var delay = RetryBackoff.Compute(failures, RequeueInitialBackoff, RequeueMaxBackoff, Random.Shared.NextDouble());
            LogRequeue(exception, message.MessageId, failures, delay);

            try
            {
                await Task.Delay(delay, _stopped.Token);
            }
            catch (OperationCanceledException)
            {
                // Stopping: requeue now; the message is redelivered to the next consumer.
            }

            return Settlement.Requeue;
        }
    }

    /// <summary>Error at most once per <see cref="RequeueErrorLogInterval"/> (with the number suppressed since), Debug otherwise.</summary>
    private void LogRequeue(Exception exception, string? messageId, int consecutiveFailures, TimeSpan delay)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRequeueErrorLogAt);
        if ((last == 0 || now - last >= (long)RequeueErrorLogInterval.TotalMilliseconds)
            && Interlocked.CompareExchange(ref _lastRequeueErrorLogAt, now, last) == last)
        {
            var suppressed = Interlocked.Exchange(ref _suppressedRequeueErrors, 0);
            _logger.LogError(
                exception,
                "Could not publish the retry copy of message {MessageId} on {Queue} ({ConsecutiveFailures} consecutive, {Suppressed} not logged since the last error); requeued after {Delay}",
                messageId, _settings.Queue, consecutiveFailures, suppressed, delay);
            return;
        }

        Interlocked.Increment(ref _suppressedRequeueErrors);
        _logger.LogDebug(
            "Could not publish the retry copy of message {MessageId} on {Queue} ({ConsecutiveFailures} consecutive): {Error}; requeued after {Delay}",
            messageId, _settings.Queue, consecutiveFailures, exception.Message, delay);
    }

    private static int HandlerFailuresOf(ReceivedMessage message) =>
        message.Properties.Headers is { } headers
        && headers.TryGetValue(RabbitMqRetryPublisher.HandlerFailuresHeader, out var value)
        && value is not null
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    private static string Describe(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= 500 ? text : text[..500];
    }

    private async Task SettleAsync(ulong deliveryTag, Settlement settlement, string? messageId)
    {
        try
        {
            switch (settlement)
            {
                case Settlement.Ack:
                    await _channel.BasicAckAsync(deliveryTag, multiple: false);
                    break;
                case Settlement.Reject:
                    await _channel.BasicRejectAsync(deliveryTag, requeue: false);
                    break;
                default:
                    await _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true);
                    break;
            }
        }
        catch (Exception exception)
        {
            // Channel/connection gone: the broker redelivers the message; consumers are idempotent.
            _logger.LogWarning(exception, "Could not {Settlement} message {MessageId} on {Queue}", settlement, messageId, _settings.Queue);
        }
    }

    private static long DeliveryCountOf(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } headers && headers.TryGetValue(DeliveryCountHeader, out var value) && value is not null
            ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

    private enum Settlement
    {
        Ack,
        Reject,
        Requeue
    }
}

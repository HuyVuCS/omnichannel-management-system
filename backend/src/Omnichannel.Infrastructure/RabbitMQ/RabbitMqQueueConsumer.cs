using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>What the consumer does with a message after the handler returned.</summary>
public enum MessageDisposition
{
    /// <summary>Processed (state committed): acknowledge.</summary>
    Ack,

    /// <summary>Poison: reject without requeue, the queue dead-letters it to <c>&lt;queue&gt;.dlq</c>.</summary>
    Reject,

    /// <summary>Temporary failure: negative-acknowledge with requeue (bounded by the queue's delivery limit).</summary>
    Requeue
}

/// <summary>A delivered message. <see cref="Body"/> is a copy, valid after the callback.</summary>
public sealed record ReceivedMessage(
    ReadOnlyMemory<byte> Body,
    string? MessageId,
    string? CorrelationId,
    bool Redelivered,
    string RoutingKey);

/// <summary>
/// Consumes one queue with manual acknowledgements: prefetch = concurrency, up to <c>concurrency</c> handlers in parallel
/// (channel dispatch concurrency). The message is acknowledged only after the handler returned <see cref="MessageDisposition.Ack"/>
/// (i.e. after the handler committed its state). No exception escapes the delivery callback: a throwing handler means requeue.
/// </summary>
public sealed class RabbitMqQueueConsumer : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly ILogger _logger;
    private readonly string _queue;
    private readonly Func<ReceivedMessage, Task<MessageDisposition>> _handler;
    private readonly object _inFlightLock = new();
    private int _inFlight;
    private TaskCompletionSource? _drained;
    private string? _consumerTag;

    private RabbitMqQueueConsumer(
        IChannel channel,
        string queue,
        Func<ReceivedMessage, Task<MessageDisposition>> handler,
        ILogger logger)
    {
        _channel = channel;
        _queue = queue;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Opens a channel on the shared connection and starts consuming.</summary>
    public static async Task<RabbitMqQueueConsumer> StartAsync(
        RabbitMqConnectionProvider connectionProvider,
        string queue,
        ushort concurrency,
        Func<ReceivedMessage, Task<MessageDisposition>> handler,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(concurrency);

        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: concurrency),
            cancellationToken);

        try
        {
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: concurrency, global: false, cancellationToken);

            var consumer = new RabbitMqQueueConsumer(channel, queue, handler, logger);
            var basicConsumer = new AsyncEventingBasicConsumer(channel);
            basicConsumer.ReceivedAsync += (_, delivery) => consumer.OnReceivedAsync(delivery);
            consumer._consumerTag = await channel.BasicConsumeAsync(queue, autoAck: false, basicConsumer, cancellationToken);
            return consumer;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops receiving new messages, then waits until the handlers in progress have finished and acknowledged.
    /// Messages that were never acknowledged are redelivered by the broker.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumerTag is not null && _channel.IsOpen)
        {
            try
            {
                await _channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not cancel the consumer on {Queue}", _queue);
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

    public async ValueTask DisposeAsync() => await _channel.DisposeAsync();

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
                delivery.RoutingKey);

            MessageDisposition disposition;
            try
            {
                disposition = await _handler(message);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Handler for {Queue} threw; message {MessageId} requeued", _queue, message.MessageId);
                disposition = MessageDisposition.Requeue;
            }

            await SettleAsync(delivery.DeliveryTag, disposition, message.MessageId);
        }
        catch (Exception exception)
        {
            // Never let an exception escape the callback; an unsettled message is redelivered by the broker.
            _logger.LogError(exception, "Unexpected error while handling a delivery from {Queue}", _queue);
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

    private async Task SettleAsync(ulong deliveryTag, MessageDisposition disposition, string? messageId)
    {
        try
        {
            switch (disposition)
            {
                case MessageDisposition.Ack:
                    await _channel.BasicAckAsync(deliveryTag, multiple: false);
                    break;
                case MessageDisposition.Reject:
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
            _logger.LogWarning(exception, "Could not {Disposition} message {MessageId} on {Queue}", disposition, messageId, _queue);
        }
    }
}

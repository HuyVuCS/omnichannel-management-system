using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>
/// Keeps one <see cref="RabbitMqQueueConsumer"/> running for the lifetime of a worker: starts it (with backoff while RabbitMQ is
/// unavailable), re-creates it when its channel is closed or the broker cancels it while the connection stays open, and stops
/// it gracefully (in-flight messages finish) on shutdown. Connection loss is left to the client's automatic recovery.
/// </summary>
public sealed class ResilientQueueConsumer(
    RabbitMqConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    IRetryPublisher retryPublisher,
    TimeProvider timeProvider,
    ILogger<ResilientQueueConsumer> logger)
{
    private volatile RabbitMqQueueConsumer? _current;

    /// <summary>The active consumer, if any (diagnostics and tests).</summary>
    public RabbitMqQueueConsumer? Current => _current;

    /// <summary>Number of times the consumer was re-created after an interruption.</summary>
    public int Restarts { get; private set; }

    /// <summary>Runs until <paramref name="stoppingToken"/> is cancelled.</summary>
    /// <exception cref="RabbitMqTopologyMismatchException">The broker's topology conflicts; the host must stop.</exception>
    public async Task RunAsync(QueueConsumerSettings settings, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            RabbitMqQueueConsumer consumer;
            try
            {
                consumer = await RabbitMqQueueConsumer.StartAsync(
                    connectionProvider, settings, scopeFactory, retryPublisher, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not RabbitMqTopologyMismatchException)
            {
                failures++;
                if (!await DelayAsync(settings, failures, stoppingToken, exception, "Cannot start consuming {Queue}; next try in {Delay}"))
                {
                    return;
                }

                continue;
            }

            _current = consumer;
            logger.LogInformation("Consuming {Queue} with concurrency {Concurrency}", settings.Queue, settings.Concurrency);

            await using (consumer)
            {
                var stopped = Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
                var finished = await Task.WhenAny(stopped, consumer.Interrupted);

                // Stop receiving, then wait for in-flight handlers (the host abandons the wait after its shutdown timeout).
                await consumer.StopAsync(CancellationToken.None);
                _current = null;

                if (finished == stopped)
                {
                    logger.LogInformation("Stopped consuming {Queue}", settings.Queue);
                    return;
                }

                Restarts++;
                failures = 1;
                logger.LogWarning(
                    "Consumer on {Queue} stopped receiving ({Reason}); re-creating it",
                    settings.Queue,
                    await consumer.Interrupted);
            }

            if (!await DelayAsync(settings, failures, stoppingToken, null, "Re-creating the consumer on {Queue} in {Delay}"))
            {
                return;
            }
        }
    }

    private async Task<bool> DelayAsync(
        QueueConsumerSettings settings,
        int failures,
        CancellationToken stoppingToken,
        Exception? exception,
        string message)
    {
        var delay = RetryBackoff.Compute(failures, settings.InitialBackoff, settings.MaxBackoff, Random.Shared.NextDouble());
        logger.LogWarning(exception, message, settings.Queue, delay);
        try
        {
            await Task.Delay(delay, timeProvider, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

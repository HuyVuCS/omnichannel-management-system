using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.OutboxPublisher;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>
/// Consumes <c>order.processing</c> (doc §8.2). Starts the consumer (retrying with backoff while RabbitMQ is unavailable),
/// and on shutdown cancels it and lets in-flight messages finish; unacknowledged messages are redelivered by the broker.
/// </summary>
/// <remarks>
/// Interim retry policy until task 007: a temporary failure is nacked with requeue, i.e. redelivered immediately, bounded by
/// the queue's <c>x-delivery-limit</c> (10), after which the broker dead-letters the message to <c>order.processing.dlq</c>.
/// </remarks>
public sealed class OrderWorkerService(
    RabbitMqConnectionProvider connectionProvider,
    OrderMessageHandler handler,
    IOptions<OrderWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<OrderWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = OrderMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        RabbitMqQueueConsumer? consumer = null;
        for (var failures = 1; consumer is null && !stoppingToken.IsCancellationRequested; failures++)
        {
            try
            {
                consumer = await RabbitMqQueueConsumer.StartAsync(
                    connectionProvider,
                    RabbitMqTopology.OrderProcessingQueue,
                    settings.Concurrency,
                    handler.HandleAsync,
                    logger,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (RabbitMqTopologyMismatchException exception)
            {
                logger.LogCritical(exception, "RabbitMQ topology mismatch; stopping the Workers host");
                Environment.ExitCode = 1;
                lifetime.StopApplication();
                return;
            }
            catch (Exception exception)
            {
                var delay = OutboxBackoff.Compute(failures, settings.InitialBackoff, settings.MaxBackoff, Random.Shared.NextDouble());
                logger.LogWarning(exception, "Cannot start consuming {Queue}; next try in {Delay}", RabbitMqTopology.OrderProcessingQueue, delay);
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (consumer is null)
        {
            return;
        }

        await using (consumer)
        {
            logger.LogInformation(
                "Order worker consuming {Queue} with concurrency {Concurrency}",
                RabbitMqTopology.OrderProcessingQueue,
                settings.Concurrency);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested.
            }

            // Stop receiving, then wait for in-flight handlers (the host abandons the wait after its shutdown timeout).
            await consumer.StopAsync(CancellationToken.None);
            logger.LogInformation("Order worker stopped");
        }
    }
}

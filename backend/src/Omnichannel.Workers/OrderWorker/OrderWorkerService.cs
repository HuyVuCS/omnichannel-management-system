using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>
/// Consumes <c>order.processing</c> (doc §8.2) through a <see cref="ResilientQueueConsumer"/>: started with backoff while
/// RabbitMQ is unavailable, re-created when its channel closes, stopped gracefully on shutdown. Temporary failures are retried
/// with delay (5 s / 30 s / 2 min / 10 min tiers) up to <see cref="OrderWorkerOptions.MaxAttempts"/>, then the task is failed and
/// the message goes to <c>order.processing.dlq</c> (doc §8.3).
/// </summary>
public sealed class OrderWorkerService(
    ResilientQueueConsumer consumer,
    IOptions<OrderWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<OrderWorkerService> logger) : BackgroundService
{
    /// <summary>The underlying consumer (diagnostics and tests).</summary>
    public ResilientQueueConsumer Consumer => consumer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = OrderMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        try
        {
            await consumer.RunAsync(
                new QueueConsumerSettings(
                    RabbitMqTopology.OrderProcessingQueue,
                    settings.Concurrency,
                    new RetryPolicy(settings.MaxAttempts, RabbitMqTopology.DeliveryLimit),
                    services => services.GetRequiredService<OrderMessageHandler>(),
                    settings.InitialBackoff,
                    settings.MaxBackoff),
                stoppingToken);
        }
        catch (RabbitMqTopologyMismatchException exception)
        {
            logger.LogCritical(exception, "RabbitMQ topology mismatch; stopping the Workers host");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}

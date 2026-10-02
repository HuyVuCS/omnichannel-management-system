using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.StockSyncWorker;

/// <summary>
/// Pushes stock to the channels (doc §8.3): one consumer per stock-sync queue (<see cref="RabbitMqTopology.StockSyncQueues"/>),
/// each bound to its channel, so a slow or failing channel never holds back the others. Every consumer is a
/// <see cref="ResilientQueueConsumer"/>: started with backoff while RabbitMQ is unavailable, re-created when its channel closes,
/// stopped gracefully on shutdown. Temporary failures are retried with delay (5 s / 30 s / 2 min / 10 min tiers) up to
/// <see cref="StockSyncWorkerOptions.MaxAttempts"/>, then the task is failed and the message goes to <c>&lt;queue&gt;.dlq</c>.
/// </summary>
public sealed class StockSyncWorkerService(
    IServiceProvider services,
    IOptions<StockSyncWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<StockSyncWorkerService> logger) : BackgroundService
{
    private static readonly ObjectFactory<StockSyncMessageHandler> CreateHandler =
        ActivatorUtilities.CreateFactory<StockSyncMessageHandler>([typeof(StockSyncChannel)]);

    // ResilientQueueConsumer is registered transient: one instance per queue.
    private readonly IReadOnlyList<(StockSyncChannel Channel, ResilientQueueConsumer Consumer)> _consumers = RabbitMqTopology.StockSyncQueues
        .Select(entry => (new StockSyncChannel(entry.Key, entry.Value), services.GetRequiredService<ResilientQueueConsumer>()))
        .ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = StockSyncMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        try
        {
            await Task.WhenAll(_consumers.Select(consumer => consumer.Consumer.RunAsync(
                new QueueConsumerSettings(
                    consumer.Channel.Queue,
                    settings.Concurrency,
                    new RetryPolicy(settings.MaxAttempts, RabbitMqTopology.DeliveryLimit),
                    scopedServices => CreateHandler(scopedServices, [consumer.Channel]),
                    settings.InitialBackoff,
                    settings.MaxBackoff),
                stoppingToken)));
        }
        catch (RabbitMqTopologyMismatchException exception)
        {
            logger.LogCritical(exception, "RabbitMQ topology mismatch; stopping the Workers host");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}

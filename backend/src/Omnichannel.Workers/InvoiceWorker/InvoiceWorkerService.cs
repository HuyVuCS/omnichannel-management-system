using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.InvoiceWorker;

/// <summary>
/// Consumes <c>invoice.processing</c> through a <see cref="ResilientQueueConsumer"/>: started with backoff while
/// RabbitMQ is unavailable, re-created when its channel closes, stopped gracefully on shutdown. Temporary failures (the
/// provider, the database) are retried with delay (5 s / 30 s / 2 min / 10 min tiers) up to
/// <see cref="Processing.ProviderWorkerOptions.MaxAttempts"/>, then the task is failed and the message goes to
/// <c>invoice.processing.dlq</c>.
/// </summary>
public sealed class InvoiceWorkerService(
    ResilientQueueConsumer consumer,
    IOptions<InvoiceWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<InvoiceWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = InvoiceMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        try
        {
            await consumer.RunAsync(
                new QueueConsumerSettings(
                    RabbitMqTopology.InvoiceProcessingQueue,
                    settings.Concurrency,
                    new RetryPolicy(settings.MaxAttempts, RabbitMqTopology.DeliveryLimit),
                    services => services.GetRequiredService<InvoiceMessageHandler>(),
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

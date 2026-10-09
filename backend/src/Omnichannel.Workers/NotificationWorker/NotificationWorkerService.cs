using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.NotificationWorker;

/// <summary>
/// Consumes <c>notification.email</c> through a <see cref="ResilientQueueConsumer"/>: started with backoff while
/// RabbitMQ is unavailable, re-created when its channel closes, stopped gracefully on shutdown. Temporary failures (the
/// mail server, the database) are retried with delay (5 s / 30 s / 2 min / 10 min tiers) up to
/// <see cref="Processing.ProviderWorkerOptions.MaxAttempts"/>, then the task is failed and the message goes to
/// <c>notification.email.dlq</c>.
/// </summary>
public sealed class NotificationWorkerService(
    ResilientQueueConsumer consumer,
    IOptions<NotificationWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<NotificationWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = EmailMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        try
        {
            await consumer.RunAsync(
                new QueueConsumerSettings(
                    RabbitMqTopology.EmailNotificationQueue,
                    settings.Concurrency,
                    new RetryPolicy(settings.MaxAttempts, RabbitMqTopology.DeliveryLimit),
                    services => services.GetRequiredService<EmailMessageHandler>(),
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

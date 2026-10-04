using Microsoft.Extensions.Options;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers.ReportExportWorker;

/// <summary>
/// Consumes <c>report.export</c> through a <see cref="ResilientQueueConsumer"/>: started with backoff while RabbitMQ is
/// unavailable, re-created when its channel closes, stopped gracefully on shutdown. Temporary failures (object storage,
/// database) are retried with delay (5 s / 30 s / 2 min / 10 min tiers) up to
/// <see cref="ReportExportWorkerOptions.MaxAttempts"/>, then the task is failed and the message goes to
/// <c>report.export.dlq</c>. One export at a time per instance by default; several instances may run.
/// </summary>
public sealed class ReportExportWorkerService(
    ResilientQueueConsumer consumer,
    IOptions<ReportExportWorkerOptions> options,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<ReportExportWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = ReportExportMessageHandler.WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        var settings = options.Value;
        try
        {
            await consumer.RunAsync(
                new QueueConsumerSettings(
                    RabbitMqTopology.ReportExportQueue,
                    settings.Concurrency,
                    new RetryPolicy(settings.MaxAttempts, RabbitMqTopology.DeliveryLimit),
                    services => services.GetRequiredService<ReportExportMessageHandler>(),
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

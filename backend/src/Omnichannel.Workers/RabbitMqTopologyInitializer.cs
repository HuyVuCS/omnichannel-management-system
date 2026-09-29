using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Workers;

/// <summary>
/// Registered before every publisher/consumer: connects and declares the topology at start-up.
/// A topology mismatch stops the host (clear log, non-zero exit). An unreachable broker does not: the host starts and
/// the publisher keeps retrying, so rows simply stay Pending until the broker is back.
/// </summary>
public sealed class RabbitMqTopologyInitializer(
    RabbitMqConnectionProvider connectionProvider,
    ILogger<RabbitMqTopologyInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await connectionProvider.GetConnectionAsync(cancellationToken);
        }
        catch (RabbitMqTopologyMismatchException exception)
        {
            logger.LogCritical(exception, "RabbitMQ topology mismatch at start-up; the Workers host will not start");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "RabbitMQ is not reachable at start-up; the topology is declared on the first successful connection");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>
/// One long-lived connection per host (singleton). Connects lazily and declares the topology on the first successful
/// connection; after that the client's automatic recovery reconnects (and re-declares recorded topology) on its own.
/// If the broker is unreachable, <see cref="GetConnectionAsync"/> throws and the next call tries again.
/// </summary>
public sealed class RabbitMqConnectionProvider(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnectionProvider> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnection? _connection;

    /// <exception cref="RabbitMqTopologyMismatchException">The broker's topology conflicts with <see cref="RabbitMqTopology"/>.</exception>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        // A recovering connection is kept: automatic recovery will bring it back; channel creation fails meanwhile.
        if (_connection is not null)
        {
            return _connection;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.HostName,
                Port = settings.Port,
                VirtualHost = settings.VirtualHost,
                UserName = settings.UserName,
                Password = settings.Password,
                ClientProvidedName = settings.ClientProvidedName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = settings.NetworkRecoveryInterval
            };

            var connection = await factory.CreateConnectionAsync(cancellationToken);
            try
            {
                await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
                await RabbitMqTopology.DeclareAsync(channel, settings.EffectiveRetryDelays, cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            logger.LogInformation(
                "Connected to RabbitMQ {HostName}:{Port}{VirtualHost}; topology declared",
                settings.HostName,
                settings.Port,
                settings.VirtualHost);

            _connection = connection;
            return connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _connectLock.Dispose();
    }
}

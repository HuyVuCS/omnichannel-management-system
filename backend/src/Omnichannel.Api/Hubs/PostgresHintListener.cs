using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Realtime;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Api.Hubs;

/// <summary>
/// Receives the hints every committed unit of work sends with <c>pg_notify</c> (API and Workers alike) and pushes them to the
/// browsers connected to <b>this</b> API instance. Each instance listens for itself, so several instances need no SignalR
/// backplane.
/// <list type="bullet">
/// <item>Its own connection (not from the pool, not the <c>DbContext</c>), opened with the application role:
/// <c>LISTEN</c> needs no grant.</item>
/// <item>Connection lost → Warning, reconnect with exponential backoff (1 s doubling to 30 s). Notifications sent while it
/// was away are gone, so after every reconnect the clients get <c>resync</c>.</item>
/// <item>A payload that is not a hint batch is skipped with a Warning (its length is logged, not its content).</item>
/// </list>
/// </summary>
public sealed class PostgresHintListener(
    IOptions<DatabaseOptions> database,
    RealtimeHintBatcher batcher,
    IHubContext<NotificationsHub> hub,
    TimeProvider timeProvider,
    ILogger<PostgresHintListener> logger) : BackgroundService
{
    /// <summary><c>application_name</c> of the listener's connection (visible in <c>pg_stat_activity</c>).</summary>
    public const string ApplicationName = "omnichannel-hint-listener";

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>True while the <c>LISTEN</c> connection is established (diagnostics and tests).</summary>
    public bool IsListening { get; private set; }

    /// <summary>Number of reconnects after a lost connection (diagnostics and tests).</summary>
    public int Reconnects { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(ListenAsync(stoppingToken), batcher.RunAsync(SendAsync, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        // Built from the configured connection string; never logged (it contains the password).
        var connectionString = new NpgsqlConnectionStringBuilder(database.Value.ConnectionString)
        {
            Pooling = false,
            // Seconds between keep-alives while waiting: a dead connection is noticed instead of waiting forever.
            KeepAlive = 30,
            ApplicationName = ApplicationName
        }.ConnectionString;

        var connectedBefore = false;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += OnNotification;
                await connection.OpenAsync(stoppingToken);
                await using (var listen = new NpgsqlCommand($"LISTEN {RealtimeChannel.Name}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                failures = 0;
                IsListening = true;
                if (connectedBefore)
                {
                    Reconnects++;
                    logger.LogInformation("Listening for realtime hints again; clients are told to resync");
                    batcher.RequestResync();
                }
                else
                {
                    logger.LogInformation("Listening for realtime hints on channel {Channel}", RealtimeChannel.Name);
                }

                connectedBefore = true;
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                IsListening = false;
                failures++;
                var delay = RetryBackoff.Compute(failures, InitialBackoff, MaxBackoff, Random.Shared.NextDouble());
                // The message of a database exception does not contain the connection string.
                logger.LogWarning(
                    "Realtime hint listener lost its connection ({ErrorType}: {Error}); next try in {Delay}",
                    exception.GetType().Name, exception.Message, delay);
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        IsListening = false;
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs notification)
    {
        if (notification.Channel != RealtimeChannel.Name)
        {
            return;
        }

        if (RealtimeHintBatch.TryParse(notification.Payload) is not { } batch)
        {
            logger.LogWarning("Skipped a notification that is not a hint batch ({PayloadLength} characters)", notification.Payload.Length);
            return;
        }

        logger.LogDebug("Hints received: {EventTypes}", string.Join(", ", batch.Events.Select(e => e.Type)));
        batcher.Add(batch.Events);
    }

    private async Task SendAsync(RealtimeHintMessage message, CancellationToken cancellationToken)
    {
        await hub.Clients.All.SendAsync(RealtimeChannel.HubMethod, message, cancellationToken);
        logger.LogDebug(
            "Hint batch sent to the clients: {EventCount} events ({EventTypes}), resync {Resync}",
            message.Events.Count, string.Join(", ", message.Events.Select(e => e.Type).Distinct()), message.Resync);
    }
}

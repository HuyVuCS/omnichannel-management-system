using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Omnichannel.Workers.OutboxPublisher;

/// <summary>
/// Publishes pending <see cref="OutboxEvent"/> rows to RabbitMQ (doc §8.3).
/// <list type="bullet">
/// <item>Each batch is locked with <c>FOR UPDATE SKIP LOCKED</c> inside a transaction, so several instances never publish
/// the same row concurrently.</item>
/// <item>A row becomes <c>Published</c> only after a positive publisher confirm for a mandatory publish that was not returned.</item>
/// <item>Failures are retried with exponential backoff; after <see cref="OutboxPublisherOptions.MaxAttempts"/> the row is
/// <c>Failed</c> and a <c>SystemEventLog</c> error is written. Nothing retries forever.</item>
/// </list>
/// <para>
/// <b>Delivery is at-least-once.</b> If the process dies (or the commit fails) after the broker confirmed a message but before
/// the batch is committed, the row is still Pending and is published again later. Consumers must deduplicate by
/// <c>messageId</c> (= <c>OutboxEventId</c>).
/// </para>
/// </summary>
public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnectionProvider connectionProvider,
    IOptions<OutboxPublisherOptions> publisherOptions,
    IOptions<RabbitMqOptions> rabbitMqOptions,
    IOptions<WorkerOptions> workerOptions,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    public const string WorkerName = "OutboxPublisher";
    public const string PublishFailedEventType = "outbox.publish_failed";
    private const string JsonContentType = "application/json";

    private readonly OutboxPublisherOptions _options = publisherOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkerName"] = WorkerName,
            ["WorkerInstanceId"] = workerOptions.Value.InstanceId
        });

        logger.LogInformation(
            "Outbox publisher started (batch {BatchSize}, poll {PollInterval}, max attempts {MaxAttempts})",
            _options.BatchSize,
            _options.PollInterval,
            _options.MaxAttempts);

        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rowCount = await PublishBatchAsync(stoppingToken);
                consecutiveErrors = 0;

                if (rowCount < _options.BatchSize)
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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
                // E.g. database unavailable. Never let the loop die; back off instead of spinning.
                consecutiveErrors++;
                var delay = OutboxBackoff.Compute(consecutiveErrors, _options.PollInterval, _options.MaxBackoff, Random.Shared.NextDouble());
                logger.LogError(exception, "Outbox publish batch failed; next try in {Delay}", delay);

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

        logger.LogInformation("Outbox publisher stopped");
    }

    /// <returns>Number of rows processed (published or failed) in this batch.</returns>
    private async Task<int> PublishBatchAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

        var now = timeProvider.GetUtcNow();
        var rows = await dbContext.OutboxEvents
            .FromSql($"""
                SELECT * FROM "OutboxEvents"
                WHERE "Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
                ORDER BY "CreatedAt"
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(stoppingToken);

        if (rows.Count == 0)
        {
            return 0;
        }

        var errors = await PublishAsync(rows, stoppingToken);
        var completedAt = timeProvider.GetUtcNow();

        foreach (var row in rows)
        {
            if (errors[row.OutboxEventId] is not { } error)
            {
                row.MarkPublished(completedAt);
                logger.LogDebug(
                    "Published {EventType} {MessageId} (correlation {CorrelationId})",
                    row.EventType, row.OutboxEventId, row.CorrelationId);
                continue;
            }

            var delay = OutboxBackoff.Compute(
                row.PublishAttempts + 1, _options.InitialBackoff, _options.MaxBackoff, Random.Shared.NextDouble());

            if (row.RecordPublishFailure(error, completedAt, delay, _options.MaxAttempts))
            {
                logger.LogError(
                    "Giving up on {EventType} {MessageId} (correlation {CorrelationId}) after {Attempts} attempts: {Error}",
                    row.EventType, row.OutboxEventId, row.CorrelationId, row.PublishAttempts, row.LastError);

                dbContext.SystemEventLogs.Add(new SystemEventLog(
                    SystemEventLevel.Error,
                    PublishFailedEventType,
                    row.CorrelationId,
                    taskId: MessageEnvelope.FromOutboxEvent(row).TaskId,
                    orderId: null,
                    WorkerName,
                    MessageJson.Serialize(new
                    {
                        outboxEventId = row.OutboxEventId,
                        eventType = row.EventType,
                        publishAttempts = row.PublishAttempts,
                        lastError = row.LastError
                    }),
                    completedAt));
            }
            else
            {
                logger.LogWarning(
                    "Publishing {EventType} {MessageId} (correlation {CorrelationId}) failed, attempt {Attempt}; retry at {NextAttemptAt}: {Error}",
                    row.EventType, row.OutboxEventId, row.CorrelationId, row.PublishAttempts, row.NextAttemptAt, row.LastError);
            }
        }

        // The messages are already out: finish the batch even if shutdown was requested meanwhile.
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return rows.Count;
    }

    /// <returns>Per row: null when confirmed by the broker, otherwise a sanitised error.</returns>
    private async Task<Dictionary<Guid, string?>> PublishAsync(IReadOnlyList<OutboxEvent> rows, CancellationToken stoppingToken)
    {
        IChannel channel;
        try
        {
            var connection = await connectionProvider.GetConnectionAsync(stoppingToken);
            channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not RabbitMqTopologyMismatchException)
        {
            // Broker unreachable or connection recovering: every row of the batch counts one failed attempt.
            var error = Describe(exception, timedOut: false);
            return rows.ToDictionary(r => r.OutboxEventId, _ => (string?)error);
        }

        await using (channel)
        {
            using var confirmTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            confirmTimeout.CancelAfter(_options.ConfirmTimeout);

            // Publish the whole batch, then await the confirms (the client tracks them per message).
            var publishes = rows.Select(row => (row, task: PublishOneAsync(channel, row, confirmTimeout.Token))).ToList();

            var results = new Dictionary<Guid, string?>(rows.Count);
            foreach (var (row, task) in publishes)
            {
                try
                {
                    await task;
                    results[row.OutboxEventId] = null;
                }
                catch (Exception exception)
                {
                    results[row.OutboxEventId] = Describe(exception, confirmTimeout.IsCancellationRequested);
                }
            }

            // Shutdown mid-batch: roll back (rows stay Pending; confirmed ones are republished later, at-least-once).
            stoppingToken.ThrowIfCancellationRequested();
            return results;
        }
    }

    private static async Task PublishOneAsync(IChannel channel, OutboxEvent row, CancellationToken cancellationToken)
    {
        var envelope = MessageEnvelope.FromOutboxEvent(row);
        var properties = new BasicProperties
        {
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            Type = envelope.EventType,
            ContentType = JsonContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds())
        };

        await channel.BasicPublishAsync(
            RabbitMqTopology.Exchange,
            routingKey: envelope.EventType,
            mandatory: true,
            properties,
            MessageJson.SerializeToUtf8Bytes(envelope),
            cancellationToken);
    }

    /// <summary>Error text stored in <c>LastError</c>: no stack traces, never the broker password.</summary>
    private string Describe(Exception exception, bool timedOut)
    {
        var message = exception switch
        {
            PublishReturnException returned =>
                $"Returned by the broker (unroutable): {returned.ReplyCode} {returned.ReplyText}, routing key '{returned.RoutingKey}'.",
            PublishException => "Negatively acknowledged (nack) by the broker.",
            OperationCanceledException when timedOut => $"No publisher confirm within {_options.ConfirmTimeout}.",
            _ => $"{exception.GetType().Name}: {exception.Message}"
                + (exception.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : string.Empty)
        };

        var password = rabbitMqOptions.Value.Password;
        return string.IsNullOrEmpty(password) ? message : message.Replace(password, "***", StringComparison.Ordinal);
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>Publishes the delayed retry copy of a message.</summary>
public interface IRetryPublisher
{
    /// <summary>
    /// Publishes a copy of <paramref name="message"/> to the delay tier for <paramref name="retryNumber"/> (1 → first tier, ...)
    /// with routing key = <paramref name="queue"/>, carrying <paramref name="headers"/>, and returns after a positive publisher
    /// confirm. Throws when the copy was not confirmed.
    /// </summary>
    Task PublishDelayedAsync(
        string queue,
        ReceivedMessage message,
        int retryNumber,
        IReadOnlyDictionary<string, object?> headers,
        CancellationToken cancellationToken);
}

/// <summary>
/// Retry copies: same body and properties (MessageId/CorrelationId unchanged) with the given headers (<c>x-retry-attempt</c>,
/// <c>x-last-error-code</c> or <c>x-handler-failures</c>); broker headers (<c>x-death</c>, <c>x-delivery-count</c>, ...) are not copied.
/// </summary>
public sealed class RabbitMqRetryPublisher(
    RabbitMqConnectionProvider connectionProvider,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqRetryPublisher> logger) : IRetryPublisher
{
    /// <summary>The failed attempt (from the database) this copy retries.</summary>
    public const string RetryAttemptHeader = "x-retry-attempt";

    public const string LastErrorCodeHeader = "x-last-error-code";

    /// <summary>How many deliveries of this message made the handler throw (no attempt could be recorded).</summary>
    public const string HandlerFailuresHeader = "x-handler-failures";

    public async Task PublishDelayedAsync(
        string queue,
        ReceivedMessage message,
        int retryNumber,
        IReadOnlyDictionary<string, object?> headers,
        CancellationToken cancellationToken)
    {
        var delay = RabbitMqTopology.RetryDelayFor(retryNumber, options.Value.EffectiveRetryDelays);
        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await channel.BasicPublishAsync(
            RabbitMqTopology.DelayNameOf(delay),
            routingKey: queue,
            mandatory: true,
            CreateRetryProperties(message.Properties, headers),
            message.Body,
            cancellationToken);

        logger.LogInformation(
            "Retry {RetryNumber} of message {MessageId}: waits {Delay} in {DelayQueue}, then returns to {Queue}",
            retryNumber, message.MessageId, delay, RabbitMqTopology.DelayNameOf(delay), queue);
    }

    public static BasicProperties CreateRetryProperties(IReadOnlyBasicProperties original, IReadOnlyDictionary<string, object?> retryHeaders)
    {
        var headers = new Dictionary<string, object?>();
        foreach (var (key, value) in original.Headers ?? new Dictionary<string, object?>())
        {
            // Broker/client-managed headers (x-death, x-delivery-count, x-dotnet-pub-seq-no, ...) belong to the original.
            if (!key.StartsWith("x-", StringComparison.Ordinal))
            {
                headers[key] = value;
            }
        }

        foreach (var (key, value) in retryHeaders)
        {
            headers[key] = value;
        }

        return new BasicProperties
        {
            MessageId = original.MessageId,
            CorrelationId = original.CorrelationId,
            Type = original.Type,
            ContentType = original.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = original.Timestamp,
            Headers = headers
        };
    }
}

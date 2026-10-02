using Omnichannel.Application.Common.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Omnichannel.Infrastructure.RabbitMQ;

/// <summary>
/// The complete broker topology (doc §10). Declared idempotently by the Workers host; the definitions here are the
/// single source of truth. Quorum-queue arguments cannot be changed on an existing queue: a mismatch raises
/// <see cref="RabbitMqTopologyMismatchException"/> and queues are never deleted automatically.
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = "omnichannel.topic";
    public const string DeadLetterExchange = "omnichannel.dlx";

    public const string OrderProcessingQueue = "order.processing";
    public const string StockSyncWebsiteQueue = "stock.sync.website";
    public const string StockSyncPosQueue = "stock.sync.pos";

    /// <summary>Stock sync for the <c>digital-platform</c> channel.</summary>
    public const string StockSyncMarketplaceQueue = "stock.sync.marketplace";

    public const string InvoiceProcessingQueue = "invoice.processing";
    public const string EmailNotificationQueue = "notification.email";

    public const string DeadLetterSuffix = ".dlq";

    /// <summary>Broker-side safety net against crash loops; application-level retry is handled by consumers (task 007).</summary>
    public const int DeliveryLimit = 10;

    /// <summary>Main queues and the routing key (event type) bound to each on <see cref="Exchange"/>.</summary>
    public static readonly IReadOnlyList<(string Queue, string RoutingKey)> Bindings =
    [
        (OrderProcessingQueue, EventTypes.OrderCreated),
        // Every channel gets its own copy of each stock change.
        (StockSyncWebsiteQueue, EventTypes.InventoryChanged),
        (StockSyncPosQueue, EventTypes.InventoryChanged),
        (StockSyncMarketplaceQueue, EventTypes.InventoryChanged),
        (InvoiceProcessingQueue, EventTypes.InvoiceRequested),
        (EmailNotificationQueue, EventTypes.EmailRequested)
        // processing.retry is reserved for manual retry (task 008): no binding yet.
    ];

    /// <summary>
    /// Channel slug → the queue that channel's stock sync consumes (doc §10). A channel without an entry (one created through
    /// the API) can have product mappings but is never synced. Each of these queues is also bound to its own name on
    /// <see cref="Exchange"/>, so a directed <c>stock.sync.requested</c> (routing key = queue name) reaches one channel only,
    /// while <c>inventory.changed</c> fans out to all of them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> StockSyncQueues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["website"] = StockSyncWebsiteQueue,
        ["pos"] = StockSyncPosQueue,
        ["digital-platform"] = StockSyncMarketplaceQueue
    };

    /// <summary>The stock-sync queue of the channel with <paramref name="channelSlug"/>; null when the channel is not synced.</summary>
    public static string? StockSyncQueueOf(string channelSlug) => StockSyncQueues.GetValueOrDefault(channelSlug);

    public static string DeadLetterQueueOf(string queue) => queue + DeadLetterSuffix;

    /// <summary>Arguments of every main queue.</summary>
    public static IDictionary<string, object?> MainQueueArguments(string queue) => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-dead-letter-exchange"] = DeadLetterExchange,
        ["x-dead-letter-routing-key"] = DeadLetterQueueOf(queue),
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish",
        ["x-delivery-limit"] = DeliveryLimit
    };

    /// <summary>Arguments of every dead-letter queue (no DLX of their own).</summary>
    public static IDictionary<string, object?> DeadLetterQueueArguments() => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum"
    };

    // ---- Delayed retry (task 007) -------------------------------------------------------------------------------------

    /// <summary>
    /// Direct exchange that expired retry copies are dead-lettered to; every main queue is bound with routing key = its own
    /// name, so a copy published with routing key = source queue returns to exactly that queue.
    /// </summary>
    public const string RetryExchange = "omnichannel.retry";

    public const string DelayPrefix = "omnichannel.delay.";

    /// <summary>Default retry delays (tiers): 5 s, 30 s, 2 min, 10 min.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// Name of the delay exchange and queue of a tier, e.g. <c>omnichannel.delay.30s</c>, <c>omnichannel.delay.2m</c>.
    /// The TTL is part of the name, so a changed tier creates a new pair and never conflicts with an existing queue.
    /// </summary>
    public static string DelayNameOf(TimeSpan delay) => DelayPrefix + delay switch
    {
        { TotalMilliseconds: var ms } when ms % 60_000 == 0 => $"{(long)delay.TotalMinutes}m",
        { TotalMilliseconds: var ms } when ms % 1_000 == 0 => $"{(long)delay.TotalSeconds}s",
        _ => $"{(long)delay.TotalMilliseconds}ms"
    };

    /// <summary>
    /// Tier for the retry after failed attempt <paramref name="attemptNo"/>: attempt 1 → first tier, 2 → second, ...,
    /// attempts beyond the last tier keep using the last (longest) one.
    /// </summary>
    public static TimeSpan RetryDelayFor(int attemptNo, IReadOnlyList<TimeSpan> delays)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNo);
        ArgumentOutOfRangeException.ThrowIfZero(delays.Count);
        return delays[Math.Min(attemptNo, delays.Count) - 1];
    }

    /// <summary>
    /// Delay queue: messages wait for the TTL and are then dead-lettered to <see cref="RetryExchange"/> keeping their
    /// routing key (no <c>x-dead-letter-routing-key</c>). One fixed TTL per queue avoids head-of-line blocking.
    /// </summary>
    public static IDictionary<string, object?> DelayQueueArguments(TimeSpan delay) => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-message-ttl"] = (int)delay.TotalMilliseconds,
        ["x-dead-letter-exchange"] = RetryExchange,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish"
    };

    /// <summary>Declares the default topology (default retry delays).</summary>
    public static Task DeclareAsync(IChannel channel, CancellationToken cancellationToken) =>
        DeclareAsync(channel, DefaultRetryDelays, cancellationToken);

    /// <summary>Declares exchanges, queues and bindings, including the delay tiers. Safe to call repeatedly.</summary>
    /// <exception cref="RabbitMqTopologyMismatchException">An existing exchange or queue has different settings.</exception>
    public static async Task DeclareAsync(IChannel channel, IReadOnlyList<TimeSpan> retryDelays, CancellationToken cancellationToken)
    {
        try
        {
            await channel.ExchangeDeclareAsync(
                Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.ExchangeDeclareAsync(
                DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.ExchangeDeclareAsync(
                RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);

            foreach (var (queue, routingKey) in Bindings)
            {
                var deadLetterQueue = DeadLetterQueueOf(queue);

                await channel.QueueDeclareAsync(
                    deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                    arguments: DeadLetterQueueArguments(), cancellationToken: cancellationToken);
                await channel.QueueBindAsync(deadLetterQueue, DeadLetterExchange, deadLetterQueue, cancellationToken: cancellationToken);

                await channel.QueueDeclareAsync(
                    queue, durable: true, exclusive: false, autoDelete: false,
                    arguments: MainQueueArguments(queue), cancellationToken: cancellationToken);
                await channel.QueueBindAsync(queue, Exchange, routingKey, cancellationToken: cancellationToken);
                await channel.QueueBindAsync(queue, RetryExchange, queue, cancellationToken: cancellationToken);
            }

            // Directed stock-sync messages. A binding only: no argument of an existing queue changes.
            foreach (var queue in StockSyncQueues.Values)
            {
                await channel.QueueBindAsync(queue, Exchange, routingKey: queue, cancellationToken: cancellationToken);
            }

            foreach (var delay in retryDelays.Distinct())
            {
                var name = DelayNameOf(delay);
                await channel.ExchangeDeclareAsync(
                    name, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
                await channel.QueueDeclareAsync(
                    name, durable: true, exclusive: false, autoDelete: false,
                    arguments: DelayQueueArguments(delay), cancellationToken: cancellationToken);
                await channel.QueueBindAsync(name, name, routingKey: string.Empty, cancellationToken: cancellationToken);
            }
        }
        catch (OperationInterruptedException exception)
            when (exception.ShutdownReason?.ReplyCode == Constants.PreconditionFailed)
        {
            throw new RabbitMqTopologyMismatchException(exception.ShutdownReason.ReplyText, exception);
        }
    }
}

/// <summary>
/// The broker already has an exchange or queue with the same name but different settings (PRECONDITION_FAILED).
/// Requires an operator decision (migrate or delete the queue); the host must stop instead of guessing.
/// </summary>
public sealed class RabbitMqTopologyMismatchException(string brokerMessage, Exception innerException)
    : Exception($"RabbitMQ topology mismatch: {brokerMessage}. Existing queues are never changed or deleted automatically.",
        innerException);

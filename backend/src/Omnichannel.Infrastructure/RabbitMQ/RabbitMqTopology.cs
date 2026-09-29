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

    /// <summary>Declares exchanges, queues and bindings. Safe to call repeatedly.</summary>
    /// <exception cref="RabbitMqTopologyMismatchException">An existing exchange or queue has different settings.</exception>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await channel.ExchangeDeclareAsync(
                Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.ExchangeDeclareAsync(
                DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);

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

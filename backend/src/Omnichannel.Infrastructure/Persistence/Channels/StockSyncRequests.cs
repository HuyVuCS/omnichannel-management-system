using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Channels;

/// <summary>
/// Builds the rows of a stock sync that is asked for directly (initial sync of a mapping, manual retry, reconciliation): a
/// <c>stock.sync.requested</c> outbox row whose routing key is the queue of one channel, so only that channel's consumer
/// receives it. The caller adds the rows to its own transaction.
/// </summary>
internal static class StockSyncRequests
{
    /// <summary>A new Pending stock-sync task and its directed message (the task's <c>MessageId</c> is the outbox id).</summary>
    /// <param name="idempotencyKey">Optional business key of the task (a reconciliation run: one task per pair).</param>
    public static (ProcessingTask Task, OutboxEvent Message) NewTask(
        Guid channelId,
        Guid productId,
        string queue,
        string correlationId,
        string? idempotencyKey = null)
    {
        var outboxEventId = Guid.CreateVersion7();
        var task = ProcessingTask.ForStockSync(channelId, productId, correlationId, outboxEventId.ToString(), idempotencyKey);
        return (task, MessageFor(task.TaskId, channelId, productId, queue, correlationId, outboxEventId));
    }

    /// <summary>A directed message for an existing stock-sync task (envelope <c>taskId</c> = the aggregate id).</summary>
    public static OutboxEvent MessageFor(
        Guid taskId,
        Guid channelId,
        Guid productId,
        string queue,
        string correlationId,
        Guid? outboxEventId = null) =>
        new(
            EventTypes.StockSyncRequested,
            taskId.ToString(),
            MessageJson.Serialize(new StockSyncRequestedPayload(taskId, channelId, productId)),
            correlationId,
            inventoryMovementId: null,
            outboxEventId,
            routingKey: queue);
}

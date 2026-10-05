using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;
using RabbitMQ.Client;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 015 Part A1 (review 014, P2): a stock-sync task that exists is brought to an end even when its mapping or channel
/// was deactivated while a delivery of its message was still on the way.
/// </summary>
public sealed partial class StockSyncWorkerTests
{
    [Fact]
    public async Task Mapping_deactivated_while_a_retry_waits_completes_the_task_without_a_push()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: Sku("WEB"));
        var import = await ImportAsync(product.ProductId, 4);
        var change = await OutboxOfMovementAsync(import.Items[0].MovementId);
        var websiteId = await ChannelIdAsync(Website);
        var failed = 0;
        var channel = new RecordingChannelClient();
        channel.Behaviour = async (update, _) =>
        {
            // The push of the stock change fails once; before its retry arrives the mapping is switched off.
            if (update.ProductId == product.ProductId && update.TaskId != mapping.SyncTaskId && Interlocked.Exchange(ref failed, 1) == 0)
            {
                await UpdateMappingAsync(Website, mapping.ChannelProductId, new UpdateChannelProductRequest(null, Sku("WEB"), ChannelProductStatus.Inactive));
                throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "channel down (test)", isTransient: true);
            }
        };

        await RunWorkersAsync(channel, async () =>
        {
            await WaitForTasksAsync(product.ProductId, count: 2, ProcessingTaskStatus.Succeeded);
            await WaitForEmptyAsync(WebsiteQueue);
        });

        var task = (await TasksOfAsync(product.ProductId)).Single(t => t.IdempotencyKey == StockSyncIdempotencyKey.For(websiteId, change.OutboxEventId.ToString()));
        var attempts = task.Attempts.OrderBy(a => a.AttemptNo).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal((ProcessingAttemptStatus.Failed, ChannelApiErrorCodes.Unavailable), (attempts[0].Status, attempts[0].ErrorCode));
        Assert.Equal(ProcessingAttemptStatus.Succeeded, attempts[1].Status);
        // The only call for this task is the one that failed: the retry found the mapping inactive and pushed nothing.
        Assert.Equal($"{task.TaskId}:1:1", Assert.Single(channel.PushesOfTask(task.TaskId)).RequestId);
        await AssertNoDeadLettersAsync();
        await AssertDelayQueuesEmptyAsync();
    }

    [Fact]
    public async Task Channel_deactivated_while_a_retry_waits_completes_the_task_without_a_push()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Pos, product.ProductId, externalSku: Sku("POS"));
        var import = await ImportAsync(product.ProductId, 4);
        var change = await OutboxOfMovementAsync(import.Items[0].MovementId);
        var pos = await fixture.WithDbAsync(db => db.Channels.AsNoTracking().SingleAsync(c => c.Slug == Pos));
        var failed = 0;
        var channel = new RecordingChannelClient();
        channel.Behaviour = async (update, _) =>
        {
            if (update.ProductId == product.ProductId && update.TaskId != mapping.SyncTaskId && Interlocked.Exchange(ref failed, 1) == 0)
            {
                await SetChannelStatusAsync(pos, ChannelStatus.Inactive);
                throw new ChannelApiException(ChannelApiErrorCodes.Unavailable, "channel down (test)", isTransient: true);
            }
        };

        try
        {
            await RunWorkersAsync(channel, async () =>
            {
                await WaitForTasksAsync(product.ProductId, count: 2, ProcessingTaskStatus.Succeeded);
                await WaitForEmptyAsync(PosQueue);
            });
        }
        finally
        {
            // The seeded channel is shared by the collection.
            await SetChannelStatusAsync(pos, ChannelStatus.Active);
        }

        var task = (await TasksOfAsync(product.ProductId)).Single(t => t.IdempotencyKey == StockSyncIdempotencyKey.For(pos.ChannelId, change.OutboxEventId.ToString()));
        Assert.Equal(
            [ProcessingAttemptStatus.Failed, ProcessingAttemptStatus.Succeeded],
            task.Attempts.OrderBy(a => a.AttemptNo).Select(a => a.Status));
        Assert.Single(channel.PushesOfTask(task.TaskId));
        await AssertNoDeadLettersAsync();
    }

    [Fact]
    public async Task Delivery_limit_on_a_message_of_a_deactivated_mapping_fails_the_existing_task()
    {
        var product = await SeedProductAsync();
        var mapping = await MapAsync(Website, product.ProductId, externalSku: Sku("WEB"));
        var websiteId = await ChannelIdAsync(Website);

        // A stock change whose task exists already (an earlier delivery created it), then the mapping is switched off.
        var messageId = Guid.CreateVersion7().ToString();
        var correlationId = $"corr-{messageId}";
        var resolution = await fixture.WithServicesAsync(services => services.GetRequiredService<IStockSyncService>()
            .ResolveInventoryChangeAsync(Website, product.ProductId, messageId, correlationId, CancellationToken.None));
        var taskId = resolution.TaskId!.Value;
        await UpdateMappingAsync(Website, mapping.ChannelProductId, new UpdateChannelProductRequest(null, Sku("WEB"), ChannelProductStatus.Inactive));

        var envelope = new MessageEnvelope(
            messageId,
            correlationId,
            TaskId: null,
            EventTypes.InventoryChanged,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(
                new InventoryChangedPayload(product.ProductId, Guid.NewGuid(), InventoryMovementType.Import, 1, 0, 1, DateTimeOffset.UtcNow),
                MessageJson.Options));
        // Routed by the queue's own name: only the website queue gets it.
        await fixture.PublishAsync(WebsiteQueue, MessageJson.SerializeToUtf8Bytes(envelope), messageId);

        // It comes back unsettled as often as the queue allows (consumers that died holding it).
        for (var n = 0; n < RabbitMqTopology.DeliveryLimit; n++)
        {
            await using var crashing = await fixture.OpenChannelAsync();
            await Eventually.WaitAsync(() => crashing.BasicGetAsync(WebsiteQueue, autoAck: false), r => r is not null, Timeout, "crash-loop delivery");
            await crashing.CloseAsync();
        }

        var channel = new RecordingChannelClient();
        await RunWorkersAsync(channel, async () =>
        {
            await Eventually.WaitAsync(() => TaskAsync(taskId), t => t.Status == ProcessingTaskStatus.Failed, Timeout, "task Failed");
            await WaitForEmptyAsync(WebsiteQueue);
        });

        // Not acknowledged silently: the task is failed with its own attempt, visible, and the message is in the DLQ.
        var attempt = Assert.Single((await TaskAsync(taskId)).Attempts);
        Assert.Equal((ProcessingAttemptStatus.Failed, "delivery_limit_reached"), (attempt.Status, attempt.ErrorCode));
        Assert.Single(await ProcessingFailedLogsAsync(taskId));
        var dead = Assert.Single(await DrainAsync(RabbitMqTopology.DeadLetterQueueOf(WebsiteQueue), expected: 1));
        Assert.Equal(messageId, dead.BasicProperties.MessageId);
        Assert.Empty(channel.PushesOf(product.ProductId));
        Assert.Equal(websiteId, (await TaskAsync(taskId)).ChannelId);
    }

    private Task SetChannelStatusAsync(Channel channel, ChannelStatus status) =>
        fixture.WithServicesAsync(services => services.GetRequiredService<IChannelService>()
            .UpdateAsync(channel.ChannelId, new UpdateChannelRequest(channel.Name, status), CancellationToken.None));
}

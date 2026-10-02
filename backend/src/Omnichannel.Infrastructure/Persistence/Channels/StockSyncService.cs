using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Processing;

namespace Omnichannel.Infrastructure.Persistence.Channels;

/// <summary>
/// Stock-sync persistence for the Stock Sync Worker (doc §8.3). Reads take no lock and nothing here spans the call to a
/// channel: the worker reads the current stock, pushes it, and reads again - a value that changed in between is pushed again,
/// so the channel converges to the central stock without holding a transaction during an external call.
/// </summary>
internal sealed class StockSyncService(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    TimeProvider timeProvider) : IStockSyncService
{
    public async Task<StockSyncResolution> ResolveInventoryChangeAsync(
        string channelSlug,
        Guid productId,
        string messageId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.Slug == channelSlug)
            .Select(c => new
            {
                c.ChannelId,
                c.Status,
                IsMapped = dbContext.ChannelProducts.Any(m =>
                    m.ChannelId == c.ChannelId && m.ProductId == productId && m.Status == ChannelProductStatus.Active)
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (channel is null)
        {
            return StockSyncResolution.ChannelNotFound;
        }

        if (channel.Status != ChannelStatus.Active || !channel.IsMapped)
        {
            return StockSyncResolution.NotMapped;
        }

        // A redelivery or a delayed retry copy of the same message finds its task here.
        var idempotencyKey = StockSyncIdempotencyKey.For(channel.ChannelId, messageId);
        if (await FindByKeyAsync(idempotencyKey, cancellationToken) is { } existing)
        {
            return existing;
        }

        var task = ProcessingTask.ForStockSync(channel.ChannelId, productId, correlationId, messageId, idempotencyKey);
        dbContext.ProcessingTasks.Add(task);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ProcessingTaskIdempotencyKey))
        {
            // Another instance created the task for the same message at the same moment: the unique index decides.
            dbContext.ChangeTracker.Clear();
            return await FindByKeyAsync(idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException($"Stock-sync task '{idempotencyKey}' exists but cannot be read.");
        }

        // Later steps load the task again with its attempts.
        dbContext.ChangeTracker.Clear();
        return new StockSyncResolution(StockSyncResolutionKind.Task, task.TaskId, channel.ChannelId, task.Status);
    }

    public async Task<StockSyncTaskState?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new StockSyncTaskState(
                t.TaskId,
                t.TaskType,
                t.Status,
                t.CorrelationId,
                t.ChannelId,
                dbContext.Channels.Where(c => c.ChannelId == t.ChannelId).Select(c => c.Slug).SingleOrDefault(),
                t.ProductId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<AttemptStart?> StartAttemptAsync(
        Guid taskId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken) =>
        attempts.StartAttemptAsync(taskId, workerName, workerInstanceId, cancellationToken);

    public async Task<StockSyncTarget?> ReadTargetAsync(Guid channelId, Guid productId, CancellationToken cancellationToken) =>
        await dbContext.ChannelProducts
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId && m.ProductId == productId)
            .Select(m => new StockSyncTarget(
                m.Channel.Slug,
                m.Status == ChannelProductStatus.Active && m.Channel.Status == ChannelStatus.Active,
                m.Product.Sku,
                m.ExternalProductId,
                m.ExternalSku,
                // Every product has its inventory row (created with the product); the null branch only guards the LEFT JOIN.
                m.Product.Inventory == null ? 0 : m.Product.Inventory.AvailableStock))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task CompleteAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await attempts.LockTaskAsync(taskId, cancellationToken);
        if (task.IsTerminal)
        {
            // Another delivery of the same message finished first; this attempt pushed the current stock as well.
            task.CloseAttemptOfCompletedTask(attemptId, now);
        }
        else
        {
            task.Succeed(orderId: null, attemptId, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task RecordTransientFailureAsync(
        Guid taskId,
        Guid attemptId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken) =>
        attempts.RecordTransientFailureAsync(taskId, attemptId, errorCode, errorMessage, cancellationToken);

    public Task<int?> RecordFinalFailureAsync(
        Guid taskId,
        Guid? attemptId,
        string errorCode,
        string errorMessage,
        string workerName,
        string workerInstanceId,
        string queue,
        CancellationToken cancellationToken) =>
        attempts.RecordFinalFailureAsync(
            taskId, attemptId, errorCode, errorMessage, workerName, workerInstanceId, queue, cancellationToken);

    public async Task RecordRejectedMessageAsync(
        string? messageId,
        string? correlationId,
        Guid? taskId,
        string reason,
        string workerName,
        string queue,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Error,
            StockSyncEvents.MessageRejected,
            correlationId,
            taskId,
            orderId: null,
            workerName,
            MessageJson.Serialize(new { messageId, reason, queue }),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<StockSyncResolution?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.IdempotencyKey == idempotencyKey)
            .Select(t => new StockSyncResolution(StockSyncResolutionKind.Task, t.TaskId, t.ChannelId, t.Status))
            .SingleOrDefaultAsync(cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Channels;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// Task search, detail and manual retry. The list is one COUNT plus one page query (attempt filters as <c>EXISTS</c>, attempt
/// count and last attempt as correlated subqueries, the product's SKU from a left join); the detail is one query for
/// task + attempts + SKU and one primary-key lookup of the outbox row.
/// </summary>
internal sealed class ProcessingTaskService(
    OmnichannelDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<ProcessingTaskService> logger) : IProcessingTaskService
{
    private const string SystemActor = "system";

    public async Task<PagedResult<ProcessingTaskSummaryResponse>> ListAsync(
        ProcessingTaskListQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<ProcessingTask> tasks = dbContext.ProcessingTasks.AsNoTracking();

        if (query.TaskType is { } taskType)
        {
            tasks = tasks.Where(t => t.TaskType == taskType);
        }

        if (query.Status is { } status)
        {
            tasks = tasks.Where(t => t.Status == status);
        }

        if (query.OrderId is { } orderId)
        {
            tasks = tasks.Where(t => t.OrderId == orderId);
        }

        if (query.ChannelId is { } channelId)
        {
            tasks = tasks.Where(t => t.ChannelId == channelId);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            var correlationId = query.CorrelationId.Trim();
            tasks = tasks.Where(t => t.CorrelationId == correlationId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            tasks = tasks.Where(t => t.CreatedAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            tasks = tasks.Where(t => t.CreatedAt <= toUtc);
        }

        if (query.HasAttemptFilter)
        {
            // One EXISTS: some attempt of the task matches every attempt-level filter given.
            var workerName = Trimmed(query.WorkerName);
            var workerInstanceId = Trimmed(query.WorkerInstanceId);
            var errorCode = Trimmed(query.ErrorCode);
            var attemptNo = query.AttemptNo;
            tasks = tasks.Where(t => t.Attempts.Any(a =>
                (workerName == null || a.WorkerName == workerName)
                && (workerInstanceId == null || a.WorkerInstanceId == workerInstanceId)
                && (errorCode == null || a.ErrorCode == errorCode)
                && (attemptNo == null || a.AttemptNo == attemptNo)));
        }

        var totalCount = await tasks.CountAsync(cancellationToken);

        // Both sort fields are times: newest first unless the client asks for ascending order explicitly.
        var descending = query.SortDir is null || query.IsDescending;
        var ordered = query.IsSortedBy(ProcessingTaskListQuery.SortByUpdatedAt)
            ? tasks.OrderBy(t => t.UpdatedAt ?? t.CreatedAt, descending)
            : tasks.OrderBy(t => t.CreatedAt, descending);

        var items = await (descending ? ordered.ThenByDescending(t => t.TaskId) : ordered.ThenBy(t => t.TaskId))
            .Skip(query.Skip)
            .Take(query.PageSize)
            // Only stock-sync tasks have a product: a left join of the page, in the same statement.
            .LeftJoin(dbContext.Products, t => t.ProductId, p => (Guid?)p.ProductId, (t, p) => new ProcessingTaskSummaryResponse(
                t.TaskId,
                t.TaskType,
                t.Status,
                t.OrderId,
                t.ChannelId,
                t.ProductId,
                p != null ? p.Sku : null,
                t.RetryCount,
                t.AttemptBudgetStart,
                t.CorrelationId,
                t.MessageId,
                t.CreatedAt,
                t.UpdatedAt,
                t.Attempts.Count,
                t.Attempts.AsQueryable().OrderByDescending(a => a.AttemptNo).Select(ProcessingProjections.ToAttempt).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProcessingTaskSummaryResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<ProcessingTaskDetailResponse> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .LeftJoin(dbContext.Products, t => t.ProductId, p => (Guid?)p.ProductId, (t, p) => new
            {
                t.TaskId,
                t.TaskType,
                t.Status,
                t.OrderId,
                t.ChannelId,
                t.ProductId,
                ProductSku = p != null ? p.Sku : null,
                t.RetryCount,
                t.AttemptBudgetStart,
                t.CorrelationId,
                t.MessageId,
                t.CreatedAt,
                t.UpdatedAt,
                Attempts = t.Attempts.AsQueryable().OrderBy(a => a.AttemptNo).Select(ProcessingProjections.ToAttempt).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(ProcessingTask), taskId, ProcessingErrorCodes.TaskNotFound);

        // The message id is the outbox row's id (the RabbitMQ messageId).
        ProcessingMessageResponse? message = null;
        if (Guid.TryParse(task.MessageId, out var outboxEventId))
        {
            message = await dbContext.OutboxEvents
                .AsNoTracking()
                .Where(e => e.OutboxEventId == outboxEventId)
                .Select(e => new ProcessingMessageResponse(
                    e.OutboxEventId,
                    e.EventType,
                    e.Status,
                    e.CreatedAt,
                    e.PublishedAt,
                    e.PublishAttempts,
                    e.LastError))
                .SingleOrDefaultAsync(cancellationToken);
        }

        return new ProcessingTaskDetailResponse(
            task.TaskId,
            task.TaskType,
            task.Status,
            task.OrderId,
            task.ChannelId,
            task.ProductId,
            task.ProductSku,
            task.RetryCount,
            task.AttemptBudgetStart,
            task.CorrelationId,
            task.MessageId,
            task.CreatedAt,
            task.UpdatedAt,
            task.Attempts,
            message);
    }

    public async Task<RetryProcessingTaskResponse> RetryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var requestedBy = currentUser.UserId ?? SystemActor;

        // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Row lock: concurrent retries of the same task (or a worker finishing it) are serialised; the second retry then sees
        // the task Pending and gets 409.
        await dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "ProcessingTasks" WHERE "TaskId" = {taskId} FOR UPDATE""",
            cancellationToken);

        var task = await dbContext.ProcessingTasks
            .Include(t => t.Attempts)
            .SingleOrDefaultAsync(t => t.TaskId == taskId, cancellationToken)
            ?? throw new NotFoundException(nameof(ProcessingTask), taskId, ProcessingErrorCodes.TaskNotFound);

        if (task.Status != ProcessingTaskStatus.Failed)
        {
            throw new ConflictException(
                ProcessingErrorCodes.TaskNotFailed,
                $"Task '{taskId}' is {task.Status}; only a Failed task can be retried.");
        }

        if (task.TaskType == ProcessingTaskType.ReportExport)
        {
            await EnsureExportNotExpiredAsync(taskId, now, cancellationToken);
        }

        var retryMessage = task.TaskType == ProcessingTaskType.StockSync
            ? await StockSyncRetryMessageAsync(task, cancellationToken)
            : await CopyOfSourceMessageAsync(task, cancellationToken);
        dbContext.OutboxEvents.Add(retryMessage);
        var newMessageId = retryMessage.OutboxEventId.ToString();
        var previousMessageId = task.RequeueForManualRetry(newMessageId);

        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Information,
            ProcessingEvents.RetryRequested,
            task.CorrelationId,
            task.TaskId,
            task.OrderId,
            workerName: null,
            MessageJson.Serialize(new
            {
                taskId = task.TaskId,
                taskType = task.TaskType,
                requestedBy,
                previousMessageId,
                newMessageId,
                attemptBudgetStart = task.AttemptBudgetStart
            }),
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Task {TaskId} retried manually by {RequestedBy}: new message {MessageId} (previous {PreviousMessageId}), attempt budget starts after attempt {AttemptBudgetStart}",
            task.TaskId, requestedBy, newMessageId, previousMessageId, task.AttemptBudgetStart);

        return new RetryProcessingTaskResponse(task.TaskId, task.Status, newMessageId);
    }

    /// <summary>
    /// An export past its retention time can only fail again (the worker refuses to build it), and the alert of that
    /// failure would stay open for good: the retention sweep has dealt with the export already. Nothing is written.
    /// </summary>
    private async Task EnsureExportNotExpiredAsync(Guid taskId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var export = await dbContext.ReportExports
            .AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .Select(e => new { e.DeletedAt, e.ExpiresAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (export is not null && ReportExportExpiry.IsPastRetention(export.DeletedAt, export.ExpiresAt, now))
        {
            throw new ConflictException(ReportErrorCodes.ExportExpired, ValidationMessages.Get(ValidationMessages.Keys.ExportExpired));
        }
    }

    /// <summary>
    /// A new message (new id) with the original content; it is published with its original routing key, so it reaches the same
    /// consumer. The copy of the failed message stays in the DLQ for inspection. No movement link: the movement is already
    /// referenced by the original row (1:0..1).
    /// </summary>
    private async Task<OutboxEvent> CopyOfSourceMessageAsync(ProcessingTask task, CancellationToken cancellationToken)
    {
        var source = Guid.TryParse(task.MessageId, out var sourceId)
            ? await dbContext.OutboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.OutboxEventId == sourceId, cancellationToken)
            : null;
        if (source is null)
        {
            throw new ConflictException(
                ProcessingErrorCodes.RetrySourceMissing,
                $"The message of task '{task.TaskId}' ({task.MessageId ?? "none"}) is not in the outbox; there is nothing to republish.");
        }

        return new OutboxEvent(
            source.EventType,
            source.AggregateId,
            source.Payload,
            source.CorrelationId,
            inventoryMovementId: null,
            routingKey: source.RoutingKey);
    }

    /// <summary>
    /// A stock-sync task usually comes from an <c>inventory.changed</c> that fans out to every channel: a copy of it would
    /// create unrelated tasks on the other channels and leave this one Pending. The retry is a message for this task only,
    /// routed to the queue of its channel.
    /// </summary>
    private async Task<OutboxEvent> StockSyncRetryMessageAsync(ProcessingTask task, CancellationToken cancellationToken)
    {
        var channelSlug = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.ChannelId == task.ChannelId)
            .Select(c => c.Slug)
            .SingleOrDefaultAsync(cancellationToken);

        if (task.ChannelId is not { } channelId
            || task.ProductId is not { } productId
            || channelSlug is null
            || RabbitMqTopology.StockSyncQueueOf(channelSlug) is not { } queue)
        {
            throw new ConflictException(
                ProcessingErrorCodes.RetrySourceMissing,
                $"Stock-sync task '{task.TaskId}' has no channel with a stock-sync queue; there is nothing to send.");
        }

        return StockSyncRequests.MessageFor(task.TaskId, channelId, productId, queue, task.CorrelationId);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

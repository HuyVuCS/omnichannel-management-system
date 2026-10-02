using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// Order intake, decision A (implementation log, steps 4–5): the API stores a processing task and an
/// <c>order.created</c> outbox event and returns 202. It never creates the Order, never checks stock and never
/// talks to RabbitMQ; the Order Worker creates the Order inside the reserve-stock transaction.
/// </summary>
internal sealed class OrderIntakeService(
    OmnichannelDbContext dbContext,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<OrderIntakeService> logger) : IOrderIntakeService
{
    private const string SystemActor = "system";

    public async Task<SubmitOrderResponse> SubmitAsync(
        string channelSlug,
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        var slug = channelSlug.Trim().ToLowerInvariant();
        var channel = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.Slug == slug && c.Status == ChannelStatus.Active)
            .Select(c => new { c.ChannelId, c.Slug })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Channel), channelSlug, OrderErrorCodes.ChannelNotFound);

        var prices = await LoadPricesOrThrowAsync(request, cancellationToken);

        var externalOrderId = request.ExternalOrderId.Trim();
        var idempotencyKey = OrderIdempotencyKey.For(channel.ChannelId, externalOrderId);
        var requestHash = OrderRequestHash.Compute(request.CustomerId, request.Items);
        var correlationId = correlationContext.CorrelationId;

        // The outbox id is the RabbitMQ messageId; the task records it, so it is generated first.
        var outboxEventId = Guid.CreateVersion7();
        var task = new ProcessingTask(
            ProcessingTaskType.OrderProcessing,
            orderId: null,
            correlationId,
            messageId: outboxEventId.ToString(),
            idempotencyKey,
            requestHash);

        var order = new NormalizedOrder(
            task.TaskId,
            channel.ChannelId,
            channel.Slug,
            externalOrderId,
            request.CustomerId,
            request.Items.Select(i => new NormalizedOrderItem(i.ProductId, i.Stock, prices[i.ProductId])).ToList(),
            timeProvider.GetUtcNow(),
            currentUser.UserId ?? SystemActor);

        dbContext.ProcessingTasks.Add(task);
        dbContext.OutboxEvents.Add(new OutboxEvent(
            EventTypes.OrderCreated,
            task.TaskId.ToString(),
            MessageJson.Serialize(OrderCreatedPayload.From(order)),
            correlationId,
            inventoryMovementId: null,
            outboxEventId));

        try
        {
            // One SaveChanges = one transaction: task and outbox row are stored together or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.IsUniqueViolation(ExpectedUniqueViolations.ProcessingTaskIdempotencyKey))
        {
            return await ExistingSubmissionAsync(idempotencyKey, requestHash, externalOrderId, cancellationToken);
        }

        logger.LogInformation(
            "Order {ExternalOrderId} from channel {ChannelSlug} accepted as task {TaskId}",
            externalOrderId,
            channel.Slug,
            task.TaskId);

        return new SubmitOrderResponse(task.TaskId, task.Status, correlationId, Duplicate: false);
    }

    /// <summary>Customer and products must exist; returns the current price of every product (snapshot for the order).</summary>
    private async Task<Dictionary<Guid, decimal>> LoadPricesOrThrowAsync(
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        var failures = new List<ValidationFailure>();

        if (!await dbContext.Customers.AnyAsync(c => c.CustomerId == request.CustomerId, cancellationToken))
        {
            failures.Add(new ValidationFailure(
                ValidationConventions.JsonName(nameof(SubmitOrderRequest.CustomerId)),
                ValidationMessages.Get(ValidationMessages.Keys.CustomerNotFound, ("CustomerId", request.CustomerId))));
        }

        var productIds = request.Items.Select(i => i.ProductId).ToList();
        var prices = await dbContext.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId))
            .ToDictionaryAsync(p => p.ProductId, p => p.Price, cancellationToken);

        for (var index = 0; index < request.Items.Count; index++)
        {
            var productId = request.Items[index].ProductId;
            if (!prices.ContainsKey(productId))
            {
                failures.Add(new ValidationFailure(
                    ValidationConventions.ItemPath(nameof(SubmitOrderRequest.Items), index, nameof(SubmitOrderItem.ProductId)),
                    ValidationMessages.Get(ValidationMessages.Keys.ProductNotFound, ("ProductId", productId))));
            }
        }

        return failures.Count == 0 ? prices : throw new ValidationException(failures);
    }

    /// <summary>
    /// Same key: identical content → the existing task (duplicate); different content → 409, the new content is not
    /// silently dropped. Tasks stored before hashes existed (null hash) are treated as identical.
    /// </summary>
    private async Task<SubmitOrderResponse> ExistingSubmissionAsync(
        string idempotencyKey,
        string requestHash,
        string externalOrderId,
        CancellationToken cancellationToken)
    {
        // Forget the rejected inserts; the unique index guarantees the winning submission is committed.
        dbContext.ChangeTracker.Clear();

        var existing = await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => t.IdempotencyKey == idempotencyKey)
            .Select(t => new { t.TaskId, t.Status, t.CorrelationId, t.RequestHash })
            .SingleAsync(cancellationToken);

        if (existing.RequestHash is not null && existing.RequestHash != requestHash)
        {
            logger.LogWarning(
                "Order {ExternalOrderId} resubmitted with different content; existing task {TaskId}",
                externalOrderId,
                existing.TaskId);
            throw new ConflictException(
                OrderErrorCodes.OrderConflict,
                $"Order '{externalOrderId}' was already submitted on this channel with different content.");
        }

        logger.LogInformation("Duplicate order submission for existing task {TaskId}", existing.TaskId);

        return new SubmitOrderResponse(existing.TaskId, existing.Status, existing.CorrelationId, Duplicate: true);
    }
}

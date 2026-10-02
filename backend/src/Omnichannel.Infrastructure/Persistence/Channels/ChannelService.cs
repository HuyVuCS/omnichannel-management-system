using System.Linq.Expressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Infrastructure.Persistence.Channels;

internal sealed class ChannelService(
    OmnichannelDbContext dbContext,
    ICorrelationContext correlationContext,
    ILogger<ChannelService> logger) : IChannelService
{
    private static readonly Expression<Func<Channel, ChannelResponse>> ToResponse = c => new ChannelResponse(
        c.ChannelId, c.Name, c.Slug, c.Type, c.Status, c.CreatedAt, c.UpdatedAt);

    private static readonly Func<Channel, ChannelResponse> ToResponseCompiled = ToResponse.Compile();

    private static readonly Expression<Func<ChannelProduct, ChannelProductResponse>> ToProductResponse = m => new ChannelProductResponse(
        m.ChannelProductId,
        m.ChannelId,
        m.ProductId,
        m.Product.Sku,
        m.Product.Name,
        m.ExternalProductId,
        m.ExternalSku,
        m.Status,
        m.CreatedAt,
        m.UpdatedAt);

    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Channels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.ChannelId)
            .Select(ToResponse)
            .ToListAsync(cancellationToken);

    public async Task<ChannelResponse> GetAsync(Guid channelId, CancellationToken cancellationToken) =>
        await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .Select(ToResponse)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(Channel), channelId);

    public async Task<ChannelResponse> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = new Channel(request.Name, request.Slug, request.Type);
        dbContext.Channels.Add(channel);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ChannelSlug))
        {
            throw new ConflictException(ChannelErrorCodes.SlugExists, $"A channel with slug '{channel.Slug}' already exists.");
        }

        return ToResponseCompiled(channel);
    }

    public async Task<ChannelResponse> UpdateAsync(Guid channelId, UpdateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await dbContext.Channels.SingleOrDefaultAsync(c => c.ChannelId == channelId, cancellationToken)
            ?? throw new NotFoundException(nameof(Channel), channelId);

        channel.Update(request.Name, request.Status);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponseCompiled(channel);
    }

    public async Task<PagedResult<ChannelProductResponse>> ListProductsAsync(
        Guid channelId,
        ChannelProductListQuery query,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Channels.AnyAsync(c => c.ChannelId == channelId, cancellationToken))
        {
            throw new NotFoundException(nameof(Channel), channelId);
        }

        var mappings = dbContext.ChannelProducts.AsNoTracking().Where(m => m.ChannelId == channelId);

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            mappings = mappings.Where(m =>
                EF.Functions.ILike(m.Product.Sku, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(m.Product.Name, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(m.ExternalProductId!, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(m.ExternalSku!, pattern, LikePattern.EscapeCharacter));
        }

        var totalCount = await mappings.CountAsync(cancellationToken);

        var ordered = query.IsSortedBy(ChannelProductListQuery.SortByCreatedAt)
            ? mappings.OrderBy(m => m.CreatedAt, query.IsDescending)
            : mappings.OrderBy(m => m.Product.Sku, query.IsDescending);

        var items = await ordered
            .ThenBy(m => m.ChannelProductId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(ToProductResponse)
            .ToListAsync(cancellationToken);

        return new PagedResult<ChannelProductResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<ChannelProductResponse> GetProductAsync(Guid channelId, Guid channelProductId, CancellationToken cancellationToken) =>
        await dbContext.ChannelProducts
            .AsNoTracking()
            .Where(m => m.ChannelProductId == channelProductId && m.ChannelId == channelId)
            .Select(ToProductResponse)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(ChannelProduct), channelProductId, ChannelProductErrorCodes.MappingNotFound);

    public async Task<ChannelProductSavedResponse> CreateProductAsync(
        Guid channelId,
        CreateChannelProductRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .Select(c => new { c.Slug, c.Status })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Channel), channelId);

        var product = await dbContext.Products
            .AsNoTracking()
            .Where(p => p.ProductId == request.ProductId)
            .Select(p => new { p.Sku, p.Name })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ValidationException(
            [
                new ValidationFailure(
                    ValidationConventions.JsonName(nameof(CreateChannelProductRequest.ProductId)),
                    ValidationMessages.Get(ValidationMessages.Keys.ProductNotFound, ("ProductId", request.ProductId)))
            ]);

        var mapping = new ChannelProduct(channelId, request.ProductId, request.ExternalProductId, request.ExternalSku);
        dbContext.ChannelProducts.Add(mapping);

        // A new mapping is Active: the channel has to learn the product's stock even if no movement follows.
        var syncTaskId = RequestSync(mapping, channel.Slug, channel.Status);

        // One SaveChanges = one transaction: mapping, task and message are stored together or not at all.
        await SaveMappingAsync(mapping, cancellationToken);

        logger.LogInformation(
            "Product {ProductId} mapped on channel {Channel} as mapping {ChannelProductId}; sync task {SyncTaskId}",
            mapping.ProductId, channel.Slug, mapping.ChannelProductId, syncTaskId);

        return ToSavedResponse(mapping, product.Sku, product.Name, syncTaskId);
    }

    public async Task<ChannelProductSavedResponse> UpdateProductAsync(
        Guid channelId,
        Guid channelProductId,
        UpdateChannelProductRequest request,
        CancellationToken cancellationToken)
    {
        // A mapping of another channel is "not found" here: the address names the channel it must belong to.
        var mapping = await dbContext.ChannelProducts
            .Include(m => m.Channel)
            .Include(m => m.Product)
            .SingleOrDefaultAsync(m => m.ChannelProductId == channelProductId && m.ChannelId == channelId, cancellationToken)
            ?? throw new NotFoundException(nameof(ChannelProduct), channelProductId, ChannelProductErrorCodes.MappingNotFound);

        var syncTaskId = mapping.Update(request.ExternalProductId, request.ExternalSku, request.Status)
            ? RequestSync(mapping, mapping.Channel.Slug, mapping.Channel.Status)
            : null;

        await SaveMappingAsync(mapping, cancellationToken);

        logger.LogInformation(
            "Mapping {ChannelProductId} on channel {Channel} updated ({Status}); sync task {SyncTaskId}",
            mapping.ChannelProductId, mapping.Channel.Slug, mapping.Status, syncTaskId);

        return ToSavedResponse(mapping, mapping.Product.Sku, mapping.Product.Name, syncTaskId);
    }

    /// <summary>
    /// Adds a stock-sync task and its directed message to the unit of work - only for an Active channel that has a
    /// stock-sync queue. Nothing is sent here: the Outbox Publisher and the Stock Sync Worker take it from there.
    /// </summary>
    /// <returns>The task id, or null when no sync was requested.</returns>
    private Guid? RequestSync(ChannelProduct mapping, string channelSlug, ChannelStatus channelStatus)
    {
        if (channelStatus != ChannelStatus.Active || RabbitMqTopology.StockSyncQueueOf(channelSlug) is not { } queue)
        {
            return null;
        }

        var (task, message) = StockSyncRequests.NewTask(mapping.ChannelId, mapping.ProductId, queue, correlationContext.CorrelationId);
        dbContext.ProcessingTasks.Add(task);
        dbContext.OutboxEvents.Add(message);
        return task.TaskId;
    }

    /// <summary>The unique indexes decide (no check-then-insert): one mapping per product, one product per external id.</summary>
    private async Task SaveMappingAsync(ChannelProduct mapping, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ChannelProductMapping))
        {
            throw new ConflictException(
                ChannelProductErrorCodes.MappingExists,
                $"Product '{mapping.ProductId}' is already mapped on this channel.");
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ChannelProductExternalProductId))
        {
            throw new ConflictException(
                ChannelProductErrorCodes.ExternalProductIdTaken,
                $"External product id '{mapping.ExternalProductId}' belongs to another product on this channel.");
        }
    }

    private static ChannelProductSavedResponse ToSavedResponse(ChannelProduct mapping, string sku, string productName, Guid? syncTaskId) =>
        new(
            mapping.ChannelProductId,
            mapping.ChannelId,
            mapping.ProductId,
            sku,
            productName,
            mapping.ExternalProductId,
            mapping.ExternalSku,
            mapping.Status,
            mapping.CreatedAt,
            mapping.UpdatedAt,
            syncTaskId);
}

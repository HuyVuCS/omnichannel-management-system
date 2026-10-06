using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Orders;

/// <summary>
/// Normalises a webhook order (doc §8.1 "Identify CHANNEL → Normalize → Common Order DTO") and hands it to the order
/// intake as it is: this class writes no task and no message, and decides nothing about idempotency - a webhook that
/// arrives twice resolves to the same customer and the same products, so the intake recognises it by its key and hash.
/// </summary>
internal sealed class WebhookOrderService(
    OmnichannelDbContext dbContext,
    IOrderIntakeService orderIntake,
    ILogger<WebhookOrderService> logger) : IWebhookOrderService
{
    public Task<bool> IsActiveChannelAsync(string channelSlug, CancellationToken cancellationToken)
    {
        var slug = Normalise(channelSlug);
        return dbContext.Channels.AnyAsync(c => c.Slug == slug && c.Status == ChannelStatus.Active, cancellationToken);
    }

    public async Task<SubmitOrderResponse> SubmitAsync(string channelSlug, WebhookOrderRequest request, CancellationToken cancellationToken)
    {
        var slug = Normalise(channelSlug);
        var channelId = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.Slug == slug && c.Status == ChannelStatus.Active)
            .Select(c => (Guid?)c.ChannelId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Channel), channelSlug, OrderErrorCodes.ChannelNotFound);

        // Items before the customer: an order with a line the channel does not sell creates nobody.
        var items = await ResolveItemsAsync(channelId, request.Items, cancellationToken);
        var customerId = await ResolveCustomerAsync(channelId, request.Customer, cancellationToken);

        return await orderIntake.SubmitAsync(slug, new SubmitOrderRequest(request.ExternalOrderId, customerId, items), cancellationToken);
    }

    private static string Normalise(string channelSlug) => channelSlug.Trim().ToLowerInvariant();

    /// <summary>
    /// Every line names one mapping of the channel by its key (<see cref="ChannelProductKey"/>): the external product id,
    /// or - only for a mapping without one - the external SKU. The mapping must be Active; a line without such a mapping
    /// is "not sold on this channel", whatever the reason (unknown id, mapping switched off, a mapping of another
    /// channel, the SKU of a mapping that is known by its product id).
    /// </summary>
    private async Task<List<SubmitOrderItem>> ResolveItemsAsync(
        Guid channelId,
        IReadOnlyList<WebhookOrderItem> lines,
        CancellationToken cancellationToken)
    {
        var productIds = lines.Select(l => WebhookOrderItem.Given(l.ExternalProductId)).OfType<string>().ToList();
        var skus = lines.Where(l => WebhookOrderItem.Given(l.ExternalProductId) is null)
            .Select(l => WebhookOrderItem.Given(l.ExternalSku))
            .OfType<string>()
            .ToList();

        var mappings = await dbContext.ChannelProducts
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId
                && m.Status == ChannelProductStatus.Active
                && ((m.ExternalProductId != null && productIds.Contains(m.ExternalProductId))
                    || (m.ExternalProductId == null && skus.Contains(m.ExternalSku!))))
            .Select(m => new { m.ProductId, m.ExternalProductId, m.ExternalSku })
            .ToListAsync(cancellationToken);

        // One mapping per key: both unique indexes of ChannelProducts say so.
        var products = mappings.ToDictionary(m => ChannelProductKey.Of(m.ExternalProductId, m.ExternalSku), m => m.ProductId, StringComparer.Ordinal);

        var items = new List<SubmitOrderItem>(lines.Count);
        var failures = new List<ValidationFailure>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (products.TryGetValue(line.Key(), out var productId))
            {
                items.Add(new SubmitOrderItem(productId, line.Quantity));
            }
            else
            {
                var member = WebhookOrderItem.Given(line.ExternalProductId) is null
                    ? nameof(WebhookOrderItem.ExternalSku)
                    : nameof(WebhookOrderItem.ExternalProductId);
                failures.Add(new ValidationFailure(
                    ValidationConventions.ItemPath(nameof(WebhookOrderRequest.Items), index, member),
                    ValidationMessages.Get(ValidationMessages.Keys.ItemNotSoldOnChannel)));
            }
        }

        return failures.Count == 0 ? items : throw new ValidationException(failures);
    }

    /// <summary>
    /// The customer the channel means: the one its customer mapping names, or - the first time the channel names this
    /// external customer - a new customer with a new mapping, both in one transaction. A later webhook never changes the
    /// customer: its name, phone and e-mail are read on first sight only.
    /// </summary>
    private async Task<Guid> ResolveCustomerAsync(Guid channelId, WebhookCustomer customer, CancellationToken cancellationToken)
    {
        var externalCustomerId = customer.ExternalCustomerId.Trim();
        if (await FindCustomerAsync(channelId, externalCustomerId, cancellationToken) is { } known)
        {
            return known;
        }

        var created = new Customer(customer.Name, customer.Phone, customer.Email, address: null);
        dbContext.Customers.Add(created);
        dbContext.ChannelCustomers.Add(new ChannelCustomer(channelId, externalCustomerId, created.CustomerId));

        try
        {
            // One SaveChanges = one transaction: customer and mapping are stored together or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(ExpectedUniqueViolations.ChannelCustomerExternalId))
        {
            // Another webhook naming the same new customer was first: its rows are committed, ours were rolled back.
            dbContext.ChangeTracker.Clear();
            return await FindCustomerAsync(channelId, externalCustomerId, cancellationToken)
                ?? throw new InvalidOperationException("The customer mapping that caused the unique violation was not found.", exception);
        }

        // Never the channel's customer id or anything else of the body: ids of this system only.
        logger.LogInformation("Customer {CustomerId} created for a new external customer of channel {ChannelId}", created.CustomerId, channelId);
        return created.CustomerId;
    }

    private async Task<Guid?> FindCustomerAsync(Guid channelId, string externalCustomerId, CancellationToken cancellationToken) =>
        await dbContext.ChannelCustomers
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId && m.ExternalCustomerId == externalCustomerId)
            .Select(m => (Guid?)m.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);
}

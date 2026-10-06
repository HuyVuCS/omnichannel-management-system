namespace Omnichannel.Application.Orders;

/// <summary>Orders that arrive as webhooks of a channel (doc §8.1): normalised here, then accepted by the order intake.</summary>
public interface IWebhookOrderService
{
    /// <summary>True when <paramref name="channelSlug"/> is the slug of an Active channel.</summary>
    Task<bool> IsActiveChannelAsync(string channelSlug, CancellationToken cancellationToken);

    /// <summary>
    /// Turns the channel's order into the common one and submits it through <see cref="IOrderIntakeService"/>, unchanged:
    /// every item is resolved to its product through the channel's Active mappings, the customer through the channel's
    /// customer mapping - created, with the customer, when the channel names them for the first time and never updated
    /// afterwards. A customer created for an order the intake then refuses stays.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">
    /// Unknown or inactive channel (<see cref="OrderErrorCodes.ChannelNotFound"/>).
    /// </exception>
    /// <exception cref="FluentValidation.ValidationException">
    /// An item that is not sold on the channel: no Active mapping has its key (<c>items[n].externalProductId</c> /
    /// <c>items[n].externalSku</c>).
    /// </exception>
    /// <exception cref="Common.Exceptions.ConflictException">
    /// The order id was already submitted on this channel with other content (<see cref="OrderErrorCodes.OrderConflict"/>).
    /// </exception>
    Task<SubmitOrderResponse> SubmitAsync(string channelSlug, WebhookOrderRequest request, CancellationToken cancellationToken);
}

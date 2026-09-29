namespace Omnichannel.Application.Orders;

public interface IOrderIntakeService
{
    /// <summary>
    /// Validates the order against current data, normalises it and stores a <c>ProcessingTask(OrderProcessing)</c> plus an
    /// <c>order.created</c> outbox event in one transaction. Does not create the Order and does not check stock:
    /// the Order Worker does both atomically. Resubmitting the same channel + external order id returns the existing task.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">
    /// Unknown or inactive channel (<see cref="OrderErrorCodes.ChannelNotFound"/>).
    /// </exception>
    /// <exception cref="FluentValidation.ValidationException">Unknown customer or product.</exception>
    Task<SubmitOrderResponse> SubmitAsync(string channelSlug, SubmitOrderRequest request, CancellationToken cancellationToken);
}

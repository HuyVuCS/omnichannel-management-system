namespace Omnichannel.Application.Orders;

/// <summary>
/// Idempotency key of an order submission, stored on its <c>ProcessingTask</c>:
/// <c>order:{channelId:N}:{externalOrderId}</c>. The external order id is case-sensitive and must already be trimmed.
/// </summary>
public static class OrderIdempotencyKey
{
    public static string For(Guid channelId, string externalOrderId) => $"order:{channelId:N}:{externalOrderId}";
}

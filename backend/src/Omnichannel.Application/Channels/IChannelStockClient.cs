namespace Omnichannel.Application.Channels;

/// <summary>
/// Pushes the available stock of one product to one external channel (doc §8.3) and reads what a channel shows
/// (doc §8.5, reconciliation). One implementation per kind of channel API; the workers only know this interface, so a
/// real adapter replaces the simulated one without touching them.
/// </summary>
public interface IChannelStockClient
{
    /// <summary>Sets the stock of the mapped product on the channel to <see cref="ChannelStockUpdate.AvailableStock"/>.</summary>
    /// <exception cref="ChannelApiException">The channel did not accept the update.</exception>
    Task PushAvailableStockAsync(ChannelStockUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// The available stock the channel holds for each requested product: one reading per item, in any order.
    /// The call succeeds or fails as a whole - there are no errors per item.
    /// </summary>
    /// <exception cref="ChannelApiException">The channel did not answer the read.</exception>
    Task<IReadOnlyList<ChannelStockReading>> ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken cancellationToken);
}

/// <summary>One read of a channel: at most <see cref="MaxItems"/> mapped products.</summary>
public sealed record ChannelStockQuery
{
    /// <summary>Most products one read may ask for.</summary>
    public const int MaxItems = 100;

    public ChannelStockQuery(string channelSlug, IReadOnlyList<ChannelStockItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelSlug);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, MaxItems, nameof(items));

        ChannelSlug = channelSlug;
        Items = items;
    }

    /// <summary>The channel (<c>website</c>, <c>pos</c>, <c>digital-platform</c>).</summary>
    public string ChannelSlug { get; }

    public IReadOnlyList<ChannelStockItem> Items { get; }
}

/// <summary>A mapped product as the channel knows it (the external ids of its mapping).</summary>
public sealed record ChannelStockItem(Guid ProductId, string Sku, string? ExternalProductId, string? ExternalSku);

/// <param name="AvailableStock">Null = the channel holds no stock value for the product (it never received one).</param>
public sealed record ChannelStockReading(Guid ProductId, int? AvailableStock);

/// <param name="ChannelSlug">The channel (<c>website</c>, <c>pos</c>, <c>digital-platform</c>).</param>
/// <param name="AvailableStock">The central available stock, read right before the push (never a message snapshot).</param>
/// <param name="RequestId">
/// <c>{taskId}:{attemptNo}:{n}</c>, n = push number within the attempt: lets a channel deduplicate a request it sees twice.
/// </param>
public sealed record ChannelStockUpdate(
    string ChannelSlug,
    Guid ProductId,
    string Sku,
    string? ExternalProductId,
    string? ExternalSku,
    int AvailableStock,
    Guid TaskId,
    int AttemptNo,
    string RequestId)
{
    public static string RequestIdOf(Guid taskId, int attemptNo, int push) => $"{taskId}:{attemptNo}:{push}";
}

/// <summary>
/// A channel API call failed. <see cref="Exception.Message"/> is sanitised (safe for the attempt history and the dashboard:
/// no credentials, no response bodies).
/// </summary>
/// <param name="errorCode">One of <see cref="ChannelApiErrorCodes"/>.</param>
/// <param name="isTransient">True when trying again later can succeed (retry with delay); false = permanent failure.</param>
public sealed class ChannelApiException(string errorCode, string message, bool isTransient, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string ErrorCode { get; } = errorCode;

    public bool IsTransient { get; } = isTransient;
}

/// <summary><c>ErrorCode</c> values of a failed stock-sync attempt.</summary>
public static class ChannelApiErrorCodes
{
    /// <summary>No answer within the call timeout (transient).</summary>
    public const string Timeout = "channel_timeout";

    /// <summary>The channel is down or answered 5xx (transient).</summary>
    public const string Unavailable = "channel_unavailable";

    /// <summary>The channel asked to slow down, 429 (transient).</summary>
    public const string RateLimited = "channel_rate_limited";

    /// <summary>The channel rejected the update: unknown external product, invalid value (permanent).</summary>
    public const string Rejected = "channel_rejected";

    /// <summary>Not called: the channel's circuit breaker is open after repeated transient failures (transient).</summary>
    public const string CircuitOpen = "channel_circuit_open";
}

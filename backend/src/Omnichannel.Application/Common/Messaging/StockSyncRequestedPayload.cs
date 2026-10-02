namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Payload of <see cref="EventTypes.StockSyncRequested"/>: the stock-sync task to run. The value to push is not part of the
/// message; the worker reads the current available stock when it runs.
/// </summary>
public sealed record StockSyncRequestedPayload(Guid TaskId, Guid ChannelId, Guid ProductId);

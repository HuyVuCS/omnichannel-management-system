using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Common.Messaging;

/// <summary>
/// Payload of <see cref="EventTypes.InventoryChanged"/>: a snapshot of the stock right after the movement.
/// Consumers (stock sync) must re-read the current stock before calling a channel; this snapshot may be stale.
/// </summary>
public sealed record InventoryChangedPayload(
    Guid ProductId,
    Guid MovementId,
    InventoryMovementType MovementType,
    int TotalStock,
    int ReservedStock,
    int AvailableStock,
    DateTimeOffset OccurredAt);

namespace Omnichannel.Domain.Enums;

/// <summary>
/// Business status of the order itself. Independent tasks (invoice, email, stock sync)
/// are tracked by <see cref="ProcessingTaskStatus"/> and never folded into this value.
/// </summary>
public enum OrderStatus
{
    Pending,
    Reserved,
    InsufficientStock,
    Cancelled,
    Completed
}

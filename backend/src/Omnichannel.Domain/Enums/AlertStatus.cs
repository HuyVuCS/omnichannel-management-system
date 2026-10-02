namespace Omnichannel.Domain.Enums;

public enum AlertStatus
{
    Open,
    /// <summary>A manager has seen it; the condition still holds.</summary>
    Acknowledged,
    /// <summary>Terminal: the system found the condition gone. A condition that comes back opens a new alert.</summary>
    Resolved
}

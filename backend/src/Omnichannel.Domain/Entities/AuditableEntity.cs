namespace Omnichannel.Domain.Entities;

/// <summary>
/// Base class for business entities carrying CreatedAt/By and UpdatedAt/By.
/// Values are set by the persistence layer, never by application code.
/// CreatedBy/UpdatedBy hold a user id, or "system:{worker}" for background processing.
/// </summary>
public abstract class AuditableEntity : IHasTimestamps
{
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? UpdatedBy { get; private set; }
}

namespace Omnichannel.Domain.Entities;

/// <summary>
/// Entity whose CreatedAt/UpdatedAt are maintained by the persistence layer on SaveChanges.
/// </summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? UpdatedAt { get; }
}

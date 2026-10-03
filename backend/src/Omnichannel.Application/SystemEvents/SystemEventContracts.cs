using System.Text.Json.Nodes;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.SystemEvents;

/// <summary>
/// A business/audit event (<c>SystemEventLog</c>) as the Logs/Audit page shows it. The stored payload is never returned as
/// it is: <see cref="Details"/> holds only the fields <see cref="SystemEventDetails"/> allows for the event type.
/// </summary>
/// <param name="Details">Allowed payload fields; null for an event type without an entry or a payload that is no JSON object.</param>
/// <param name="ActorEmail">E-mail of the user who did it; null when the event has no actor or the actor is no user (<c>system</c>).</param>
/// <param name="TargetEmail">E-mail of the user it was done to (user management events); null otherwise.</param>
public sealed record SystemEventResponse(
    long LogId,
    SystemEventLevel Level,
    string EventType,
    DateTimeOffset CreatedAt,
    string? CorrelationId,
    Guid? TaskId,
    Guid? OrderId,
    string? WorkerName,
    JsonObject? Details,
    string? ActorEmail,
    string? TargetEmail);

/// <summary>
/// <see cref="Level"/> and <see cref="EventType"/> are repeatable (<c>?level=Error&amp;level=Warning</c>); an event type is
/// matched exactly. <see cref="From"/>/<see cref="To"/> filter on <c>CreatedAt</c> (inclusive, at most
/// <see cref="MaxRangeDays"/> days when both are given). <c>search</c> is not supported (ignored). The only sort field is
/// the creation time, newest first unless <c>sortDir=asc</c>.
/// </summary>
public sealed record SystemEventListQuery : PageQuery
{
    public const int MaxRangeDays = 92;

    public const string SortByCreatedAt = "createdAt";

    public static readonly IReadOnlyList<string> SortFields = [SortByCreatedAt];

    public IReadOnlyList<SystemEventLevel>? Level { get; init; }
    public IReadOnlyList<string>? EventType { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? TaskId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

/// <summary>Which events only an administrator may read.</summary>
public static class SystemEventVisibility
{
    /// <summary>Prefix of the user management events (<see cref="UserEvents"/>): the audit of users is the Admin's.</summary>
    public const string AdminOnlyPrefix = "user.";
}

public interface ISystemEventService
{
    /// <summary>
    /// One page of events. Without <paramref name="includeUserEvents"/> the events whose type starts with
    /// <see cref="SystemEventVisibility.AdminOnlyPrefix"/> do not exist for the caller - also when they are asked for by
    /// <see cref="SystemEventListQuery.EventType"/> (an empty page, not an error).
    /// </summary>
    Task<PagedResult<SystemEventResponse>> ListAsync(
        SystemEventListQuery query,
        bool includeUserEvents,
        CancellationToken cancellationToken);
}

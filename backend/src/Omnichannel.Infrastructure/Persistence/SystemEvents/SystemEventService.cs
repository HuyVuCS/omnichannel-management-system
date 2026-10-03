using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.SystemEvents;

/// <summary>
/// The Logs/Audit list: one COUNT, one page query and - only when the page names users - one user lookup for all of them.
/// Payloads are reduced to their allowed fields (<see cref="SystemEventDetails"/>) after the page is read; the stored
/// payload itself never leaves this class.
/// </summary>
internal sealed class SystemEventService(OmnichannelDbContext dbContext) : ISystemEventService
{
    public async Task<PagedResult<SystemEventResponse>> ListAsync(
        SystemEventListQuery query,
        bool includeUserEvents,
        CancellationToken cancellationToken)
    {
        var filtered = dbContext.SystemEventLogs.AsNoTracking();

        // The role rule comes first and is independent of the filters: asking for a hidden type finds nothing.
        if (!includeUserEvents)
        {
            filtered = filtered.Where(e => !e.EventType.StartsWith(SystemEventVisibility.AdminOnlyPrefix));
        }

        if (query.Level is { Count: > 0 } levels)
        {
            filtered = filtered.Where(e => levels.Contains(e.Level));
        }

        if (query.EventType is { Count: > 0 } eventTypes)
        {
            filtered = filtered.Where(e => eventTypes.Contains(e.EventType));
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            var correlationId = query.CorrelationId.Trim();
            filtered = filtered.Where(e => e.CorrelationId == correlationId);
        }

        if (query.OrderId is { } orderId)
        {
            filtered = filtered.Where(e => e.OrderId == orderId);
        }

        if (query.TaskId is { } taskId)
        {
            filtered = filtered.Where(e => e.TaskId == taskId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            filtered = filtered.Where(e => e.CreatedAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            filtered = filtered.Where(e => e.CreatedAt <= toUtc);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        // Newest first unless the client asks for ascending order explicitly; the identity breaks ties in the same direction.
        var descending = query.SortDir is null || query.IsDescending;
        var ordered = filtered.OrderBy(e => e.CreatedAt, descending);
        var rows = await (descending ? ordered.ThenByDescending(e => e.LogId) : ordered.ThenBy(e => e.LogId))
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(e => new Row(e.LogId, e.Level, e.EventType, e.CreatedAt, e.CorrelationId, e.TaskId, e.OrderId, e.WorkerName, e.Payload))
            .ToListAsync(cancellationToken);

        var events = rows.Select(row =>
        {
            var details = SystemEventDetails.Filter(row.EventType, row.Payload);
            return (Row: row, Details: details, ActorId: SystemEventDetails.ActorIdOf(details), TargetId: SystemEventDetails.TargetIdOf(details));
        }).ToList();

        // Every user the page names, in one statement.
        var userIds = events.SelectMany(e => new[] { e.ActorId, e.TargetId }).OfType<Guid>().Distinct().ToList();
        var emails = userIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await dbContext.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email })
                .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        string? EmailOf(Guid? userId) => userId is { } id ? emails.GetValueOrDefault(id) : null;

        var items = events
            .Select(e => new SystemEventResponse(
                e.Row.LogId,
                e.Row.Level,
                e.Row.EventType,
                e.Row.CreatedAt,
                e.Row.CorrelationId,
                e.Row.TaskId,
                e.Row.OrderId,
                e.Row.WorkerName,
                e.Details,
                EmailOf(e.ActorId),
                EmailOf(e.TargetId)))
            .ToList();

        return new PagedResult<SystemEventResponse>(items, query.Page, query.PageSize, totalCount);
    }

    private sealed record Row(
        long LogId,
        SystemEventLevel Level,
        string EventType,
        DateTimeOffset CreatedAt,
        string? CorrelationId,
        Guid? TaskId,
        Guid? OrderId,
        string? WorkerName,
        string? Payload);
}

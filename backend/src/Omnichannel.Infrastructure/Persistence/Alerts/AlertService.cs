using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Alerts;

/// <summary>
/// Alert list, detail and acknowledge. The list is one COUNT plus one page query: product, channel, the current stock of a
/// low-stock alert and the e-mail of who acknowledged it come from joins of that statement.
/// </summary>
internal sealed class AlertService(
    OmnichannelDbContext dbContext,
    AlertStore alerts,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<AlertService> logger) : IAlertService
{
    private const string SystemActor = "system";

    public async Task<PagedResult<AlertResponse>> ListAsync(AlertListQuery query, CancellationToken cancellationToken)
    {
        var statuses = query.Statuses;
        var filtered = dbContext.Alerts.AsNoTracking().Where(a => statuses.Contains(a.Status));

        if (query.Type is { Count: > 0 } types)
        {
            filtered = filtered.Where(a => types.Contains(a.Type));
        }

        if (query.ProductId is { } productId)
        {
            filtered = filtered.Where(a => a.ProductId == productId);
        }

        if (query.ChannelId is { } channelId)
        {
            filtered = filtered.Where(a => a.ChannelId == channelId);
        }

        // Npgsql only writes UTC DateTimeOffset values to timestamptz; clients may send any offset.
        if (query.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            filtered = filtered.Where(a => a.LastOccurredAt >= fromUtc);
        }

        if (query.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            filtered = filtered.Where(a => a.LastOccurredAt <= toUtc);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        // Times and counts: the most recent / the most frequent first unless the client asks for ascending order explicitly.
        var descending = query.SortDir is null || query.IsDescending;
        IOrderedQueryable<Alert> ordered;
        if (query.IsSortedBy(AlertListQuery.SortByFirstOccurredAt))
        {
            ordered = filtered.OrderBy(a => a.FirstOccurredAt, descending);
        }
        else if (query.IsSortedBy(AlertListQuery.SortByOccurrenceCount))
        {
            ordered = filtered.OrderBy(a => a.OccurrenceCount, descending);
        }
        else
        {
            ordered = filtered.OrderBy(a => a.LastOccurredAt, descending);
        }

        var page = (descending ? ordered.ThenByDescending(a => a.AlertId) : ordered.ThenBy(a => a.AlertId))
            .Skip(query.Skip)
            .Take(query.PageSize);

        var items = await ToResponse(page).ToListAsync(cancellationToken);
        return new PagedResult<AlertResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<AlertResponse> GetAsync(Guid alertId, CancellationToken cancellationToken) =>
        await ToResponse(dbContext.Alerts.AsNoTracking().Where(a => a.AlertId == alertId)).SingleOrDefaultAsync(cancellationToken)
        ?? throw NotFound(alertId);

    public async Task<AlertResponse> AcknowledgeAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var acknowledgedBy = currentUser.UserId ?? SystemActor;

        // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            if (await alerts.AcknowledgeAsync(alertId, acknowledgedBy, cancellationToken) is { } acknowledged)
            {
                // Audit row in the same transaction: it exists exactly when the alert was acknowledged.
                dbContext.SystemEventLogs.Add(new SystemEventLog(
                    SystemEventLevel.Information,
                    AlertEvents.Acknowledged,
                    correlationContext.CorrelationId,
                    taskId: null,
                    orderId: null,
                    workerName: null,
                    MessageJson.Serialize(new { alertId, type = acknowledged.Type, acknowledgedBy }),
                    timeProvider.GetUtcNow()));
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Alert {AlertId} ({AlertType}) acknowledged by {AcknowledgedBy}", alertId, acknowledged.Type, acknowledgedBy);
            }
            else
            {
                // Nothing was written: the alert is unknown, acknowledged already (idempotent) or resolved.
                var status = await dbContext.Alerts
                    .AsNoTracking()
                    .Where(a => a.AlertId == alertId)
                    .Select(a => (AlertStatus?)a.Status)
                    .SingleOrDefaultAsync(cancellationToken)
                    ?? throw NotFound(alertId);

                if (status == AlertStatus.Resolved)
                {
                    throw new ConflictException(
                        AlertErrorCodes.AlertResolved,
                        $"Alert '{alertId}' is resolved already; there is nothing to acknowledge.");
                }
            }
        }

        return await GetAsync(alertId, cancellationToken);
    }

    private IQueryable<AlertResponse> ToResponse(IQueryable<Alert> source) =>
        source.Select(a => new AlertResponse(
            a.AlertId,
            a.Type,
            a.Status,
            a.ProductId,
            a.Product != null ? a.Product.Sku : null,
            a.Product != null ? a.Product.Name : null,
            a.ChannelId,
            a.Channel != null ? a.Channel.Name : null,
            a.TaskId,
            a.OrderId,
            a.ErrorCode,
            a.FirstOccurredAt,
            a.LastOccurredAt,
            a.OccurrenceCount,
            a.AcknowledgedAt,
            // Compared as text, like the actor of a stock movement.
            dbContext.Users.Where(u => u.Id.ToString() == a.AcknowledgedBy).Select(u => u.Email).FirstOrDefault(),
            a.ResolvedAt,
            // The stock and the threshold as they are now, for low stock only.
            a.Type == AlertType.LowStock && a.Product != null && a.Product.Inventory != null
                ? (int?)a.Product.Inventory.AvailableStock
                : null,
            a.Type == AlertType.LowStock && a.Product != null ? (int?)a.Product.LowStockThreshold : null));

    private static NotFoundException NotFound(Guid alertId) => new(nameof(Alert), alertId, AlertErrorCodes.AlertNotFound);
}

using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Infrastructure.Persistence.Orders;

namespace Omnichannel.Infrastructure.Persistence.Reports;

/// <summary>
/// Requests, list, detail and download link of exports (API). A request never builds anything: it stores the task, the
/// export row, the outbox event and the audit event in one transaction and returns; the Report Export Worker does the
/// work. Everything a caller reads is filtered by <c>CreatedBy</c> = the caller.
/// </summary>
internal sealed class ReportExportService(
    OmnichannelDbContext dbContext,
    IObjectStorage storage,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    IOptions<ReportsOptions> options,
    TimeProvider timeProvider,
    ILogger<ReportExportService> logger) : IReportExportService
{
    private static readonly ProcessingTaskStatus[] UnfinishedStatuses =
        [ProcessingTaskStatus.Pending, ProcessingTaskStatus.InProgress, ProcessingTaskStatus.RetryScheduled];

    public async Task<ExportAcceptedResponse> RequestOrdersAsync(ExportOrdersRequest request, CancellationToken cancellationToken)
    {
        var parameters = OrdersExportParameters.Normalize(request, ReportLanguages.Current());
        EnsureRangeAllowed(parameters);
        var requester = Requester();
        await EnsureBelowUnfinishedLimitAsync(requester, cancellationToken);

        // Rows of the file: one per order and one per item line.
        var orders = OrderFilters.Apply(dbContext.Orders.AsNoTracking(), parameters.ToFilter());
        var rows = await orders.LongCountAsync(cancellationToken)
            + await orders.SelectMany(o => o.Items).LongCountAsync(cancellationToken);
        EnsureNotTooLarge(rows);

        return await StoreAsync(ReportType.Orders, MessageJson.Serialize(parameters), parameters.TimeZone, requester, cancellationToken);
    }

    public async Task<ExportAcceptedResponse> RequestInventoryAsync(ExportInventoryRequest request, CancellationToken cancellationToken)
    {
        var parameters = InventoryExportParameters.Normalize(request, ReportLanguages.Current());
        var requester = Requester();
        await EnsureBelowUnfinishedLimitAsync(requester, cancellationToken);

        var rows = await InventoryFilters.Apply(dbContext.Inventories.AsNoTracking(), parameters.ToFilter())
            .LongCountAsync(cancellationToken);
        EnsureNotTooLarge(rows);

        return await StoreAsync(ReportType.Inventory, MessageJson.Serialize(parameters), parameters.TimeZone, requester, cancellationToken);
    }

    public async Task<PagedResult<ReportExportResponse>> ListAsync(ReportExportListQuery query, CancellationToken cancellationToken)
    {
        var exports = OwnExports();
        if (query.ReportType is { } reportType)
        {
            exports = exports.Where(e => e.ReportType == reportType);
        }

        var totalCount = await exports.CountAsync(cancellationToken);

        // Newest first unless the client asks for ascending order explicitly; the id breaks ties in the same direction.
        var descending = query.SortDir is null || query.IsDescending;
        var ordered = exports.OrderBy(e => e.CreatedAt, descending);
        var rows = await Project(descending ? ordered.ThenByDescending(e => e.TaskId) : ordered.ThenBy(e => e.TaskId))
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        return new PagedResult<ReportExportResponse>(rows.Select(row => ToResponse(row, now)).ToList(), query.Page, query.PageSize, totalCount);
    }

    public async Task<ReportExportResponse> GetAsync(Guid taskId, CancellationToken cancellationToken) =>
        ToResponse(await FindOwnAsync(taskId, cancellationToken), timeProvider.GetUtcNow());

    public async Task<ReportDownloadResponse> GetDownloadAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var export = await FindOwnAsync(taskId, cancellationToken);

        // Before "not ready": past its retention time an export has nothing to wait for either.
        if (ReportExportExpiry.IsPastRetention(export.DeletedAt, export.ExpiresAt, timeProvider.GetUtcNow()))
        {
            throw new ConflictException(ReportErrorCodes.ExportExpired, ValidationMessages.Get(ValidationMessages.Keys.ExportExpired));
        }

        if (export.TaskStatus != ProcessingTaskStatus.Succeeded || export.ObjectKey is null)
        {
            throw new ConflictException(ReportErrorCodes.ExportNotReady, ValidationMessages.Get(ValidationMessages.Keys.ExportNotReady));
        }

        // Computed locally, no call to the storage. The file carries its own Content-Disposition (attachment + name).
        var link = storage.PresignGet(export.ObjectKey, options.Value.DownloadLinkLifetime);
        return new ReportDownloadResponse(link.Url, link.ExpiresAt, export.FileName);
    }

    /// <summary>Task, export row, outbox event and audit event: one <c>SaveChanges</c>, so all four are stored or none.</summary>
    private async Task<ExportAcceptedResponse> StoreAsync(
        ReportType reportType,
        string parameters,
        string timeZoneId,
        string requester,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var correlationId = correlationContext.CorrelationId;
        // The validator found the zone a moment ago.
        ReportTimeZones.TryFind(timeZoneId, out var timeZone);

        // The outbox id is the RabbitMQ messageId; the task records it, so it is generated first.
        var outboxEventId = Guid.CreateVersion7();
        var task = new ProcessingTask(ProcessingTaskType.ReportExport, orderId: null, correlationId, outboxEventId.ToString());

        dbContext.ProcessingTasks.Add(task);
        dbContext.ReportExports.Add(new ReportExport(
            task.TaskId,
            reportType,
            parameters,
            ReportFileName.For(reportType, now, timeZone),
            now + options.Value.Retention));
        // What to export is not in the message: the worker reads the parameters of the row above.
        dbContext.OutboxEvents.Add(new OutboxEvent(
            EventTypes.ReportExportRequested,
            task.TaskId.ToString(),
            MessageJson.Serialize(new ReportExportRequestedPayload(task.TaskId)),
            correlationId,
            inventoryMovementId: null,
            outboxEventId));
        // Audit: an export takes bulk data out of the system.
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Information,
            ReportEvents.ExportRequested,
            correlationId,
            task.TaskId,
            orderId: null,
            workerName: null,
            MessageJson.Serialize(new { reportType, requestedBy = requester }),
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("{ReportType} export requested by {RequestedBy} as task {TaskId}", reportType, requester, task.TaskId);
        return new ExportAcceptedResponse(task.TaskId);
    }

    private void EnsureRangeAllowed(OrdersExportParameters parameters)
    {
        var maxDays = options.Value.MaxRangeDays;
        if (parameters.To - parameters.From <= TimeSpan.FromDays(maxDays))
        {
            return;
        }

        // The limit is configuration, so it is checked here and not by the validator; same key and text as its rules.
        throw new ValidationException(
        [
            new ValidationFailure(
                ValidationConventions.JsonName(nameof(ExportOrdersRequest.To)),
                ValidationMessages.Get(
                    ValidationMessages.Keys.DateRangeTooLong,
                    ("PropertyName", FieldNames.ForMember(nameof(ExportOrdersRequest.To))),
                    ("From", FieldNames.ForMember(nameof(ExportOrdersRequest.From))),
                    ("MaxDays", maxDays)))
        ]);
    }

    /// <summary>
    /// Check-then-insert, on purpose without a lock: two requests of one user at the same moment can both pass and
    /// leave the user one export above the limit. The limit protects the queue from one user filling it; one more does
    /// no harm, and a lock per user is not worth it.
    /// </summary>
    private async Task EnsureBelowUnfinishedLimitAsync(string requester, CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxUnfinishedPerUser;
        var unfinished = await dbContext.ReportExports
            .CountAsync(e => e.CreatedBy == requester && UnfinishedStatuses.Contains(e.ProcessingTask.Status), cancellationToken);

        if (unfinished >= limit)
        {
            throw new ConflictException(
                ReportErrorCodes.TooManyExports,
                ValidationMessages.Get(ValidationMessages.Keys.TooManyExports, ("MaxExports", limit)));
        }
    }

    private void EnsureNotTooLarge(long rows)
    {
        var maxRows = options.Value.MaxRows;
        if (rows > maxRows)
        {
            throw new RequestRejectedException(
                ReportErrorCodes.ExportTooLarge,
                ValidationMessages.Get(ValidationMessages.Keys.ExportTooLarge, ("RowCount", rows), ("MaxRows", maxRows)));
        }
    }

    /// <summary>The caller. Every export endpoint requires an authenticated user.</summary>
    private string Requester() =>
        currentUser.UserId ?? throw new InvalidOperationException("An export needs an authenticated user.");

    /// <summary>The caller's own exports - the only ones any method of this class reads.</summary>
    private IQueryable<ReportExport> OwnExports()
    {
        var requester = Requester();
        return dbContext.ReportExports.AsNoTracking().Where(e => e.CreatedBy == requester);
    }

    private async Task<Row> FindOwnAsync(Guid taskId, CancellationToken cancellationToken) =>
        await Project(OwnExports().Where(e => e.TaskId == taskId)).SingleOrDefaultAsync(cancellationToken)
        // Another user's export answers exactly like one that does not exist.
        ?? throw new NotFoundException(nameof(ReportExport), taskId, ReportErrorCodes.ExportNotFound);

    private static IQueryable<Row> Project(IQueryable<ReportExport> exports) =>
        exports.Select(e => new Row(
            e.TaskId,
            e.ReportType,
            e.ProcessingTask.Status,
            e.Parameters,
            e.FileName,
            e.ObjectKey,
            e.RowCount,
            e.SizeBytes,
            e.CreatedAt,
            e.CompletedAt,
            e.ExpiresAt,
            e.DeletedAt,
            // The code of the last attempt; its message stays where it is.
            e.ProcessingTask.Attempts.OrderByDescending(a => a.AttemptNo).Select(a => a.ErrorCode).FirstOrDefault()));

    private static ReportExportResponse ToResponse(Row row, DateTimeOffset now)
    {
        var status = ReportExportExpiry.StatusOf(row.TaskStatus, row.DeletedAt, row.ExpiresAt, now);
        return new ReportExportResponse(
            row.TaskId,
            row.ReportType,
            status,
            JsonNode.Parse(row.Parameters) as JsonObject,
            row.FileName,
            row.RowCount,
            row.SizeBytes,
            row.CreatedAt,
            row.CompletedAt,
            row.ExpiresAt,
            status == ReportExportStatus.Failed ? row.LastErrorCode : null);
    }

    private sealed record Row(
        Guid TaskId,
        ReportType ReportType,
        ProcessingTaskStatus TaskStatus,
        string Parameters,
        string FileName,
        string? ObjectKey,
        int? RowCount,
        long? SizeBytes,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? DeletedAt,
        string? LastErrorCode);
}

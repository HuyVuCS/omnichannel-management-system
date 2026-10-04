using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Reports;

/// <summary>
/// Exports as their requester sees them (API). Every export runs in the background: a request stores a processing task,
/// the export row, the <c>report.export.requested</c> outbox event and an audit event in one transaction and returns;
/// the Report Export Worker builds the file. Reading and downloading are for the requester alone - for anybody else,
/// an administrator included, the export does not exist.
/// </summary>
public interface IReportExportService
{
    /// <exception cref="FluentValidation.ValidationException">The range is longer than <c>Reports:MaxRangeDays</c>.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><c>too_many_exports</c>.</exception>
    /// <exception cref="Common.Exceptions.RequestRejectedException"><c>export_too_large</c>; nothing was written.</exception>
    Task<ExportAcceptedResponse> RequestOrdersAsync(ExportOrdersRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.ConflictException"><c>too_many_exports</c>.</exception>
    /// <exception cref="Common.Exceptions.RequestRejectedException"><c>export_too_large</c>; nothing was written.</exception>
    Task<ExportAcceptedResponse> RequestInventoryAsync(ExportInventoryRequest request, CancellationToken cancellationToken);

    Task<PagedResult<ReportExportResponse>> ListAsync(ReportExportListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException"><c>export_not_found</c>, also for another user's export.</exception>
    Task<ReportExportResponse> GetAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>A short-lived link to the file. The link is a credential: it is returned and never logged.</summary>
    /// <exception cref="Common.Exceptions.NotFoundException"><c>export_not_found</c>, also for another user's export.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><c>export_expired</c>, <c>export_not_ready</c>.</exception>
    Task<ReportDownloadResponse> GetDownloadAsync(Guid taskId, CancellationToken cancellationToken);
}

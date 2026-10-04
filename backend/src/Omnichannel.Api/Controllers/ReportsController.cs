using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Reports;

namespace Omnichannel.Api.Controllers;

/// <summary>
/// Excel exports. Every export is built in the background: a request is answered at once with the task that builds
/// the file. Managers and administrators may export; an export is visible to the user who asked for it and to nobody
/// else.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
public sealed class ReportsController(IReportExportService reportExportService) : ControllerBase
{
    /// <summary>Asks for an orders export (the filters of the order list, a required range and a time zone): 202 with the task.</summary>
    [HttpPost("orders/export")]
    [ProducesResponseType<ExportAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExportAcceptedResponse>> ExportOrders(ExportOrdersRequest request, CancellationToken cancellationToken) =>
        AcceptedExport(await reportExportService.RequestOrdersAsync(request, cancellationToken));

    /// <summary>Asks for an inventory export (the filters of the inventory list and a time zone): 202 with the task.</summary>
    [HttpPost("inventory/export")]
    [ProducesResponseType<ExportAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExportAcceptedResponse>> ExportInventory(ExportInventoryRequest request, CancellationToken cancellationToken) =>
        AcceptedExport(await reportExportService.RequestInventoryAsync(request, cancellationToken));

    /// <summary>The caller's own exports, newest first.</summary>
    [HttpGet("exports")]
    [ProducesResponseType<PagedResult<ReportExportResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ReportExportResponse>>> List(
        [FromQuery] ReportExportListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await reportExportService.ListAsync(query, cancellationToken));

    /// <summary>One of the caller's own exports; 404 for any other.</summary>
    [HttpGet("exports/{taskId:guid}")]
    [ProducesResponseType<ReportExportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReportExportResponse>> Get(Guid taskId, CancellationToken cancellationToken) =>
        Ok(await reportExportService.GetAsync(taskId, cancellationToken));

    /// <summary>A link that downloads the file of a succeeded export; valid for a few minutes, usable without the API's token.</summary>
    [HttpGet("exports/{taskId:guid}/download")]
    [ProducesResponseType<ReportDownloadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReportDownloadResponse>> Download(Guid taskId, CancellationToken cancellationToken) =>
        Ok(await reportExportService.GetDownloadAsync(taskId, cancellationToken));

    private AcceptedAtActionResult AcceptedExport(ExportAcceptedResponse export) =>
        AcceptedAtAction(nameof(Get), new { taskId = export.TaskId }, export);
}

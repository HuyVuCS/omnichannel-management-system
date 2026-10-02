using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Dashboard;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class DashboardController(IDashboardService dashboardService) : ControllerBase
{
    /// <summary>
    /// Numbers of the overview page: orders and finished tasks in the range (<c>from</c>/<c>to</c>, default the last 24
    /// hours, at most 92 days), current stock, waiting tasks, stock sync per channel and outbox health.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType<DashboardSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DashboardSummaryResponse>> Summary(
        [FromQuery] DashboardSummaryQuery query,
        CancellationToken cancellationToken) =>
        Ok(await dashboardService.GetSummaryAsync(query, cancellationToken));
}

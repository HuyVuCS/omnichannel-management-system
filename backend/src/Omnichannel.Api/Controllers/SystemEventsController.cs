using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.SystemEvents;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/system-events")]
[Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
public sealed class SystemEventsController(ISystemEventService systemEventService) : ControllerBase
{
    /// <summary>
    /// Business/audit events (Logs/Audit), newest first. User management events (<c>user.*</c>) are returned to an
    /// administrator only; for a manager they do not exist, whatever the filter.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<SystemEventResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<SystemEventResponse>>> List(
        [FromQuery] SystemEventListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await systemEventService.ListAsync(query, includeUserEvents: User.IsInRole(AppRoles.Admin), cancellationToken));
}

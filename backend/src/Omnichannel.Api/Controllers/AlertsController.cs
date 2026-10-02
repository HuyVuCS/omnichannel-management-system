using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/alerts")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class AlertsController(IAlertService alertService) : ControllerBase
{
    /// <summary>Alerts, by default the unresolved ones (<c>Open</c> and <c>Acknowledged</c>), most recent occurrence first.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<AlertResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AlertResponse>>> List(
        [FromQuery] AlertListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await alertService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await alertService.GetAsync(id, cancellationToken));

    /// <summary>
    /// <c>Open</c> → <c>Acknowledged</c> (200 with the alert; also when it is acknowledged already). It does not resolve the
    /// alert: the system does, when the condition is gone. A resolved alert answers 409 <c>alert_resolved</c>.
    /// </summary>
    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlertResponse>> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        Ok(await alertService.AcknowledgeAsync(id, cancellationToken));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Processing;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/processing/tasks")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class ProcessingController(IProcessingTaskService processingTaskService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ProcessingTaskSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ProcessingTaskSummaryResponse>>> List(
        [FromQuery] ProcessingTaskListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await processingTaskService.ListAsync(query, cancellationToken));

    /// <summary>The task, all its attempts, and the publish state of its current message.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProcessingTaskDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessingTaskDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await processingTaskService.GetAsync(id, cancellationToken));

    /// <summary>Manual retry of a Failed task (202): its original message is republished through the outbox.</summary>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<RetryProcessingTaskResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RetryProcessingTaskResponse>> Retry(Guid id, CancellationToken cancellationToken) =>
        Accepted(await processingTaskService.RetryAsync(id, cancellationToken));
}

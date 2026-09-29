using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Orders;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class OrdersController(IOrderIntakeService orderIntakeService) : ControllerBase
{
    /// <summary>Accepts an order for asynchronous processing (202). Resubmitting the same order returns the same task.</summary>
    [HttpPost("{slug}")]
    [ProducesResponseType<SubmitOrderResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubmitOrderResponse>> Submit(
        string slug,
        SubmitOrderRequest request,
        CancellationToken cancellationToken) =>
        Accepted(await orderIntakeService.SubmitAsync(slug, request, cancellationToken));
}

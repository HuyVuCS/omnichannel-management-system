using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Omnichannel.Api.Auth;
using Omnichannel.Api.Filters;
using Omnichannel.Application.Orders;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class WebhooksController(IWebhookOrderService webhookOrders) : ControllerBase
{
    /// <summary>
    /// A new order sent by a channel, in the channel's own identifiers. Anonymous to JWT: the caller is authenticated by
    /// the signature of the request (<see cref="WebhookSignatureFilter"/>), after the rate limit and before the body is
    /// read as JSON. Accepted for asynchronous processing like an order of the order-entry page (202, the same task for
    /// the same order).
    /// </summary>
    [HttpPost("{slug}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Webhook)]
    [ServiceFilter<WebhookSignatureFilter>]
    [ProducesResponseType<SubmitOrderResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SubmitOrderResponse>> Receive(
        string slug,
        WebhookOrderRequest request,
        CancellationToken cancellationToken) =>
        Accepted(await webhookOrders.SubmitAsync(slug, request, cancellationToken));
}

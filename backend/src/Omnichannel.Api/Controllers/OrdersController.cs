using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Orders;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class OrdersController(
    IOrderIntakeService orderIntakeService,
    IOrderQueryService orderQueryService,
    IOrderPaymentService orderPaymentService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<OrderSummaryResponse>>> List(
        [FromQuery] OrderListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await orderQueryService.ListAsync(query, cancellationToken));

    /// <summary>The order with its lines and every processing task (with all attempts).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await orderQueryService.GetAsync(id, cancellationToken));

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

    /// <summary>
    /// Records that a <c>Reserved</c> order was paid and queues its invoice and - when the customer has an e-mail
    /// address - its payment confirmation. The order's status and the stock do not change.
    /// </summary>
    [HttpPost("{orderId:guid}/payment")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<PayOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PayOrderResponse>> Pay(
        Guid orderId,
        PayOrderRequest request,
        CancellationToken cancellationToken) =>
        Ok(await orderPaymentService.PayAsync(orderId, request, cancellationToken));
}

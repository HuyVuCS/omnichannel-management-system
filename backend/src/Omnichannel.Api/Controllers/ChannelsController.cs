using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class ChannelsController(IChannelService channelService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ChannelResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ChannelResponse>>> List(CancellationToken cancellationToken) =>
        Ok(await channelService.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await channelService.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChannelResponse>> Create(CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await channelService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = channel.ChannelId }, channel);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelResponse>> Update(
        Guid id,
        UpdateChannelRequest request,
        CancellationToken cancellationToken) =>
        Ok(await channelService.UpdateAsync(id, request, cancellationToken));

    /// <summary>Products mapped on the channel (search: SKU, product name, external product id, external SKU).</summary>
    [HttpGet("{channelId:guid}/products")]
    [ProducesResponseType<PagedResult<ChannelProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ChannelProductResponse>>> ListProducts(
        Guid channelId,
        [FromQuery] ChannelProductListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await channelService.ListProductsAsync(channelId, query, cancellationToken));

    /// <summary>Maps a product on the channel; <c>syncTaskId</c> is the task that pushes its current stock to the channel.</summary>
    [HttpPost("{channelId:guid}/products")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<ChannelProductSavedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChannelProductSavedResponse>> CreateProduct(
        Guid channelId,
        CreateChannelProductRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await channelService.CreateProductAsync(channelId, request, cancellationToken);
        return Created($"/api/channels/{channelId}/products/{mapping.ChannelProductId}", mapping);
    }

    /// <summary>Changes the external ids and the status of a mapping (no delete: deactivate instead).</summary>
    [HttpPut("{channelId:guid}/products/{channelProductId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.ManagerOrAdmin)]
    [ProducesResponseType<ChannelProductSavedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChannelProductSavedResponse>> UpdateProduct(
        Guid channelId,
        Guid channelProductId,
        UpdateChannelProductRequest request,
        CancellationToken cancellationToken) =>
        Ok(await channelService.UpdateProductAsync(channelId, channelProductId, request, cancellationToken));
}

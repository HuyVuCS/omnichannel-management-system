using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Channels;

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
}

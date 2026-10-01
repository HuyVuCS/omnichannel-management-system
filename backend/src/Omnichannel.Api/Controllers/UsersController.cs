using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Users;

namespace Omnichannel.Api.Controllers;

/// <summary>User management for administrators. Role changes, locks and password resets revoke the user's current tokens.</summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<UserResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<UserResponse>>> List(
        [FromQuery] UserListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await userService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await userService.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await userService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = user.UserId }, user);
    }

    [HttpPut("{id:guid}/role")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> ChangeRole(
        Guid id,
        ChangeUserRoleRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ChangeRoleAsync(id, request, cancellationToken));

    /// <summary>Locks the user until an admin unlocks them.</summary>
    [HttpPost("{id:guid}/lock")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Lock(Guid id, CancellationToken cancellationToken) =>
        Ok(await userService.LockAsync(id, cancellationToken));

    [HttpPost("{id:guid}/unlock")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Unlock(Guid id, CancellationToken cancellationToken) =>
        Ok(await userService.UnlockAsync(id, cancellationToken));

    /// <summary>Sets a temporary password chosen by the admin (there is no e-mail flow yet).</summary>
    [HttpPost("{id:guid}/reset-password")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> ResetPassword(
        Guid id,
        ResetUserPasswordRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ResetPasswordAsync(id, request, cancellationToken));
}

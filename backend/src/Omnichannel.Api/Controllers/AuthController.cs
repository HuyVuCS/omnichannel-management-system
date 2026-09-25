using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Auth;

namespace Omnichannel.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType<LoginResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResult>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);
        if (result is null)
        {
            // Single generic message: do not reveal whether the account exists or is locked.
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid credentials",
                detail: "The email or password is incorrect, or the account is temporarily locked.");
        }

        return Ok(result);
    }

    /// <summary>Current user and roles, read from the store (not just the token) so the UI sees role changes.</summary>
    [HttpGet("me")]
    [ProducesResponseType<AuthenticatedUser>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticatedUser>> Me(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(JwtClaimNames.Subject)?.Value, out var userId))
        {
            return Unauthorized();
        }

        var user = await authService.GetUserAsync(userId, cancellationToken);
        return user is null ? Unauthorized() : Ok(user);
    }
}

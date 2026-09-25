using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Auth;

namespace Omnichannel.Infrastructure.Identity;

internal sealed class IdentityAuthService(
    UserManager<ApplicationUser> userManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IAccessTokenIssuer tokenIssuer,
    ILogger<IdentityAuthService> logger) : IAuthService
{
    private const string TokenType = "Bearer";

    // Hash compared against when the user does not exist, so response time does not reveal
    // whether an account exists. Computed once per process.
    private static string? _dummyPasswordHash;

    public async Task<LoginResult?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            SimulatePasswordCheck(request.Password);
            logger.LogWarning("Login failed: unknown account");
            return null;
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login rejected: user {UserId} is locked out", user.Id);
            return null;
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            // Counts towards lockout (Identity LockoutOptions).
            await userManager.AccessFailedAsync(user);
            logger.LogWarning("Login failed: invalid password for user {UserId}", user.Id);
            return null;
        }

        if (user.AccessFailedCount > 0)
        {
            await userManager.ResetAccessFailedCountAsync(user);
        }

        var roles = (await userManager.GetRolesAsync(user)).ToArray();
        var email = user.Email ?? user.UserName ?? string.Empty;
        var token = tokenIssuer.Issue(user.Id, email, roles);

        logger.LogInformation("User {UserId} logged in", user.Id);

        return new LoginResult(
            token.Token,
            TokenType,
            token.ExpiresAt,
            new AuthenticatedUser(user.Id, email, roles));
    }

    public async Task<AuthenticatedUser?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || await userManager.IsLockedOutAsync(user))
        {
            return null;
        }

        var roles = (await userManager.GetRolesAsync(user)).ToArray();
        return new AuthenticatedUser(user.Id, user.Email ?? user.UserName ?? string.Empty, roles);
    }

    private void SimulatePasswordCheck(string password)
    {
        var dummyUser = new ApplicationUser();
        _dummyPasswordHash ??= passwordHasher.HashPassword(dummyUser, Guid.NewGuid().ToString("N"));
        passwordHasher.VerifyHashedPassword(dummyUser, _dummyPasswordHash, password);
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Omnichannel.Application.Auth;

namespace Omnichannel.Api.Auth;

/// <summary>
/// Revocation check for access tokens (runs after signature, issuer, audience and lifetime were validated). A token is
/// rejected when its user no longer exists, the user's security stamp differs from the token's <c>sstamp</c> claim (role
/// change, lock, password reset), or the user is locked out. The lookup is cached per user
/// (<see cref="IUserSecurityStateProvider"/>), so revocation takes effect within the cache window (30 s by default) on other
/// API instances and at once on the instance that made the change.
/// </summary>
public static class TokenRevocation
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var subject = context.Principal?.FindFirst(JwtClaimNames.Subject)?.Value;
        var stamp = context.Principal?.FindFirst(JwtClaimNames.SecurityStamp)?.Value;

        if (!Guid.TryParse(subject, out var userId) || string.IsNullOrEmpty(stamp))
        {
            // Also tokens issued before the stamp claim existed: they cannot be revoked, so they are not accepted.
            context.Fail("The token carries no subject or security stamp.");
            return;
        }

        var state = await services.GetRequiredService<IUserSecurityStateProvider>()
            .GetAsync(userId, context.HttpContext.RequestAborted);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (state is null
            || !string.Equals(state.SecurityStamp, stamp, StringComparison.Ordinal)
            || state.IsLockedOut(now))
        {
            // One generic reason: do not tell the caller which of the three it was.
            context.Fail("The token is no longer valid for this user.");
        }
    }
}

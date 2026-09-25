using Omnichannel.Application.Common.Abstractions;

namespace Omnichannel.Api.Auth;

public sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string? UserId => httpContextAccessor.HttpContext?.User.FindFirst(JwtClaimNames.Subject)?.Value;
}

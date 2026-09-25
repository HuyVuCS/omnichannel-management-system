namespace Omnichannel.Application.Auth;

/// <summary>Issues signed access tokens. Implemented by the API host (JWT).</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(Guid userId, string email, IReadOnlyCollection<string> roles);
}

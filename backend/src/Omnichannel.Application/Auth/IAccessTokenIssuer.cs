namespace Omnichannel.Application.Auth;

/// <summary>Issues signed access tokens. Implemented by the API host (JWT).</summary>
public interface IAccessTokenIssuer
{
    /// <param name="securityStamp">
    /// The user's current security stamp. It is embedded in the token; the token is rejected once the stamp changes
    /// (role change, lock, password reset).
    /// </param>
    AccessToken Issue(Guid userId, string email, IReadOnlyCollection<string> roles, string securityStamp);
}

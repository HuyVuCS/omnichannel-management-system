namespace Omnichannel.Application.Auth;

public interface IAuthService
{
    /// <summary>
    /// Verifies credentials and issues an access token. Returns null for any failure
    /// (unknown user, wrong password, locked out) so callers cannot enumerate accounts.
    /// </summary>
    Task<LoginResult?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    /// <summary>Loads the user and current roles from the store; null if the user no longer exists or is locked out.</summary>
    Task<AuthenticatedUser?> GetUserAsync(Guid userId, CancellationToken cancellationToken);
}

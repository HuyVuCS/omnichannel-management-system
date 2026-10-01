namespace Omnichannel.Application.Auth;

/// <summary>
/// What decides whether an already issued access token is still acceptable. Lockout is deliberately not part of it: a
/// temporary lockout after failed logins must not end active sessions (anyone who knows an e-mail address could otherwise
/// kick that user out); an admin lock changes the stamp and so revokes on its own.
/// </summary>
/// <param name="SecurityStamp">The user's current Identity security stamp; tokens carry the stamp they were issued with.</param>
public sealed record UserSecurityState(string? SecurityStamp);

/// <summary>
/// Current security state of a user for access-token revocation. Lookups are cached per user for a short time
/// (in-process), so a change made by another API instance takes effect within that window; the instance that makes the
/// change calls <see cref="Evict"/> and sees it at once.
/// </summary>
public interface IUserSecurityStateProvider
{
    /// <summary>Null when the user does not exist (any more).</summary>
    Task<UserSecurityState?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Forgets the cached state of <paramref name="userId"/> in this process.</summary>
    void Evict(Guid userId);
}

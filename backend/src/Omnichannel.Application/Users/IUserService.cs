using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Users;

/// <summary>
/// User management for administrators (doc §6). Every user has exactly one role. Operations that change a user's rights
/// (role, lock, password) issue a new security stamp, which revokes the access tokens issued before.
/// </summary>
public interface IUserService
{
    Task<PagedResult<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException"><c>user_not_found</c>.</exception>
    Task<UserResponse> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Creates the user and assigns the role in one transaction.</summary>
    /// <exception cref="FluentValidation.ValidationException">Password policy (key <c>password</c>), invalid e-mail or role.</exception>
    /// <exception cref="Common.Exceptions.ConflictException"><c>user_email_exists</c>.</exception>
    Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.ConflictException"><c>cannot_modify_self</c>, <c>last_admin</c>.</exception>
    Task<UserResponse> ChangeRoleAsync(Guid userId, ChangeUserRoleRequest request, CancellationToken cancellationToken);

    /// <summary>Locks the user until an admin unlocks them.</summary>
    /// <exception cref="Common.Exceptions.ConflictException"><c>cannot_modify_self</c>, <c>last_admin</c>.</exception>
    Task<UserResponse> LockAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Clears the lockout and the failed-login counter.</summary>
    Task<UserResponse> UnlockAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Replaces the password with a temporary one chosen by the admin.</summary>
    /// <exception cref="FluentValidation.ValidationException">Password policy (key <c>newPassword</c>).</exception>
    Task<UserResponse> ResetPasswordAsync(Guid userId, ResetUserPasswordRequest request, CancellationToken cancellationToken);
}

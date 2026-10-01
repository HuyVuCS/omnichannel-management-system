using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Users;

/// <param name="Role">The user's role (<c>Admin</c>, <c>Manager</c> or <c>Staff</c>); null only for a user without any role.</param>
/// <param name="IsLocked">True while the user cannot log in (locked by an admin, or temporarily after failed logins).</param>
/// <param name="LockoutEnd">End of the lockout; a date in the year 9999 means "until an admin unlocks the user".</param>
public sealed record UserResponse(
    Guid UserId,
    string Email,
    string? Role,
    bool IsLocked,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount);

/// <summary>Body of <c>POST /api/users</c>. The password must satisfy the Identity password policy.</summary>
public sealed record CreateUserRequest(string Email, string Password, string Role);

/// <summary>Body of <c>PUT /api/users/{id}/role</c>.</summary>
public sealed record ChangeUserRoleRequest(string Role);

/// <summary>Body of <c>POST /api/users/{id}/reset-password</c>: a temporary password set by an admin.</summary>
public sealed record ResetUserPasswordRequest(string NewPassword);

/// <summary>Search matches the e-mail address. Default order: e-mail ascending.</summary>
public sealed record UserListQuery : PageQuery
{
    public const string SortByEmail = "email";

    public static readonly IReadOnlyList<string> SortFields = [SortByEmail];

    /// <summary>Role name, case-insensitive.</summary>
    public string? Role { get; init; }

    /// <summary>True: only users that are locked right now; false: only users that are not.</summary>
    public bool? Locked { get; init; }
}

public static class UserErrorCodes
{
    public const string UserNotFound = "user_not_found";

    /// <summary>A user with this e-mail address (compared case-insensitively) already exists.</summary>
    public const string EmailExists = "user_email_exists";

    /// <summary>The operation would leave no unlocked Admin (demoting or locking the last one).</summary>
    public const string LastAdmin = "last_admin";

    /// <summary>An admin cannot change their own role or lock themselves.</summary>
    public const string CannotModifySelf = "cannot_modify_self";
}

/// <summary>
/// SystemEventLog event types (Information) written by user management. Every payload has <c>targetUserId</c> and
/// <c>actorUserId</c> (ids only: no e-mail addresses, never passwords).
/// </summary>
public static class UserEvents
{
    /// <summary>Payload also has <c>role</c>.</summary>
    public const string Created = "user.created";

    /// <summary>Payload also has <c>from</c> and <c>to</c>.</summary>
    public const string RoleChanged = "user.role_changed";

    public const string Locked = "user.locked";
    public const string Unlocked = "user.unlocked";
    public const string PasswordReset = "user.password_reset";
}

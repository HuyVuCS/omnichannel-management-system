namespace Omnichannel.Api.Auth;

/// <summary>
/// RBAC policy names for [Authorize(Policy = ...)]. Every endpoint requires an authenticated
/// user by default (fallback policy); use [AllowAnonymous] explicitly for public endpoints.
/// </summary>
public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string ManagerOrAdmin = "ManagerOrAdmin";
    public const string AnyStaff = "AnyStaff";
}

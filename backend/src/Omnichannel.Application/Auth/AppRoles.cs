namespace Omnichannel.Application.Auth;

/// <summary>RBAC roles. Names are persisted in AspNetRoles - do not rename without a migration.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Staff = "Staff";

    public static readonly IReadOnlyList<string> All = [Admin, Manager, Staff];
}

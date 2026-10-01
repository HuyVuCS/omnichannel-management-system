namespace Omnichannel.Application.Auth;

/// <summary>RBAC roles. Names are persisted in AspNetRoles - do not rename without a migration.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Staff = "Staff";

    public static readonly IReadOnlyList<string> All = [Admin, Manager, Staff];

    /// <summary>Maps a role name in any casing (with surrounding white space) to its canonical name.</summary>
    /// <returns>False when <paramref name="role"/> is not one of <see cref="All"/>.</returns>
    public static bool TryNormalize(string? role, out string normalized)
    {
        var trimmed = role?.Trim();
        normalized = All.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return normalized.Length > 0;
    }
}

namespace Omnichannel.Infrastructure.Identity;

/// <summary>
/// Optional first administrator, created on API start-up when both values are set and no user
/// with that email exists. Supply via environment variables / secret manager only
/// (BootstrapAdmin__Email, BootstrapAdmin__Password) and remove after first start.
/// </summary>
public sealed class BootstrapAdminOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

namespace Omnichannel.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    /// <summary>Bound from ConnectionStrings:Default. Must use the least-privilege runtime role.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

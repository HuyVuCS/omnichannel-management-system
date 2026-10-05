namespace Omnichannel.Infrastructure.ExternalChannels;

/// <summary>
/// Bound from the <c>ChannelSimulation</c> configuration section and read on every call (<c>IOptionsMonitor</c>): a change
/// of the settings file applies without restarting the Workers host, so a channel can be made to fail and recover during a
/// demo. Only the simulated channel client reads it.
/// </summary>
public sealed class ChannelSimulationOptions
{
    public const string SectionName = "ChannelSimulation";

    /// <summary>External SKU prefix the simulator rejects for good (<c>channel_rejected</c>) when <see cref="SkuRules"/> is on.</summary>
    public const string PermanentErrorSkuPrefix = "SIM-PERM-";

    /// <summary>
    /// External SKU prefix that fails with <c>channel_unavailable</c> on attempt 1 only and succeeds afterwards, when
    /// <see cref="SkuRules"/> is on.
    /// </summary>
    public const string TransientErrorSkuPrefix = "SIM-TEMP-";

    /// <summary>
    /// External SKU prefix of a product whose updates the channel accepts and then loses, when <see cref="SkuRules"/> is
    /// on: the push succeeds but the channel keeps no value, so a read never shows the central stock (permanent drift,
    /// for demonstrating reconciliation).
    /// </summary>
    public const string LostUpdateSkuPrefix = "SIM-LOST-";

    /// <summary>Behaviour per channel slug (<c>website</c>, <c>pos</c>, <c>digital-platform</c>); a channel without an entry succeeds.</summary>
    public Dictionary<string, SimulatedChannelSettings> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic behaviour by external SKU prefix (<see cref="PermanentErrorSkuPrefix"/>,
    /// <see cref="TransientErrorSkuPrefix"/>, <see cref="LostUpdateSkuPrefix"/>). Pushes only: reads are not affected.
    /// Off by default; switched on in <c>appsettings.Development.json</c> only.
    /// </summary>
    public bool SkuRules { get; set; }

    public SimulatedChannelSettings For(string channelSlug) =>
        Channels.FirstOrDefault(entry => string.Equals(entry.Key, channelSlug, StringComparison.OrdinalIgnoreCase)).Value
        ?? SimulatedChannelSettings.Default;
}

public sealed class SimulatedChannelSettings
{
    public static SimulatedChannelSettings Default { get; } = new();

    /// <summary>
    /// <c>Succeed</c> (default), <c>TransientError</c>, <c>PermanentError</c> or <c>Timeout</c> (<see cref="ChannelSimulationMode"/>).
    /// Kept as text: a typing error in a file that is edited while the host runs must fail the push with a clear message, not
    /// break the configuration reload.
    /// </summary>
    public string Mode { get; set; } = nameof(ChannelSimulationMode.Succeed);

    /// <summary>Time every call takes before it answers.</summary>
    public int LatencyMs { get; set; }
}

public enum ChannelSimulationMode
{
    /// <summary>The channel accepts the update.</summary>
    Succeed,

    /// <summary>The channel is unavailable: <c>channel_unavailable</c>, retried with delay.</summary>
    TransientError,

    /// <summary>The channel rejects the update: <c>channel_rejected</c>, no retry.</summary>
    PermanentError,

    /// <summary>The channel never answers; the call ends when the caller's timeout cancels it (<c>channel_timeout</c>).</summary>
    Timeout
}

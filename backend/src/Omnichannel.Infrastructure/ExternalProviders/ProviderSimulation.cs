namespace Omnichannel.Infrastructure.ExternalProviders;

/// <summary>How a simulated provider answers every call (the modes of the simulated channels).</summary>
public enum ProviderSimulationMode
{
    /// <summary>The provider does what it is asked.</summary>
    Succeed,

    /// <summary>The provider is unavailable: retried with delay.</summary>
    TransientError,

    /// <summary>The provider refuses: no retry.</summary>
    PermanentError,

    /// <summary>The provider never answers; the call ends when the caller's timeout cancels it.</summary>
    Timeout
}

/// <summary>
/// Settings of one simulated provider, read on every call (<c>IOptionsMonitor</c>): a change of the settings file applies
/// without restarting the Workers host, so the provider can be made to fail and recover during a demo.
/// </summary>
public abstract class ProviderSimulationOptions
{
    /// <summary>
    /// <c>Succeed</c> (default), <c>TransientError</c>, <c>PermanentError</c> or <c>Timeout</c>
    /// (<see cref="ProviderSimulationMode"/>). Kept as text: a typing error in a file that is edited while the host runs
    /// must fail the call with a clear message, not break the configuration reload.
    /// </summary>
    public string Mode { get; set; } = nameof(ProviderSimulationMode.Succeed);

    /// <summary>Time every call takes before it answers.</summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Deterministic failures for single orders or recipients (the prefixes and domains each simulator documents). Off by
    /// default; switched on in <c>appsettings.Development.json</c> only.
    /// </summary>
    public bool Rules { get; set; }

    /// <summary>The configured mode, after the configured latency has passed.</summary>
    internal async Task<ProviderSimulationMode> ModeAfterLatencyAsync(string sectionName, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ProviderSimulationMode>(Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                $"{sectionName}:Mode '{Mode}' is not one of {string.Join(", ", Enum.GetNames<ProviderSimulationMode>())}.");
        }

        if (LatencyMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(LatencyMs), cancellationToken);
        }

        return mode;
    }
}

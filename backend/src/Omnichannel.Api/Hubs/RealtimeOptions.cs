using Microsoft.Extensions.Options;

namespace Omnichannel.Api.Hubs;

/// <summary>Bound from the <c>Realtime</c> configuration section.</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>
    /// Hints that arrive within this window after the first one go to the clients as one hub message, so a burst of changes
    /// causes one refetch instead of many.
    /// </summary>
    public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(250);
}

public sealed class RealtimeOptionsValidator : IValidateOptions<RealtimeOptions>
{
    public static readonly TimeSpan MinBatchWindow = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan MaxBatchWindow = TimeSpan.FromSeconds(5);

    public ValidateOptionsResult Validate(string? name, RealtimeOptions options) =>
        options.BatchWindow < MinBatchWindow || options.BatchWindow > MaxBatchWindow
            ? ValidateOptionsResult.Fail("Realtime:BatchWindow must be between 10 ms and 5 s.")
            : ValidateOptionsResult.Success;
}

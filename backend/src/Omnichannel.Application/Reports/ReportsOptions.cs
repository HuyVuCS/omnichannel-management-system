namespace Omnichannel.Application.Reports;

/// <summary>
/// Bound from the <c>Reports</c> configuration section by the API (limits of a request, lifetime of a download link)
/// and by the Workers host (row limit at generation, retention sweep).
/// </summary>
public sealed class ReportsOptions
{
    public const string SectionName = "Reports";

    /// <summary>
    /// Most data rows of one export: orders plus their item lines, or products. Checked when the export is requested
    /// (<c>export_too_large</c>, nothing is written) and again by the worker, because the data can grow in between.
    /// The workbook is built in memory, so this bounds the memory of the Workers process.
    /// </summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>Exports of one user that may be waiting or running at the same time (<c>too_many_exports</c> above it).</summary>
    public int MaxUnfinishedPerUser { get; set; } = 3;

    /// <summary>How long the file of an export is kept, counted from the request.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Pause between two rounds of the retention sweep (Workers host).</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Exports the retention sweep handles per batch.</summary>
    public int CleanupBatchSize { get; set; } = 100;

    /// <summary>How long a download link is valid. It is used at once, so it is short.</summary>
    public TimeSpan DownloadLinkLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Longest range (<c>from</c> to <c>to</c>) of an orders export, in days.</summary>
    public int MaxRangeDays { get; set; } = 366;
}

/// <summary>Ranges only. Returns one message per setting that is out of range; none when the options are valid.</summary>
public static class ReportsOptionsRules
{
    /// <summary>A sheet holds 1,048,575 data rows below its header; one export never fills more than one sheet per row kind.</summary>
    public const int MaxRowsLimit = 1_000_000;

    public const int MaxUnfinishedPerUserLimit = 100;
    public const int CleanupBatchSizeLimit = 1_000;
    public const int MaxRangeDaysLimit = 3_660;

    public static readonly TimeSpan MinRetention = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxRetention = TimeSpan.FromDays(366);
    public static readonly TimeSpan MinCleanupInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan MinDownloadLinkLifetime = TimeSpan.FromSeconds(1);

    /// <summary>The longest lifetime a SigV4 presigned link can have.</summary>
    public static readonly TimeSpan MaxDownloadLinkLifetime = TimeSpan.FromDays(7);

    public static IReadOnlyList<string> Validate(ReportsOptions options)
    {
        var failures = new List<string>();

        if (options.MaxRows is < 1 or > MaxRowsLimit)
        {
            failures.Add($"Reports:MaxRows must be between 1 and {MaxRowsLimit}.");
        }

        if (options.MaxUnfinishedPerUser is < 1 or > MaxUnfinishedPerUserLimit)
        {
            failures.Add($"Reports:MaxUnfinishedPerUser must be between 1 and {MaxUnfinishedPerUserLimit}.");
        }

        if (options.Retention < MinRetention || options.Retention > MaxRetention)
        {
            failures.Add($"Reports:Retention must be between {MinRetention:c} and {MaxRetention:c}.");
        }

        if (options.CleanupInterval < MinCleanupInterval || options.CleanupInterval > MaxCleanupInterval)
        {
            failures.Add($"Reports:CleanupInterval must be between {MinCleanupInterval:c} and {MaxCleanupInterval:c}.");
        }

        if (options.CleanupBatchSize is < 1 or > CleanupBatchSizeLimit)
        {
            failures.Add($"Reports:CleanupBatchSize must be between 1 and {CleanupBatchSizeLimit}.");
        }

        if (options.DownloadLinkLifetime < MinDownloadLinkLifetime || options.DownloadLinkLifetime > MaxDownloadLinkLifetime)
        {
            failures.Add($"Reports:DownloadLinkLifetime must be between {MinDownloadLinkLifetime:c} and {MaxDownloadLinkLifetime:c}.");
        }

        if (options.MaxRangeDays is < 1 or > MaxRangeDaysLimit)
        {
            failures.Add($"Reports:MaxRangeDays must be between 1 and {MaxRangeDaysLimit}.");
        }

        return failures;
    }
}

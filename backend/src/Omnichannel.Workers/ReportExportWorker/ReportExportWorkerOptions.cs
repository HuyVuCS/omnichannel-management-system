using Microsoft.Extensions.Options;

namespace Omnichannel.Workers.ReportExportWorker;

/// <summary>Bound from the <c>ReportExportWorker</c> configuration section.</summary>
public sealed class ReportExportWorkerOptions
{
    public const string SectionName = "ReportExportWorker";

    /// <summary>
    /// Exports built in parallel by this instance (consumer dispatch concurrency and prefetch). Default 1: a workbook is
    /// built in memory, so the memory of the process grows with every export that runs at the same time. More
    /// throughput comes from more instances.
    /// </summary>
    public ushort Concurrency { get; set; } = 1;

    /// <summary>Attempts of a task (from the database) before a temporary failure becomes final (task Failed, DLQ).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Backoff for (re)starting the consumer while RabbitMQ is unavailable.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class ReportExportWorkerOptionsValidator : IValidateOptions<ReportExportWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, ReportExportWorkerOptions options)
    {
        var failures = new List<string>();

        if (options.Concurrency is < 1 or > 8)
        {
            failures.Add("ReportExportWorker:Concurrency must be between 1 and 8.");
        }

        if (options.MaxAttempts is < 1 or > 20)
        {
            failures.Add("ReportExportWorker:MaxAttempts must be between 1 and 20.");
        }

        if (options.InitialBackoff <= TimeSpan.Zero || options.MaxBackoff < options.InitialBackoff)
        {
            failures.Add("ReportExportWorker:InitialBackoff must be positive and not larger than MaxBackoff.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

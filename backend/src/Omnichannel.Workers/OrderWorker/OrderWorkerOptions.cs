using Microsoft.Extensions.Options;

namespace Omnichannel.Workers.OrderWorker;

/// <summary>Bound from the <c>OrderWorker</c> configuration section.</summary>
public sealed class OrderWorkerOptions
{
    public const string SectionName = "OrderWorker";

    /// <summary>Messages handled in parallel by this instance (consumer dispatch concurrency and prefetch).</summary>
    public ushort Concurrency { get; set; } = 4;

    /// <summary>Attempts of a task (from the database) before a temporary failure becomes final (task Failed, DLQ).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Backoff for (re)starting the consumer while RabbitMQ is unavailable.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class OrderWorkerOptionsValidator : IValidateOptions<OrderWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, OrderWorkerOptions options)
    {
        var failures = new List<string>();

        if (options.Concurrency is < 1 or > 64)
        {
            failures.Add("OrderWorker:Concurrency must be between 1 and 64.");
        }

        if (options.MaxAttempts is < 1 or > 20)
        {
            failures.Add("OrderWorker:MaxAttempts must be between 1 and 20.");
        }

        if (options.InitialBackoff <= TimeSpan.Zero || options.MaxBackoff < options.InitialBackoff)
        {
            failures.Add("OrderWorker:InitialBackoff must be positive and not larger than MaxBackoff.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

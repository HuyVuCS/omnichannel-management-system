namespace Omnichannel.Application.Processing;

/// <summary>
/// The stale-task sweep of the Workers host. Bound from the <c>StaleTasks</c> configuration section.
/// <para>
/// A task whose message is lost (the broker's data lost, an outbox row that ended <c>Failed</c>, a message removed by
/// hand) stays <c>Pending</c>, <c>InProgress</c> or <c>RetryScheduled</c> for good: nothing will ever touch it, an
/// operator cannot retry it and reconciliation waits for it. The sweep fails such a task, so it becomes visible (alert)
/// and retryable.
/// </para>
/// </summary>
public sealed class StaleTaskOptions
{
    public const string SectionName = "StaleTasks";

    /// <summary>False = this host never fails a task for standing still.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pause between two runs, and the wait before the first one after start-up.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A task that is not finished and was not changed for this long is stale. Must leave room for every wait a healthy
    /// task goes through untouched (<see cref="StaleTaskOptionsRules.MarginFactor"/>).
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Most tasks one run fails; the rest waits for the next run.</summary>
    public int MaxPerRun { get; set; } = 200;
}

/// <summary>Returns one message per setting that is out of range; none when the options are valid.</summary>
public static class StaleTaskOptionsRules
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>Not a business limit: a longer pause is a typing error.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public const int MaxPerRunLimit = 1000;

    /// <summary>
    /// <see cref="StaleTaskOptions.StaleAfter"/> must be at least this many times the longest wait of a healthy task: a
    /// task that is merely waiting for its next retry or for the Outbox Publisher must never look stale, with room for
    /// a queue that is busy and a clock that is off.
    /// </summary>
    public const int MarginFactor = 2;

    /// <param name="longestRetryDelay">The longest time a message waits in a retry tier before it is delivered again.</param>
    /// <param name="longestPublisherWait">The longest time the Outbox Publisher keeps trying to publish one message.</param>
    public static IReadOnlyList<string> Validate(StaleTaskOptions options, TimeSpan longestRetryDelay, TimeSpan longestPublisherWait)
    {
        var failures = new List<string>();

        if (options.Interval < MinInterval || options.Interval > MaxInterval)
        {
            failures.Add($"StaleTasks:Interval must be between {MinInterval:c} and {MaxInterval:c}.");
        }

        if (options.MaxPerRun is < 1 or > MaxPerRunLimit)
        {
            failures.Add($"StaleTasks:MaxPerRun must be between 1 and {MaxPerRunLimit}.");
        }

        // Checked for a sweep that is switched off as well: switching it on later must not be the moment it turns out wrong.
        var longestWait = longestRetryDelay > longestPublisherWait ? longestRetryDelay : longestPublisherWait;
        var least = longestWait * MarginFactor;
        if (options.StaleAfter < least || options.StaleAfter <= TimeSpan.Zero)
        {
            failures.Add(
                $"StaleTasks:StaleAfter ({options.StaleAfter:c}) must be at least {least:c}: {MarginFactor} x the longest time a healthy task waits " +
                $"untouched - the longest retry delay (RabbitMq:RetryDelays, {longestRetryDelay:c}) or the time the Outbox Publisher keeps " +
                $"trying one message (OutboxPublisher:MaxAttempts / InitialBackoff / MaxBackoff, {longestPublisherWait:c}).");
        }

        return failures;
    }
}

/// <summary>How a sweep run is found again: its event and log lines carry this correlation id.</summary>
public static class StaleTaskIds
{
    public const string CorrelationIdPrefix = "stale-tasks-";

    public static string CorrelationIdOf(Guid runId) => $"{CorrelationIdPrefix}{runId:N}";
}

/// <param name="Ran">False when another instance was running a sweep: nothing was looked at.</param>
/// <param name="Stale">Tasks the run found stale (at most <see cref="StaleTaskOptions.MaxPerRun"/>).</param>
/// <param name="Failed">Ids of the tasks it failed, oldest first.</param>
/// <param name="Skipped">Stale when found, but touched by a worker before the run reached them: left alone.</param>
/// <param name="Errors">Tasks whose failure could not be written (logged); the next run finds them again.</param>
/// <param name="MoreExpected">True when the run stopped at <see cref="StaleTaskOptions.MaxPerRun"/>.</param>
public sealed record StaleTaskSweepResult(
    Guid RunId,
    bool Ran,
    int Stale,
    IReadOnlyList<Guid> Failed,
    int Skipped,
    int Errors,
    bool MoreExpected)
{
    public static StaleTaskSweepResult NotRun(Guid runId) => new(runId, false, 0, [], 0, 0, false);
}

/// <summary>
/// One sweep (doc §8, review 022): every task that is <c>Pending</c>, <c>InProgress</c> or <c>RetryScheduled</c> and was
/// not changed for <see cref="StaleTaskOptions.StaleAfter"/> is failed the way a worker fails a task that cannot be
/// retried - attempt, <c>processing.failed</c> event, alert, realtime hint - with the error code
/// <see cref="AttemptErrorCodes.StaleNoProgress"/>. One run at a time across all Workers instances.
/// </summary>
public interface IStaleTaskSweep
{
    /// <param name="runId">Id of the run; its correlation id is <see cref="StaleTaskIds.CorrelationIdOf"/>.</param>
    /// <param name="workerName">Worker name of the attempts and events the run writes.</param>
    Task<StaleTaskSweepResult> RunAsync(Guid runId, string workerName, string workerInstanceId, CancellationToken cancellationToken);
}

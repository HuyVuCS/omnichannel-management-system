using Omnichannel.Domain.Enums;

namespace Omnichannel.Application.Channels;

/// <summary>Bound from the <c>Reconciliation</c> configuration section (Workers host).</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>False = this host never runs a reconciliation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pause between two runs. The first run starts after this time, but no later than a minute after start-up.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Mappings looked at per step, and so the most products one read of a channel asks for.</summary>
    public int BatchSize { get; set; } = ChannelStockQuery.MaxItems;

    /// <summary>
    /// Most stock-sync tasks one run creates. Drift beyond it is counted and left for the next run, so a channel that
    /// lost everything cannot flood the queues at once.
    /// </summary>
    public int MaxTasksPerRun { get; set; } = 500;
}

/// <summary>Ranges only. Returns one message per setting that is out of range; none when the options are valid.</summary>
public static class ReconciliationOptionsRules
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <summary>Not a business limit: a longer pause is a typing error, and a timer cannot wait much longer than a month.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public static IReadOnlyList<string> Validate(ReconciliationOptions options)
    {
        var failures = new List<string>();

        if (options.Interval < MinInterval || options.Interval > MaxInterval)
        {
            failures.Add($"Reconciliation:Interval must be between {MinInterval:c} and {MaxInterval:c}.");
        }

        if (options.BatchSize is < 1 or > ChannelStockQuery.MaxItems)
        {
            failures.Add($"Reconciliation:BatchSize must be between 1 and {ChannelStockQuery.MaxItems}.");
        }

        if (options.MaxTasksPerRun < 1)
        {
            failures.Add("Reconciliation:MaxTasksPerRun must be at least 1.");
        }

        return failures;
    }
}

/// <summary>SystemEventLog event types written by reconciliation.</summary>
public static class ReconciliationEvents
{
    /// <summary>
    /// A run that created a stock-sync task or could not read a channel (Warning, the run's correlation id); a run that
    /// finds everything in sync writes no event. Payload: <c>{runId, checked, inSync, drifted, tasksCreated, deferred,
    /// skippedUnfinished, skippedRejected, unreadable: [{channel, errorCode, pairs}]}</c>.
    /// </summary>
    public const string Completed = "reconciliation.completed";
}

/// <summary>How a reconciliation run is found again: every task, message and event of the run carries its correlation id.</summary>
public static class ReconciliationIds
{
    public const string CorrelationIdPrefix = "reconciliation-";

    /// <summary><c>reconciliation-{runId:N}</c>: a valid correlation id of the API (letters, digits, <c>-</c>; 47 characters).</summary>
    public static string CorrelationIdOf(Guid runId) => $"{CorrelationIdPrefix}{runId:N}";

    /// <summary>
    /// <c>reconcile:{runId:N}:{channelId:N}:{productId:N}</c>: a run creates at most one task per channel and product
    /// (<c>UX_ProcessingTasks_IdempotencyKey</c>).
    /// </summary>
    public static string IdempotencyKeyOf(Guid runId, Guid channelId, Guid productId) =>
        $"reconcile:{runId:N}:{channelId:N}:{productId:N}";
}

/// <summary>Why a mapped product is not compared in a run.</summary>
public enum ReconciliationSkip
{
    /// <summary>The product is compared.</summary>
    None,

    /// <summary>A stock-sync task of the pair is waiting or running: it pushes the current stock anyway.</summary>
    UnfinishedSync,

    /// <summary>
    /// The pair's most recently finished stock-sync task failed because the channel rejected the update: a new task
    /// would fail the same way in every run. The pair's alert is open; changing the mapping or a manual retry is the fix.
    /// </summary>
    RejectedByChannel
}

/// <summary>The decisions of a reconciliation run that need no database: what is skipped, and what counts as drift.</summary>
public static class StockReconciliationRules
{
    /// <param name="hasUnfinishedSync">A stock-sync task of the pair is Pending, InProgress or RetryScheduled.</param>
    /// <param name="lastFinishedStatus">Status of the pair's most recently finished stock-sync task; null when none finished yet.</param>
    /// <param name="lastErrorCode">Error code of that task's last attempt.</param>
    public static ReconciliationSkip SkipOf(bool hasUnfinishedSync, ProcessingTaskStatus? lastFinishedStatus, string? lastErrorCode)
    {
        if (hasUnfinishedSync)
        {
            return ReconciliationSkip.UnfinishedSync;
        }

        // Only a rejection. A task that failed for good with a transient code (open circuit, timeout, channel down, rate
        // limit, database) is exactly the long outage reconciliation exists to heal.
        return lastFinishedStatus == ProcessingTaskStatus.Failed
            && string.Equals(lastErrorCode, ChannelApiErrorCodes.Rejected, StringComparison.Ordinal)
                ? ReconciliationSkip.RejectedByChannel
                : ReconciliationSkip.None;
    }

    /// <summary>
    /// The channel shows something else than the central available stock, or nothing at all (<paramref name="channelStock"/>
    /// null: it never stored a value).
    /// </summary>
    public static bool IsDrift(int? channelStock, int centralStock) => channelStock != centralStock;

    /// <summary>How many of <paramref name="drifted"/> pairs still get a task when <paramref name="tasksCreated"/> exist already.</summary>
    public static int TasksAllowed(int drifted, int tasksCreated, int maxTasksPerRun) =>
        Math.Clamp(maxTasksPerRun - tasksCreated, 0, drifted);
}

/// <summary>A channel a run could not read.</summary>
/// <param name="Channel">The channel's name.</param>
/// <param name="ErrorCode">One of <see cref="ChannelApiErrorCodes"/>: why the read failed.</param>
/// <param name="Pairs">Mapped products of the channel the run did not compare because of it.</param>
public sealed record UnreadableChannel(string Channel, string ErrorCode, int Pairs);

/// <summary>What one reconciliation run did.</summary>
/// <param name="Ran">False = another instance holds the run lock; nothing was looked at.</param>
/// <param name="Checked">Mapped products whose channel value was read (<paramref name="InSync"/> + <paramref name="Drifted"/>).</param>
/// <param name="Drifted">Of those, the ones whose channel value differs (<paramref name="TasksCreated"/> + <paramref name="Deferred"/>).</param>
/// <param name="Deferred">Drifted products left for the next run because of <see cref="ReconciliationOptions.MaxTasksPerRun"/>.</param>
public sealed record ReconciliationRunResult(
    Guid RunId,
    bool Ran,
    int Checked,
    int InSync,
    int Drifted,
    int TasksCreated,
    int Deferred,
    int SkippedUnfinished,
    int SkippedRejected,
    IReadOnlyList<UnreadableChannel> Unreadable)
{
    public static ReconciliationRunResult NotRun(Guid runId) => new(runId, false, 0, 0, 0, 0, 0, 0, 0, []);

    /// <summary>The run has something an operator should see: it started a sync or could not look at a channel.</summary>
    public bool IsNoteworthy => TasksCreated > 0 || Unreadable.Count > 0;
}

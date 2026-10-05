using Omnichannel.Api.Middleware;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Channels;
using Omnichannel.Workers.Processing;
using Omnichannel.Workers.ReconciliationWorker;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 022: the decisions of a reconciliation run that need no database, its ids and its options.</summary>
public sealed class StockReconciliationRulesTests
{
    // ---- drift ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(7, 7, false)]
    [InlineData(0, 0, false)]
    [InlineData(6, 7, true)]
    [InlineData(8, 7, true)]
    [InlineData(0, 7, true)]
    [InlineData(7, 0, true)]
    public void A_channel_value_is_drift_exactly_when_it_differs_from_the_central_stock(int channelStock, int centralStock, bool drift)
    {
        Assert.Equal(drift, StockReconciliationRules.IsDrift(channelStock, centralStock));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void A_channel_that_holds_no_value_is_drift_whatever_the_central_stock_is(int centralStock)
    {
        // "No value" is not zero: a channel that never stored anything shows nothing, also for a product without stock.
        Assert.True(StockReconciliationRules.IsDrift(null, centralStock));
    }

    // ---- skip rules -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData(ProcessingTaskStatus.Succeeded, null)]
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected)]
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.CircuitOpen)]
    public void A_pair_with_an_unfinished_sync_is_skipped_whatever_finished_before(ProcessingTaskStatus? lastFinished, string? lastErrorCode)
    {
        Assert.Equal(ReconciliationSkip.UnfinishedSync, StockReconciliationRules.SkipOf(true, lastFinished, lastErrorCode));
    }

    [Fact]
    public void A_pair_whose_last_finished_sync_was_rejected_by_the_channel_is_skipped()
    {
        Assert.Equal(
            ReconciliationSkip.RejectedByChannel,
            StockReconciliationRules.SkipOf(false, ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected));
    }

    [Theory]
    // The recovery path of long outages: a final failure with a transient code is compared again.
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.CircuitOpen)]
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Timeout)]
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Unavailable)]
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.RateLimited)]
    [InlineData(ProcessingTaskStatus.Failed, "database_unavailable")]
    [InlineData(ProcessingTaskStatus.Failed, null)]
    [InlineData(ProcessingTaskStatus.Failed, "CHANNEL_REJECTED")]
    // A success after a rejection (the mapping was corrected): compared.
    [InlineData(ProcessingTaskStatus.Succeeded, null)]
    [InlineData(ProcessingTaskStatus.Succeeded, ChannelApiErrorCodes.Rejected)]
    // No stock-sync task has finished yet.
    [InlineData(null, null)]
    public void Every_other_pair_is_compared(ProcessingTaskStatus? lastFinished, string? lastErrorCode)
    {
        Assert.Equal(ReconciliationSkip.None, StockReconciliationRules.SkipOf(false, lastFinished, lastErrorCode));
    }

    // ---- failed syncs that are repeated (task 023 A2) ---------------------------------------------------------

    [Theory]
    [InlineData(ChannelApiErrorCodes.CircuitOpen)]
    [InlineData(ChannelApiErrorCodes.Timeout)]
    [InlineData(ChannelApiErrorCodes.Unavailable)]
    [InlineData(ChannelApiErrorCodes.RateLimited)]
    [InlineData(ProcessingErrorClassifier.DatabaseUnavailable)]
    [InlineData(ProcessingErrorClassifier.Unexpected)]
    [InlineData(null)]
    public void A_final_failure_with_any_code_but_a_rejection_is_repeated(string? lastErrorCode)
    {
        Assert.True(StockReconciliationRules.RepeatsFailedSync(ProcessingTaskStatus.Failed, lastErrorCode));
    }

    [Theory]
    // A rejection would be rejected again.
    [InlineData(ProcessingTaskStatus.Failed, ChannelApiErrorCodes.Rejected, false)]
    // Nothing failed, whatever an earlier attempt of the task said.
    [InlineData(ProcessingTaskStatus.Succeeded, null, false)]
    [InlineData(ProcessingTaskStatus.Succeeded, ChannelApiErrorCodes.CircuitOpen, false)]
    [InlineData(null, null, false)]
    [InlineData(null, ChannelApiErrorCodes.CircuitOpen, false)]
    public void Nothing_else_is_repeated(ProcessingTaskStatus? lastFinished, string? lastErrorCode, bool repeated)
    {
        Assert.Equal(repeated, StockReconciliationRules.RepeatsFailedSync(lastFinished, lastErrorCode));
    }

    [Fact]
    public void A_pair_is_never_both_skipped_as_rejected_and_repeated()
    {
        foreach (var status in new ProcessingTaskStatus?[] { null, ProcessingTaskStatus.Succeeded, ProcessingTaskStatus.Failed })
        {
            foreach (var code in new[] { null, ChannelApiErrorCodes.Rejected, ChannelApiErrorCodes.CircuitOpen })
            {
                var skipped = StockReconciliationRules.SkipOf(false, status, code) == ReconciliationSkip.RejectedByChannel;
                Assert.False(skipped && StockReconciliationRules.RepeatsFailedSync(status, code));
            }
        }
    }

    [Fact]
    public void An_unexpected_read_failure_has_the_code_of_the_classifier()
    {
        // One constant (task 023 A1): what a run reports for a read that is no channel answer is the workers' code for
        // unknown failures.
        Assert.Equal("unexpected", AttemptErrorCodes.Unexpected);
        Assert.Equal(AttemptErrorCodes.Unexpected, ProcessingErrorClassifier.Unexpected);
        Assert.Equal(AttemptErrorCodes.Unexpected, ProcessingErrorClassifier.Classify(new InvalidOperationException("x")).ErrorCode);
    }

    // ---- cap --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 0, 500, 10)] // room for all
    [InlineData(10, 495, 500, 5)] // room for some: the rest is deferred
    [InlineData(10, 490, 500, 10)] // exactly fits
    [InlineData(10, 500, 500, 0)] // the cap is reached
    [InlineData(0, 3, 500, 0)]
    [InlineData(4, 0, 1, 1)]
    public void A_run_creates_tasks_only_up_to_its_cap(int wanted, int tasksCreated, int maxTasksPerRun, int allowed)
    {
        Assert.Equal(allowed, StockReconciliationRules.TasksAllowed(wanted, tasksCreated, maxTasksPerRun));
    }

    // ---- ids --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_correlation_id_of_a_run_is_valid_for_the_api_and_fits_its_columns()
    {
        var runId = Guid.Parse("0199b3c2-7d1e-7c4a-9f00-aabbccddeeff");

        var correlationId = ReconciliationIds.CorrelationIdOf(runId);

        Assert.Equal("reconciliation-0199b3c27d1e7c4a9f00aabbccddeeff", correlationId);
        Assert.StartsWith(ReconciliationIds.CorrelationIdPrefix, correlationId);
        // What the Sync & Processing and Logs pages send as a filter is accepted by the API as it is.
        Assert.True(CorrelationIdMiddleware.IsValid(correlationId));
        Assert.True(correlationId.Length <= CorrelationIdMiddleware.MaxLength); // also the column width of tasks, messages, events
        Assert.Equal(47, correlationId.Length);
    }

    [Fact]
    public void The_idempotency_key_names_run_channel_and_product_and_fits_its_column()
    {
        var (runId, channelId, productId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var key = ReconciliationIds.IdempotencyKeyOf(runId, channelId, productId);

        Assert.Equal($"reconcile:{runId:N}:{channelId:N}:{productId:N}", key);
        Assert.True(key.Length <= ProcessingTask.MaxIdempotencyKeyLength);
        Assert.Equal(108, key.Length);
        // The task accepts it; another pair or another run is another key.
        Assert.Equal(key, ProcessingTask.ForStockSync(channelId, productId, ReconciliationIds.CorrelationIdOf(runId), null, key).IdempotencyKey);
        Assert.NotEqual(key, ReconciliationIds.IdempotencyKeyOf(runId, productId, channelId));
        Assert.NotEqual(key, ReconciliationIds.IdempotencyKeyOf(Guid.NewGuid(), channelId, productId));
        // Never the key of a stock change on the same channel.
        Assert.False(key.StartsWith("stocksync:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lock_key_and_the_worker_name_are_fixed()
    {
        // Every instance of every version must ask for the same lock.
        Assert.Equal(0x4F4D4E495F52434E, StockReconciliation.AdvisoryLockKey);
        Assert.Equal("ReconciliationWorker", ReconciliationService.WorkerName);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconciliationService.MaxInitialDelay);
    }

    // ---- options ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_valid_and_as_documented()
    {
        var options = new ReconciliationOptions();

        Assert.Equal((true, TimeSpan.FromMinutes(15), 100, 500), (options.Enabled, options.Interval, options.BatchSize, options.MaxTasksPerRun));
        Assert.Empty(ReconciliationOptionsRules.Validate(options));
        Assert.True(new ReconciliationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:10", 1, 1)]
    [InlineData("1.00:00:00", 100, int.MaxValue)]
    [InlineData("00:01:00", 50, 500)]
    public void Settings_at_the_edges_of_their_ranges_are_valid(string interval, int batchSize, int maxTasksPerRun)
    {
        var options = new ReconciliationOptions { Interval = TimeSpan.Parse(interval), BatchSize = batchSize, MaxTasksPerRun = maxTasksPerRun };

        Assert.Empty(ReconciliationOptionsRules.Validate(options));
    }

    [Theory]
    [InlineData("00:00:09.999", 100, 500, "Reconciliation:Interval")]
    [InlineData("00:00:00", 100, 500, "Reconciliation:Interval")]
    [InlineData("-00:01:00", 100, 500, "Reconciliation:Interval")]
    [InlineData("1.00:00:01", 100, 500, "Reconciliation:Interval")]
    [InlineData("00:15:00", 0, 500, "Reconciliation:BatchSize")]
    [InlineData("00:15:00", 101, 500, "Reconciliation:BatchSize")]
    [InlineData("00:15:00", 100, 0, "Reconciliation:MaxTasksPerRun")]
    [InlineData("00:15:00", 100, -1, "Reconciliation:MaxTasksPerRun")]
    public void A_setting_out_of_range_is_named(string interval, int batchSize, int maxTasksPerRun, string setting)
    {
        var options = new ReconciliationOptions { Interval = TimeSpan.Parse(interval), BatchSize = batchSize, MaxTasksPerRun = maxTasksPerRun };

        Assert.StartsWith(setting, Assert.Single(ReconciliationOptionsRules.Validate(options)));
        var result = new ReconciliationOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
    }

    [Fact]
    public void Every_setting_out_of_range_is_reported_and_a_switched_off_host_is_still_validated()
    {
        var options = new ReconciliationOptions { Enabled = false, Interval = TimeSpan.Zero, BatchSize = 0, MaxTasksPerRun = 0 };

        Assert.Equal(3, ReconciliationOptionsRules.Validate(options).Count);
    }

    [Fact]
    public void A_batch_never_asks_a_channel_for_more_than_one_read_allows()
    {
        Assert.Equal(ChannelStockQuery.MaxItems, new ReconciliationOptions().BatchSize);
        Assert.NotEmpty(ReconciliationOptionsRules.Validate(new ReconciliationOptions { BatchSize = ChannelStockQuery.MaxItems + 1 }));
    }

    // ---- result -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_is_noteworthy_when_it_created_a_task_or_could_not_read_a_channel()
    {
        var clean = new ReconciliationRunResult(Guid.NewGuid(), true, 10, 10, 0, 0, 0, 0, 2, 1, []);

        Assert.False(clean.IsNoteworthy);
        Assert.True((clean with { Drifted = 1, TasksCreated = 1 }).IsNoteworthy);
        // A repeated failed sync is a created task (nothing drifted).
        Assert.True((clean with { TasksCreated = 1, FailedRetried = 1 }).IsNoteworthy);
        Assert.True((clean with { Unreadable = [new UnreadableChannel("POS", ChannelApiErrorCodes.Unavailable, 4)] }).IsNoteworthy);
        // Skipped pairs alone are nothing new: their own task or alert already says so.
        Assert.False((clean with { SkippedUnfinished = 50, SkippedRejected = 50 }).IsNoteworthy);

        var notRun = ReconciliationRunResult.NotRun(clean.RunId);
        Assert.Equal((clean.RunId, false, false), (notRun.RunId, notRun.Ran, notRun.IsNoteworthy));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Processing;

/// <summary>
/// <see cref="IStaleTaskSweep"/>. The candidates are read without a lock; each one is then failed in a transaction of
/// its own that locks the task row and checks status and age again (<see cref="ProcessingAttemptStore.FailStaleAsync"/>),
/// so a task a worker touched in between is never failed. A task whose failure cannot be written does not stop the run.
/// One run at a time across all Workers instances: a PostgreSQL advisory lock on a connection of its own.
/// </summary>
internal sealed class StaleTaskSweep(
    OmnichannelDbContext dbContext,
    ProcessingAttemptStore attempts,
    IOptions<DatabaseOptions> database,
    IOptions<StaleTaskOptions> options,
    TimeProvider timeProvider,
    ILogger<StaleTaskSweep> logger) : IStaleTaskSweep
{
    /// <summary>Key of the advisory lock that makes a run exclusive ("OMNI_STL" as ASCII bytes).</summary>
    public const long AdvisoryLockKey = 0x4F4D4E495F53544C;

    public async Task<StaleTaskSweepResult> RunAsync(Guid runId, string workerName, string workerInstanceId, CancellationToken cancellationToken)
    {
        // Not pooled: the lock belongs to the session, and closing this connection ends the session for certain.
        var lockConnectionString = new NpgsqlConnectionStringBuilder(database.Value.ConnectionString) { Pooling = false }.ConnectionString;
        await using var lockConnection = new NpgsqlConnection(lockConnectionString);
        await lockConnection.OpenAsync(cancellationToken);

        if (!await LockAsync(lockConnection, "SELECT pg_try_advisory_lock($1)", cancellationToken))
        {
            return StaleTaskSweepResult.NotRun(runId);
        }

        try
        {
            return await RunExclusiveAsync(runId, workerName, workerInstanceId, cancellationToken);
        }
        finally
        {
            try
            {
                await LockAsync(lockConnection, "SELECT pg_advisory_unlock($1)", CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // The connection is gone, and the lock with it.
            }
        }
    }

    private static async Task<bool> LockAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { new() { Value = AdvisoryLockKey } } };
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<StaleTaskSweepResult> RunExclusiveAsync(
        Guid runId,
        string workerName,
        string workerInstanceId,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var unchangedSince = now - settings.StaleAfter;

        // The statuses are written out (not a parameter), so the statement matches the partial index of unfinished tasks.
        var stale = await dbContext.ProcessingTasks
            .AsNoTracking()
            .Where(t => (t.Status == ProcessingTaskStatus.Pending
                    || t.Status == ProcessingTaskStatus.InProgress
                    || t.Status == ProcessingTaskStatus.RetryScheduled)
                && (t.UpdatedAt ?? t.CreatedAt) < unchangedSince)
            .OrderBy(t => t.UpdatedAt ?? t.CreatedAt)
            .ThenBy(t => t.TaskId)
            .Take(settings.MaxPerRun)
            .Select(t => t.TaskId)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return new StaleTaskSweepResult(runId, true, 0, [], 0, 0, false);
        }

        var message =
            $"No progress for more than {settings.StaleAfter:c}: the message of the task was lost or never delivered. Retry the task to send a new one.";
        var failed = new List<Guid>();
        var (skipped, errors) = (0, 0);
        foreach (var taskId in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await attempts.FailStaleAsync(taskId, unchangedSince, message, workerName, workerInstanceId, cancellationToken) is null)
                {
                    skipped++;
                }
                else
                {
                    failed.Add(taskId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors++;
                logger.LogError(exception, "Stale task {TaskId} could not be failed; the next run finds it again", taskId);
            }
        }

        if (failed.Count > 0)
        {
            dbContext.ChangeTracker.Clear();
            dbContext.SystemEventLogs.Add(new SystemEventLog(
                SystemEventLevel.Warning,
                ProcessingEvents.StaleTasksFailed,
                StaleTaskIds.CorrelationIdOf(runId),
                taskId: null,
                orderId: null,
                workerName,
                MessageJson.Serialize(new
                {
                    runId,
                    staleAfterSeconds = (long)settings.StaleAfter.TotalSeconds,
                    failed = failed.Count,
                    taskIds = failed.Take(ProcessingEvents.StaleTasksFailedMaxListed),
                    notListed = Math.Max(0, failed.Count - ProcessingEvents.StaleTasksFailedMaxListed)
                }),
                timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new StaleTaskSweepResult(runId, true, stale.Count, failed, skipped, errors, stale.Count == settings.MaxPerRun);
    }
}

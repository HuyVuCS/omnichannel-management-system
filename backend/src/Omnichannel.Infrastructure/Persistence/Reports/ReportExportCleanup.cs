using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Reports;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Reports;

/// <summary>What one round of the retention sweep did.</summary>
/// <param name="Expired">Exports whose file is gone now and that are marked deleted.</param>
/// <param name="Failed">Exports whose file could not be deleted; they are tried again in the next round.</param>
public sealed record ReportExportCleanupResult(int Expired, int Failed);

/// <summary>
/// The retention sweep of exports (they hold personal data and accumulate with use): the file of every export that is
/// past its <c>ExpiresAt</c> and whose task is terminal is deleted from the object storage, then the export is marked
/// deleted - which makes it <c>Expired</c> for its requester.
/// <para>
/// The storage is called outside any transaction, and only what was deleted is marked. An export without a file (it
/// failed before or after the upload) is handled the same way: its key depends on the task id alone, and deleting a
/// key without an object succeeds. An export whose task is still waiting or running is left alone - the worker fails it
/// as expired, and a later round takes it. Several instances may sweep at once: deleting is idempotent, and the first
/// mark wins.
/// </para>
/// </summary>
public sealed class ReportExportCleanup(
    OmnichannelDbContext dbContext,
    IObjectStorage storage,
    ICurrentUser currentUser,
    IOptions<ReportsOptions> options,
    TimeProvider timeProvider,
    ILogger<ReportExportCleanup> logger)
{
    private const string SystemActor = "system";

    /// <summary>
    /// One round: batches of <c>Reports:CleanupBatchSize</c> until no expired export is left, or until a batch could not
    /// delete every file (the storage is in trouble: the rest waits for the next round instead of being tried again and
    /// again).
    /// </summary>
    public async Task<ReportExportCleanupResult> RunAsync(CancellationToken cancellationToken)
    {
        var batchSize = options.Value.CleanupBatchSize;
        var expired = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var due = await DueAsync(batchSize, cancellationToken);
            var deleted = new List<Guid>(due.Count);
            foreach (var taskId in due)
            {
                try
                {
                    await storage.DeleteAsync(ReportExport.ObjectKeyOf(taskId), cancellationToken);
                    deleted.Add(taskId);
                }
                catch (ObjectStorageException)
                {
                    // What failed is in the storage's own warning; here only which export keeps its file for now.
                    logger.LogWarning("The file of expired export {TaskId} could not be deleted; it is tried again in the next round", taskId);
                }
            }

            expired += await MarkDeletedAsync(deleted, cancellationToken);

            var failed = due.Count - deleted.Count;
            if (failed > 0 || due.Count < batchSize)
            {
                return new ReportExportCleanupResult(expired, failed);
            }
        }
    }

    /// <summary>The next exports to expire: past their time, file not deleted yet, task Succeeded or Failed. Oldest first.</summary>
    public async Task<IReadOnlyList<Guid>> DueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.ReportExports
            .AsNoTracking()
            .Where(e => e.DeletedAt == null && e.ExpiresAt <= now)
            .Where(e => e.ProcessingTask.Status == ProcessingTaskStatus.Succeeded || e.ProcessingTask.Status == ProcessingTaskStatus.Failed)
            .OrderBy(e => e.ExpiresAt)
            .ThenBy(e => e.TaskId)
            .Select(e => e.TaskId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>One statement; a row another instance marked meanwhile keeps its first deletion time.</summary>
    private async Task<int> MarkDeletedAsync(List<Guid> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        // A set-based update bypasses the auditing interceptor, hence UpdatedAt/UpdatedBy.
        var now = timeProvider.GetUtcNow();
        var actor = currentUser.UserId ?? SystemActor;
        return await dbContext.ReportExports
            .Where(e => taskIds.Contains(e.TaskId) && e.DeletedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.DeletedAt, now)
                    .SetProperty(e => e.UpdatedAt, now)
                    .SetProperty(e => e.UpdatedBy, actor),
                cancellationToken);
    }
}

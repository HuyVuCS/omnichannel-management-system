using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Infrastructure.Persistence.Products;

/// <summary>
/// <see cref="IProductImageCleanup"/>. The two image prefixes are listed in pages; an object is deleted only when
/// <list type="number">
/// <item>its key is one the application generates (anything else under the prefix is left alone and logged),</item>
/// <item>the storage wrote it more than <see cref="ImageCleanupOptions.MinAge"/> ago, <b>and</b></item>
/// <item>for a final object: no product row refers to it, asked <b>after</b> the page was listed, in one statement per page.</item>
/// </list>
/// Export files (<c>exports/</c>) are under neither prefix and have their own retention.
/// <para>
/// <b>Why no object a product refers to - now or later - can be deleted.</b> A product's <c>ImageKey</c> only ever
/// becomes a final key in one place: the transaction of the confirmation that created that object
/// (<see cref="ProductImageService.ConfirmAsync"/>). A final key is made new for each confirmation, no upload link
/// leads to it and no client can name it (a confirmation takes staging keys only), so the object is written exactly
/// once, by its confirmation, and its age counts from that copy. The confirmation refers to it within
/// <see cref="ProductImageRules.ConfirmationDeadline"/> after the copy or gives up, and
/// <see cref="ImageCleanupOptions.MinAge"/> is at least four times that. Hence, for an object older than MinAge:
/// </para>
/// <list type="bullet">
/// <item>its confirmation is over - it either referred to the object or never will;</item>
/// <item>if it is referred to when this run asks, it is kept;</item>
/// <item>if it is not, it can only have been unreferenced since (replaced or removed - both delete it themselves, and a
/// second delete changes nothing) or never referenced; nothing can refer to it afterwards. It is an orphan for good.</item>
/// </list>
/// <para>
/// A product that starts to refer to an object between the listing and the question is therefore only possible for a
/// young object, which is not a candidate; the question is asked after the listing all the same, so the answer is
/// never older than the list (tested with an object that becomes referenced in between).
/// A staging object is never referred to. Deleting one that is about to be confirmed (it would have to be older than
/// MinAge) makes that confirmation answer "nothing uploaded"; the client uploads again.
/// </para>
/// One run at a time across all Workers instances: a PostgreSQL advisory lock on a connection of its own. A delete
/// that fails is logged and found again by the next run.
/// </summary>
internal sealed class ProductImageCleanup(
    OmnichannelDbContext dbContext,
    IObjectStorage storage,
    IOptions<DatabaseOptions> database,
    IOptions<ImageCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<ProductImageCleanup> logger) : IProductImageCleanup
{
    /// <summary>Key of the advisory lock that makes a run exclusive ("OMNI_IMG" as ASCII bytes).</summary>
    public const long AdvisoryLockKey = 0x4F4D4E495F494D47;

    /// <summary>Objects per listing request, and keys per reference question.</summary>
    public const int PageSize = 200;

    public async Task<ImageCleanupResult> RunAsync(Guid runId, string workerName, bool dryRun, CancellationToken cancellationToken)
    {
        // Not pooled: the lock belongs to the session, and closing this connection ends the session for certain.
        var lockConnectionString = new NpgsqlConnectionStringBuilder(database.Value.ConnectionString) { Pooling = false }.ConnectionString;
        await using var lockConnection = new NpgsqlConnection(lockConnectionString);
        await lockConnection.OpenAsync(cancellationToken);

        if (!await LockAsync(lockConnection, "SELECT pg_try_advisory_lock($1)", cancellationToken))
        {
            return ImageCleanupResult.NotRun(runId);
        }

        try
        {
            return await RunExclusiveAsync(runId, workerName, dryRun, cancellationToken);
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

    private async Task<ImageCleanupResult> RunExclusiveAsync(Guid runId, string workerName, bool dryRun, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var run = new Run(timeProvider.GetUtcNow() - settings.MinAge, settings.MaxPerRun, dryRun);

        // Uploads first: nothing refers to them, so their age decides alone.
        await SweepAsync(ProductImageKey.StagingPrefix, run, isStaging: true, cancellationToken);
        await SweepAsync(ProductImageKey.FinalPrefix, run, isStaging: false, cancellationToken);

        var deleted = run.DeletedUploads.Count + run.DeletedImages.Count;
        if (deleted > 0 && !dryRun)
        {
            string[] keys = [.. run.DeletedUploads, .. run.DeletedImages];
            dbContext.ChangeTracker.Clear();
            dbContext.SystemEventLogs.Add(new SystemEventLog(
                SystemEventLevel.Information,
                ProductImageEvents.OrphansDeleted,
                ImageCleanupIds.CorrelationIdOf(runId),
                taskId: null,
                orderId: null,
                workerName,
                MessageJson.Serialize(new
                {
                    runId,
                    minAgeSeconds = (long)settings.MinAge.TotalSeconds,
                    uploads = run.DeletedUploads.Count,
                    images = run.DeletedImages.Count,
                    keys = keys.Take(ProductImageEvents.OrphansDeletedMaxListed),
                    notListed = Math.Max(0, keys.Length - ProductImageEvents.OrphansDeletedMaxListed)
                }),
                timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ImageCleanupResult(
            runId, true, run.Listed, run.DeletedUploads, run.DeletedImages, run.Referenced, run.Unknown, run.Errors, run.Full);
    }

    private async Task SweepAsync(string prefix, Run run, bool isStaging, CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Full)
            {
                return;
            }

            var page = await storage.ListAsync(prefix, token, PageSize, cancellationToken);
            token = page.NextToken;
            run.Listed += page.Objects.Count;

            var old = new List<string>();
            foreach (var listed in page.Objects)
            {
                var known = isStaging
                    ? ProductImageKey.TryParseStaging(listed.Key, out _, out _)
                    : ProductImageKey.TryParseFinal(listed.Key, out _, out _);
                if (!known)
                {
                    // Nothing the application wrote: not this sweep's to delete.
                    run.Unknown++;
                    logger.LogWarning("Image clean-up: {ObjectKey} is under an image prefix but is no key of the application; left alone", listed.Key);
                }
                else if (listed.LastModified <= run.WrittenBefore)
                {
                    old.Add(listed.Key);
                }
            }

            if (old.Count == 0)
            {
                continue;
            }

            if (!isStaging)
            {
                // Asked now, after the listing: which of these keys does a product refer to?
                var referenced = await dbContext.Products
                    .AsNoTracking()
                    .Where(p => p.ImageKey != null && old.Contains(p.ImageKey))
                    .Select(p => p.ImageKey!)
                    .ToListAsync(cancellationToken);
                run.Referenced += referenced.Count;
                old = old.Except(referenced, StringComparer.Ordinal).ToList();
            }

            foreach (var key in old)
            {
                if (run.Full)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!run.DryRun)
                    {
                        await storage.DeleteAsync(key, cancellationToken);
                    }

                    (isStaging ? run.DeletedUploads : run.DeletedImages).Add(key);
                }
                catch (ObjectStorageException)
                {
                    // What failed is in the storage's own warning; here only which object stays for now.
                    run.Errors++;
                    logger.LogWarning("Image clean-up: {ObjectKey} could not be deleted; the next run finds it again", key);
                }
            }
        }
        while (token is not null);
    }

    /// <summary>The state of one run.</summary>
    private sealed class Run(DateTimeOffset writtenBefore, int maxDeletes, bool dryRun)
    {
        public DateTimeOffset WrittenBefore { get; } = writtenBefore;

        /// <summary>Look only: what would be deleted is collected, nothing is deleted.</summary>
        public bool DryRun { get; } = dryRun;

        public List<string> DeletedUploads { get; } = [];

        public List<string> DeletedImages { get; } = [];

        public int Listed { get; set; }

        public int Referenced { get; set; }

        public int Unknown { get; set; }

        public int Errors { get; set; }

        /// <summary>The run deleted as many objects as it may (<see cref="ImageCleanupOptions.MaxPerRun"/>).</summary>
        public bool Full => DeletedUploads.Count + DeletedImages.Count >= maxDeletes;
    }
}

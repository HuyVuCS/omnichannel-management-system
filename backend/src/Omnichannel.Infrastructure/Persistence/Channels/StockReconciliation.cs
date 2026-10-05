using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.RabbitMQ;

namespace Omnichannel.Infrastructure.Persistence.Channels;

/// <summary>
/// One reconciliation run (doc §8.5): for every Active mapping of every Active channel that is synced, the central
/// available stock is compared with what the channel shows; where they differ, a new stock-sync task pushes the current
/// stock (the path of every other stock sync: retries, DLQ, alerts, realtime hints).
/// <para>
/// <b>A run never changes the source of truth.</b> It writes <c>ProcessingTasks</c>, <c>OutboxEvents</c> and at most one
/// <c>SystemEventLogs</c> row - never stock, products, mappings, orders, movements or alerts: no business rule says
/// that a channel's value may correct the central one.
/// </para>
/// <para>
/// Nothing is locked and no transaction spans a channel call. The central stock is read before the channel is asked, so
/// a change in between can look like drift: that costs one push of the current value (every stock-sync push sends the
/// value of that moment). Drift that is missed is found by the next run.
/// </para>
/// One run at a time across all Workers instances: a PostgreSQL advisory lock on a connection of its own.
/// </summary>
public sealed class StockReconciliation(
    OmnichannelDbContext dbContext,
    IChannelStockClient channelClient,
    IOptions<DatabaseOptions> database,
    IOptions<ReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<StockReconciliation> logger)
{
    /// <summary>Key of the advisory lock that makes a run exclusive ("OMNI_RCN" as ASCII bytes).</summary>
    public const long AdvisoryLockKey = 0x4F4D4E495F52434E;

    /// <summary>
    /// One page of a channel's Active mappings in <c>ProductId</c> order (keyset: <c>{1}</c> = the last product of the
    /// previous page), each with what the run needs to decide: SKU, external ids, the central available stock, whether a
    /// stock-sync task of the pair is unfinished, and status and last error code of the pair's most recently finished
    /// one ("most recently finished" as in the alert backfill: latest <c>UpdatedAt</c>).
    /// </summary>
    public const string CandidateSql = """
        SELECT m."ProductId", p."Sku", m."ExternalProductId", m."ExternalSku",
               COALESCE(i."AvailableStock", 0) AS "AvailableStock",
               EXISTS (
                   SELECT 1 FROM "ProcessingTasks" t
                   WHERE t."ProductId" = m."ProductId" AND t."ChannelId" = m."ChannelId" AND t."TaskType" = 'StockSync'
                     AND t."Status" IN ('Pending', 'InProgress', 'RetryScheduled')) AS "HasUnfinishedSync",
               latest."Status" AS "LastFinishedStatus",
               (SELECT a."ErrorCode" FROM "ProcessingAttempts" a
                WHERE a."TaskId" = latest."TaskId" ORDER BY a."AttemptNo" DESC LIMIT 1) AS "LastErrorCode"
        FROM "ChannelProducts" m
        JOIN "Products" p ON p."ProductId" = m."ProductId"
        LEFT JOIN "Inventories" i ON i."ProductId" = m."ProductId"
        LEFT JOIN LATERAL (
            SELECT t."TaskId", t."Status"
            FROM "ProcessingTasks" t
            WHERE t."ProductId" = m."ProductId" AND t."ChannelId" = m."ChannelId" AND t."TaskType" = 'StockSync'
              AND t."Status" IN ('Succeeded', 'Failed')
            ORDER BY COALESCE(t."UpdatedAt", t."CreatedAt") DESC, t."TaskId" DESC
            LIMIT 1) latest ON TRUE
        WHERE m."ChannelId" = {0} AND m."Status" = 'Active' AND m."ProductId" > {1}
        ORDER BY m."ProductId"
        LIMIT {2}
        """;

    /// <param name="runId">Id of the run; its correlation id is <see cref="ReconciliationIds.CorrelationIdOf"/>.</param>
    /// <param name="workerName">Written to the run's system event.</param>
    /// <returns>What the run did; <see cref="ReconciliationRunResult.Ran"/> false when another instance is running one.</returns>
    public async Task<ReconciliationRunResult> RunAsync(Guid runId, string workerName, CancellationToken cancellationToken)
    {
        // Not pooled: the lock belongs to the session, and closing this connection ends the session for certain.
        var lockConnectionString = new NpgsqlConnectionStringBuilder(database.Value.ConnectionString) { Pooling = false }.ConnectionString;
        await using var lockConnection = new NpgsqlConnection(lockConnectionString);
        await lockConnection.OpenAsync(cancellationToken);

        if (!await LockAsync(lockConnection, "SELECT pg_try_advisory_lock($1)", cancellationToken))
        {
            return ReconciliationRunResult.NotRun(runId);
        }

        try
        {
            return await RunExclusiveAsync(runId, workerName, cancellationToken);
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

    private async Task<ReconciliationRunResult> RunExclusiveAsync(Guid runId, string workerName, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var correlationId = ReconciliationIds.CorrelationIdOf(runId);
        var (checkedPairs, inSync, drifted, tasksCreated, deferred, skippedUnfinished, skippedRejected) = (0, 0, 0, 0, 0, 0, 0);
        var unreadable = new List<UnreadableChannel>();

        foreach (var channel in await SyncedChannelsAsync(cancellationToken))
        {
            // Set by the first read that fails: the channel is not asked again in this run, the other channels go on.
            string? readError = null;
            var unread = 0;
            var after = Guid.Empty;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await dbContext.Database
                    .SqlQueryRaw<Candidate>(CandidateSql, channel.ChannelId, after, settings.BatchSize)
                    .ToListAsync(cancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                after = page[^1].ProductId;

                var compared = new List<Candidate>(page.Count);
                foreach (var candidate in page)
                {
                    switch (StockReconciliationRules.SkipOf(candidate.HasUnfinishedSync, candidate.LastFinished, candidate.LastErrorCode))
                    {
                        case ReconciliationSkip.UnfinishedSync:
                            skippedUnfinished++;
                            break;
                        case ReconciliationSkip.RejectedByChannel:
                            skippedRejected++;
                            break;
                        default:
                            compared.Add(candidate);
                            break;
                    }
                }

                Dictionary<Guid, int?>? channelStock = null;
                if (compared.Count > 0 && readError is null)
                {
                    try
                    {
                        channelStock = await ReadChannelAsync(channel.Slug, compared, cancellationToken);
                    }
                    catch (ChannelApiException exception)
                    {
                        readError = exception.ErrorCode;
                        logger.LogWarning(
                            "Reconciliation run {RunId}: channel {Channel} could not be read ({ErrorCode}): {ErrorMessage}; its products are left for the next run",
                            runId, channel.Slug, exception.ErrorCode, exception.Message);
                    }
                }

                if (channelStock is null)
                {
                    // Nothing to compare, or the channel does not answer in this run: the pairs are only counted.
                    unread += readError is null ? 0 : compared.Count;
                }
                else
                {
                    var differing = compared
                        .Where(c => StockReconciliationRules.IsDrift(channelStock.GetValueOrDefault(c.ProductId), c.AvailableStock))
                        .ToList();
                    var allowed = StockReconciliationRules.TasksAllowed(differing.Count, tasksCreated, settings.MaxTasksPerRun);
                    if (allowed > 0)
                    {
                        await CreateTasksAsync(runId, correlationId, channel, differing.Take(allowed), cancellationToken);
                    }

                    checkedPairs += compared.Count;
                    inSync += compared.Count - differing.Count;
                    drifted += differing.Count;
                    tasksCreated += allowed;
                    deferred += differing.Count - allowed;
                }

                if (page.Count < settings.BatchSize)
                {
                    break;
                }
            }

            if (readError is not null)
            {
                unreadable.Add(new UnreadableChannel(channel.Name, readError, unread));
            }
        }

        var result = new ReconciliationRunResult(
            runId, Ran: true, checkedPairs, inSync, drifted, tasksCreated, deferred, skippedUnfinished, skippedRejected, unreadable);
        if (result.IsNoteworthy)
        {
            await RecordAsync(result, correlationId, workerName, cancellationToken);
        }

        return result;
    }

    /// <summary>Active channels that have a stock-sync queue, by slug. A channel without one can have mappings but is never synced.</summary>
    private async Task<List<SyncedChannel>> SyncedChannelsAsync(CancellationToken cancellationToken)
    {
        var active = await dbContext.Channels
            .AsNoTracking()
            .Where(c => c.Status == ChannelStatus.Active)
            .OrderBy(c => c.Slug)
            .Select(c => new { c.ChannelId, c.Slug, c.Name })
            .ToListAsync(cancellationToken);

        return active
            .Select(c => (Channel: c, Queue: RabbitMqTopology.StockSyncQueueOf(c.Slug)))
            .Where(c => c.Queue is not null)
            .Select(c => new SyncedChannel(c.Channel.ChannelId, c.Channel.Slug, c.Channel.Name, c.Queue!))
            .ToList();
    }

    /// <summary>What the channel shows per product; a product the channel did not answer for counts as "no value".</summary>
    private async Task<Dictionary<Guid, int?>> ReadChannelAsync(string channelSlug, List<Candidate> candidates, CancellationToken cancellationToken)
    {
        var readings = await channelClient.ReadAvailableStockAsync(
            new ChannelStockQuery(
                channelSlug,
                candidates.Select(c => new ChannelStockItem(c.ProductId, c.Sku, c.ExternalProductId, c.ExternalSku)).ToList()),
            cancellationToken);

        var stock = new Dictionary<Guid, int?>(readings.Count);
        foreach (var reading in readings)
        {
            stock[reading.ProductId] = reading.AvailableStock;
        }

        return stock;
    }

    /// <summary>The tasks of one page and their directed messages: one <c>SaveChanges</c>, so all of them or none.</summary>
    private async Task CreateTasksAsync(
        Guid runId,
        string correlationId,
        SyncedChannel channel,
        IEnumerable<Candidate> pairs,
        CancellationToken cancellationToken)
    {
        foreach (var pair in pairs)
        {
            var (task, message) = StockSyncRequests.NewTask(
                channel.ChannelId,
                pair.ProductId,
                channel.Queue,
                correlationId,
                ReconciliationIds.IdempotencyKeyOf(runId, channel.ChannelId, pair.ProductId));
            dbContext.ProcessingTasks.Add(task);
            dbContext.OutboxEvents.Add(message);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task RecordAsync(ReconciliationRunResult result, string correlationId, string workerName, CancellationToken cancellationToken)
    {
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Warning,
            ReconciliationEvents.Completed,
            correlationId,
            taskId: null,
            orderId: null,
            workerName,
            MessageJson.Serialize(new
            {
                runId = result.RunId,
                @checked = result.Checked,
                inSync = result.InSync,
                drifted = result.Drifted,
                tasksCreated = result.TasksCreated,
                deferred = result.Deferred,
                skippedUnfinished = result.SkippedUnfinished,
                skippedRejected = result.SkippedRejected,
                unreadable = result.Unreadable.Select(u => new { channel = u.Channel, errorCode = u.ErrorCode, pairs = u.Pairs })
            }),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private sealed record SyncedChannel(Guid ChannelId, string Slug, string Name, string Queue);

    /// <summary>A row of <see cref="CandidateSql"/> (public for EF Core's raw-query mapping only).</summary>
    public sealed class Candidate
    {
        public Guid ProductId { get; set; }

        public string Sku { get; set; } = null!;

        public string? ExternalProductId { get; set; }

        public string? ExternalSku { get; set; }

        public int AvailableStock { get; set; }

        public bool HasUnfinishedSync { get; set; }

        /// <summary><c>Succeeded</c> or <c>Failed</c> as stored; null when no stock-sync task of the pair has finished.</summary>
        public string? LastFinishedStatus { get; set; }

        public string? LastErrorCode { get; set; }

        internal ProcessingTaskStatus? LastFinished =>
            LastFinishedStatus is null ? null : Enum.Parse<ProcessingTaskStatus>(LastFinishedStatus);
    }
}

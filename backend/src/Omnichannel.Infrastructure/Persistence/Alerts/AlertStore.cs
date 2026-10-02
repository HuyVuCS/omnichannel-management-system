using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Realtime;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence.Inventories;

namespace Omnichannel.Infrastructure.Persistence.Alerts;

/// <summary>One occurrence of a condition that opens an alert or refreshes the unresolved one with the same key.</summary>
public sealed record AlertOccurrence(
    AlertType Type,
    string DedupKey,
    Guid? ProductId,
    Guid? ChannelId,
    Guid? TaskId,
    Guid? OrderId,
    string? ErrorCode)
{
    /// <summary>An order was stored as InsufficientStock because <paramref name="productId"/> was short.</summary>
    public static AlertOccurrence InsufficientStock(Guid productId, Guid orderId) =>
        new(AlertType.InsufficientStock, AlertDedupKey.InsufficientStock(productId), productId, null, null, orderId, null);

    /// <summary>A stock-sync task of this channel and product failed for good.</summary>
    public static AlertOccurrence StockSyncFailed(Guid channelId, Guid productId, Guid taskId, string errorCode) =>
        new(AlertType.StockSyncFailed, AlertDedupKey.StockSyncFailed(channelId, productId), productId, channelId, taskId, null, Shorten(errorCode));

    /// <summary>A task of another type failed for good.</summary>
    public static AlertOccurrence ProcessingFailed(Guid taskId, Guid? orderId, string errorCode) =>
        new(AlertType.ProcessingFailed, AlertDedupKey.ProcessingFailed(taskId), null, null, taskId, orderId, Shorten(errorCode));

    private static string Shorten(string errorCode) =>
        errorCode.Length <= Alert.MaxErrorCodeLength ? errorCode : errorCode[..Alert.MaxErrorCodeLength];
}

/// <summary>A row an alert statement inserted or changed, as it is afterwards.</summary>
public sealed record AlertChange(Guid AlertId, AlertType Type, AlertStatus Status, Guid? ProductId, Guid? ChannelId);

/// <summary>
/// The only writer of <c>Alerts</c>. Every write is one SQL statement inside the transaction of the change that causes it
/// (<c>INSERT … ON CONFLICT</c> on the partial unique index of unresolved keys, or a conditional <c>UPDATE … RETURNING</c>),
/// so it is race-free without extra locks and an alert never exists without its cause nor survives its rollback. The same
/// holds for the realtime hints, sent here with <c>pg_notify</c> in that transaction (raw SQL is invisible to the change
/// tracker, so <c>RealtimeHintInterceptor</c> cannot see alerts): <c>alert.changed</c> for every row a statement changed,
/// <c>low_stock.created</c> in addition for an inserted low-stock alert; a statement that changed nothing sends nothing.
/// <para>
/// Statements that touch several rows take them in <c>DedupKey</c> order, so two transactions can never wait for each
/// other's alert rows. Low-stock rows need no such order: they are only written under the inventory row locks of their
/// products, which every caller takes in <c>ProductId</c> order.
/// </para>
/// </summary>
public sealed class AlertStore(OmnichannelDbContext dbContext, TimeProvider timeProvider)
{
    private const string Open = nameof(AlertStatus.Open);
    private const string Acknowledged = nameof(AlertStatus.Acknowledged);
    private const string Resolved = nameof(AlertStatus.Resolved);
    private const string LowStock = nameof(AlertType.LowStock);

    private const string OpenLowStockSql = $"""
        INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "ProductId", "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount")
        SELECT c."AlertId", '{LowStock}', '{Open}', c."DedupKey", c."ProductId", @now, @now, 1
        FROM unnest(@productIds, @alertIds, @dedupKeys) AS c("ProductId", "AlertId", "DedupKey")
        JOIN "Inventories" i ON i."ProductId" = c."ProductId"
        JOIN "Products" p ON p."ProductId" = i."ProductId"
        WHERE {InventoryPredicates.IsLowStockSql}
        ON CONFLICT ("DedupKey") WHERE "Status" <> '{Resolved}' DO NOTHING
        RETURNING "AlertId", "Type", "Status", "ProductId", "ChannelId"
        """;

    private const string ResolveLowStockSql = $"""
        UPDATE "Alerts" a
        SET "Status" = '{Resolved}', "ResolvedAt" = @now
        FROM "Inventories" i
        JOIN "Products" p ON p."ProductId" = i."ProductId"
        WHERE a."DedupKey" = ANY(@dedupKeys) AND a."Status" <> '{Resolved}'
          AND i."ProductId" = a."ProductId" AND NOT ({InventoryPredicates.IsLowStockSql})
        RETURNING a."AlertId", a."Type", a."Status", a."ProductId", a."ChannelId"
        """;

    // A refresh keeps the status: an acknowledged alert stays acknowledged.
    private const string OpenOrRefreshSql = $"""
        INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "ProductId", "ChannelId", "TaskId", "OrderId", "ErrorCode", "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount")
        VALUES (@alertId, @type, '{Open}', @dedupKey, @productId, @channelId, @taskId, @orderId, @errorCode, @now, @now, 1)
        ON CONFLICT ("DedupKey") WHERE "Status" <> '{Resolved}'
        DO UPDATE SET
            "LastOccurredAt" = EXCLUDED."LastOccurredAt",
            "OccurrenceCount" = "Alerts"."OccurrenceCount" + 1,
            "TaskId" = EXCLUDED."TaskId",
            "OrderId" = EXCLUDED."OrderId",
            "ErrorCode" = EXCLUDED."ErrorCode"
        RETURNING "AlertId", "Type", "Status", "ProductId", "ChannelId"
        """;

    private const string ResolveSql = $"""
        UPDATE "Alerts" a
        SET "Status" = '{Resolved}', "ResolvedAt" = @now
        FROM (
            SELECT "AlertId" FROM "Alerts"
            WHERE "DedupKey" = ANY(@dedupKeys) AND "Status" <> '{Resolved}'
            ORDER BY "DedupKey" COLLATE "C"
            FOR UPDATE
        ) AS locked
        WHERE a."AlertId" = locked."AlertId"
        RETURNING a."AlertId", a."Type", a."Status", a."ProductId", a."ChannelId"
        """;

    private const string AcknowledgeSql = $"""
        UPDATE "Alerts"
        SET "Status" = '{Acknowledged}', "AcknowledgedAt" = @now, "AcknowledgedBy" = @acknowledgedBy
        WHERE "AlertId" = @alertId AND "Status" = '{Open}'
        RETURNING "AlertId", "Type", "Status", "ProductId", "ChannelId"
        """;

    /// <summary>
    /// Brings the low-stock alerts of <paramref name="productIds"/> in line with their current stock and threshold: opens
    /// one where the product is low and has no unresolved alert, resolves the unresolved one where it is not low any more.
    /// Low stock is a state, not a counter: an unresolved alert of a product that is still low is left unchanged.
    /// Call it after the stock or threshold change, while the transaction holds the inventory row locks of the products.
    /// </summary>
    public async Task<IReadOnlyList<AlertChange>> ReconcileLowStockAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        if (productIds.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var ids = productIds.Distinct().ToArray();
        var dedupKeys = ids.Select(AlertDedupKey.LowStock).ToArray();

        var opened = await QueryAsync(
            OpenLowStockSql,
            cancellationToken,
            Timestamp("now", now),
            new NpgsqlParameter("productIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
            // Candidate ids, one per product; only the rows that are inserted use theirs.
            new NpgsqlParameter("alertIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.Select(_ => Guid.CreateVersion7()).ToArray() },
            TextArray("dedupKeys", dedupKeys));

        var resolved = await QueryAsync(ResolveLowStockSql, cancellationToken, Timestamp("now", now), TextArray("dedupKeys", dedupKeys));

        var hints = new List<RealtimeHint>(opened.Count * 2 + resolved.Count);
        foreach (var alert in opened)
        {
            hints.Add(ChangedHint(alert));
            hints.Add(RealtimeHint.LowStockCreated(alert.AlertId, alert.ProductId!.Value));
        }

        hints.AddRange(resolved.Select(ChangedHint));
        await NotifyAsync(hints, cancellationToken);
        return [.. opened, .. resolved];
    }

    /// <summary>
    /// Opens an alert per occurrence, or refreshes the unresolved alert with the same key (latest occurrence, task, order and
    /// error code; count + 1; status kept). Not for low stock (<see cref="ReconcileLowStockAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<AlertChange>> OpenOrRefreshAsync(IReadOnlyCollection<AlertOccurrence> occurrences, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        if (occurrences.Any(o => o.Type == AlertType.LowStock))
        {
            throw new ArgumentException("Low-stock alerts are reconciled from the current stock, not opened by occurrence.", nameof(occurrences));
        }

        var now = timeProvider.GetUtcNow();
        var changes = new List<AlertChange>(occurrences.Count);
        foreach (var occurrence in occurrences.OrderBy(o => o.DedupKey, StringComparer.Ordinal))
        {
            changes.AddRange(await QueryAsync(
                OpenOrRefreshSql,
                cancellationToken,
                Uuid("alertId", Guid.CreateVersion7()),
                Text("type", occurrence.Type.ToString()),
                Text("dedupKey", occurrence.DedupKey),
                Uuid("productId", occurrence.ProductId),
                Uuid("channelId", occurrence.ChannelId),
                Uuid("taskId", occurrence.TaskId),
                Uuid("orderId", occurrence.OrderId),
                Text("errorCode", occurrence.ErrorCode),
                Timestamp("now", now)));
        }

        await NotifyAsync(changes.Select(ChangedHint).ToList(), cancellationToken);
        return changes;
    }

    /// <summary>Resolves the unresolved alerts with these keys (Open or Acknowledged); keys without one are ignored.</summary>
    public async Task<IReadOnlyList<AlertChange>> ResolveAsync(IReadOnlyCollection<string> dedupKeys, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        if (dedupKeys.Count == 0)
        {
            return [];
        }

        var resolved = await QueryAsync(
            ResolveSql,
            cancellationToken,
            Timestamp("now", timeProvider.GetUtcNow()),
            TextArray("dedupKeys", dedupKeys.Distinct(StringComparer.Ordinal).ToArray()));

        await NotifyAsync(resolved.Select(ChangedHint).ToList(), cancellationToken);
        return resolved;
    }

    /// <summary><c>Open</c> → <c>Acknowledged</c>.</summary>
    /// <returns>Null when the alert is not Open (unknown, acknowledged already, or resolved): nothing was written.</returns>
    public async Task<AlertChange?> AcknowledgeAsync(Guid alertId, string acknowledgedBy, CancellationToken cancellationToken)
    {
        EnsureTransaction();

        var acknowledged = await QueryAsync(
            AcknowledgeSql,
            cancellationToken,
            Uuid("alertId", alertId),
            Text("acknowledgedBy", acknowledgedBy),
            Timestamp("now", timeProvider.GetUtcNow()));

        await NotifyAsync(acknowledged.Select(ChangedHint).ToList(), cancellationToken);
        return acknowledged.SingleOrDefault();
    }

    /// <summary>An alert, and its hint, must never become visible without the change that caused it.</summary>
    private void EnsureTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Alerts are written inside the transaction of the change that causes them; no transaction is open.");
        }
    }

    private async Task<List<AlertChange>> QueryAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        var rows = await dbContext.Database.SqlQueryRaw<AlertRow>(sql, parameters).ToListAsync(cancellationToken);
        return rows
            .Select(r => new AlertChange(r.AlertId, Enum.Parse<AlertType>(r.Type), Enum.Parse<AlertStatus>(r.Status), r.ProductId, r.ChannelId))
            .ToList();
    }

    /// <summary>Delivered by PostgreSQL at the commit of the caller's transaction, discarded by its rollback.</summary>
    private async Task NotifyAsync(IReadOnlyList<RealtimeHint> hints, CancellationToken cancellationToken)
    {
        if (hints.Count == 0)
        {
            return;
        }

        var payload = new RealtimeHintBatch(hints).ToPayload();
        await dbContext.Database.ExecuteSqlAsync($"SELECT pg_notify({RealtimeChannel.Name}, {payload})", cancellationToken);
    }

    private static RealtimeHint ChangedHint(AlertChange alert) =>
        RealtimeHint.AlertChanged(alert.AlertId, alert.Type, alert.Status, alert.ProductId, alert.ChannelId);

    private static NpgsqlParameter Uuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter TextArray(string name, string[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values };

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private sealed class AlertRow
    {
        public Guid AlertId { get; init; }
        public string Type { get; init; } = null!;
        public string Status { get; init; } = null!;
        public Guid? ProductId { get; init; }
        public Guid? ChannelId { get; init; }
    }
}

using Omnichannel.Infrastructure.Persistence.Inventories;

namespace Omnichannel.Infrastructure.Persistence.Alerts;

/// <summary>
/// What the <c>Alerts</c> migration opens for the state that exists when the table is created, so the alert view matches
/// the data from the first deploy on. Kept here as one constant (not inside the migration) so that a test can run it
/// against seeded rows: the test database is migrated empty, which proves nothing about it.
/// <list type="bullet">
/// <item><c>LowStock</c> for every product that is low now (the shared rule);</item>
/// <item><c>StockSyncFailed</c> for every channel and product whose most recently finished stock-sync task is Failed
/// (a later success would have resolved the alert; tasks that are still waiting or running decide nothing yet);</item>
/// <item><c>ProcessingFailed</c> for every Failed task of another type.</item>
/// </list>
/// Failure alerts carry the task, the error code of its last attempt and the time it failed. No <c>InsufficientStock</c>
/// backfill: that alert is tied to future imports; old rejections are history in <c>SystemEventLog</c>.
/// The keys are those of <c>AlertDedupKey</c> (ids without dashes). Ids are random (v4): PostgreSQL 17 cannot generate v7.
/// Running it again changes nothing (<c>ON CONFLICT DO NOTHING</c>).
/// </summary>
public static class AlertBackfill
{
    public const string Sql = $"""
        INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "ProductId", "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount")
        SELECT gen_random_uuid(), 'LowStock', 'Open', 'low_stock:' || replace(i."ProductId"::text, '-', ''), i."ProductId", now(), now(), 1
        FROM "Inventories" i
        JOIN "Products" p ON p."ProductId" = i."ProductId"
        WHERE {InventoryPredicates.IsLowStockSql}
        ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved' DO NOTHING;

        INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "ProductId", "ChannelId", "TaskId", "ErrorCode", "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount")
        SELECT gen_random_uuid(), 'StockSyncFailed', 'Open',
               'stock_sync_failed:' || replace(latest."ChannelId"::text, '-', '') || ':' || replace(latest."ProductId"::text, '-', ''),
               latest."ProductId", latest."ChannelId", latest."TaskId",
               (SELECT a."ErrorCode" FROM "ProcessingAttempts" a WHERE a."TaskId" = latest."TaskId" ORDER BY a."AttemptNo" DESC LIMIT 1),
               latest."FinishedAt", latest."FinishedAt", 1
        FROM (
            SELECT DISTINCT ON (t."ChannelId", t."ProductId")
                   t."TaskId", t."ChannelId", t."ProductId", t."Status", COALESCE(t."UpdatedAt", t."CreatedAt") AS "FinishedAt"
            FROM "ProcessingTasks" t
            WHERE t."TaskType" = 'StockSync' AND t."Status" IN ('Failed', 'Succeeded')
            ORDER BY t."ChannelId", t."ProductId", COALESCE(t."UpdatedAt", t."CreatedAt") DESC, t."TaskId" DESC
        ) AS latest
        WHERE latest."Status" = 'Failed'
        ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved' DO NOTHING;

        INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "TaskId", "OrderId", "ErrorCode", "FirstOccurredAt", "LastOccurredAt", "OccurrenceCount")
        SELECT gen_random_uuid(), 'ProcessingFailed', 'Open', 'processing_failed:' || replace(t."TaskId"::text, '-', ''),
               t."TaskId", t."OrderId",
               (SELECT a."ErrorCode" FROM "ProcessingAttempts" a WHERE a."TaskId" = t."TaskId" ORDER BY a."AttemptNo" DESC LIMIT 1),
               COALESCE(t."UpdatedAt", t."CreatedAt"), COALESCE(t."UpdatedAt", t."CreatedAt"), 1
        FROM "ProcessingTasks" t
        WHERE t."TaskType" <> 'StockSync' AND t."Status" = 'Failed'
        ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved' DO NOTHING;
        """;
}

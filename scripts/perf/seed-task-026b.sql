-- Seed of a THROWAWAY database for the query plans of task 026b (Part B2). Never run it against development or
-- production: it writes made-up customers, products, orders, tasks, attempts, messages and alerts.
--
--   psql -h 127.0.0.1 -U postgres -d <throwaway database, migrated> -v ON_ERROR_STOP=1 -f scripts/perf/seed-task-026b.sql
--
-- Sizes (review 015): 8,000 orders over 30 days, 24,000 processing tasks (8,000 order, 14,000 stock sync, 1,000
-- invoice, 1,000 e-mail) with about 29,000 attempts, 40,000 outbox rows, 2,000 alerts, 500 products, 200 customers.
-- Like a system that works: 96 % of the tasks succeeded, 3 % failed, 1 % are unfinished - a sixth of those for longer
-- than half an hour (stale); 30 outbox rows are pending and 12 failed. Deterministic: the same rows on every run, with
-- times relative to now().
BEGIN;

CREATE TEMP TABLE seed_channels ON COMMIT DROP AS
SELECT "ChannelId", row_number() OVER (ORDER BY "Slug") - 1 AS n FROM "Channels" WHERE "Slug" IN ('website', 'pos', 'digital-platform');

INSERT INTO "Customers" ("CustomerId", "Name", "CreatedAt", "CreatedBy")
SELECT md5('customer-' || g)::uuid, 'Perf customer ' || g, now() - interval '40 days', 'seed'
FROM generate_series(1, 200) g;

INSERT INTO "Products" ("ProductId", "Sku", "Name", "Price", "LowStockThreshold", "CreatedAt", "CreatedBy")
SELECT md5('product-' || g)::uuid, 'PERF-' || lpad(g::text, 4, '0'), 'Perf product ' || g, 10 + g, 5, now() - interval '40 days', 'seed'
FROM generate_series(1, 500) g;

INSERT INTO "Inventories" ("ProductId", "TotalStock", "ReservedStock", "CreatedAt", "CreatedBy")
SELECT md5('product-' || g)::uuid, 1000, g % 50, now() - interval '40 days', 'seed'
FROM generate_series(1, 500) g;

-- 8,000 orders, evenly over the last 30 days.
INSERT INTO "Orders" ("OrderId", "ChannelId", "CustomerId", "ExternalOrderId", "Status", "TotalAmount", "CreatedAt", "CreatedBy", "PaymentStatus")
SELECT md5('order-' || g)::uuid,
       (SELECT "ChannelId" FROM seed_channels WHERE n = g % 3),
       md5('customer-' || (1 + g % 200))::uuid,
       'PERF-' || g,
       CASE WHEN g % 10 = 0 THEN 'InsufficientStock' WHEN g % 20 = 1 THEN 'Completed' ELSE 'Reserved' END,
       100 + g % 900,
       now() - interval '30 days' * (g::float8 / 8000),
       'system:order-worker',
       'Unpaid'
FROM generate_series(1, 8000) g;

-- 24,000 tasks. g 1..8000: the task of order g; 8001..22000: stock sync; 22001..23000 invoice; 23001..24000 e-mail.
-- Status by g: every 100th unfinished (Pending / InProgress / RetryScheduled in turn), 3 of 100 failed, the rest succeeded.
-- An unfinished task was last changed within the last 30 minutes - except every sixth, which stands still for hours.
CREATE TEMP TABLE seed_tasks ON COMMIT DROP AS
SELECT g,
       md5('task-' || g)::uuid AS task_id,
       CASE WHEN g <= 8000 THEN 'OrderProcessing' WHEN g <= 22000 THEN 'StockSync' WHEN g <= 23000 THEN 'Invoice' ELSE 'Email' END AS task_type,
       CASE WHEN g % 100 = 0 THEN (ARRAY['Pending', 'InProgress', 'RetryScheduled'])[1 + (g / 100) % 3]
            WHEN g % 100 IN (1, 2, 3) THEN 'Failed'
            ELSE 'Succeeded' END AS status,
       now() - interval '30 days' * (((g * 7919) % 24000)::float8 / 24000) AS created_at
FROM generate_series(1, 24000) g;

INSERT INTO "ProcessingTasks" ("TaskId", "OrderId", "TaskType", "Status", "RetryCount", "CorrelationId", "MessageId", "CreatedAt", "UpdatedAt",
                               "AttemptBudgetStart", "ChannelId", "ProductId")
SELECT task_id,
       CASE WHEN task_type = 'StockSync' THEN NULL WHEN g <= 8000 THEN md5('order-' || g)::uuid ELSE md5('order-' || (1 + g % 8000))::uuid END,
       task_type,
       status,
       CASE WHEN status IN ('RetryScheduled', 'Failed') THEN 1 ELSE 0 END,
       'perf-' || g,
       md5('message-' || g)::uuid::text,
       CASE WHEN status IN ('Pending', 'InProgress', 'RetryScheduled')
            THEN CASE WHEN (g / 100) % 6 = 0 THEN now() - interval '3 hours' - interval '1 minute' * (g % 97)
                      ELSE now() - interval '1 minute' * (1 + g % 25) END
            ELSE created_at END,
       CASE WHEN status = 'Pending' THEN NULL
            WHEN status IN ('InProgress', 'RetryScheduled')
            THEN CASE WHEN (g / 100) % 6 = 0 THEN now() - interval '2 hours' - interval '1 minute' * (g % 97)
                      ELSE now() - interval '10 seconds' * (1 + g % 100) END
            ELSE created_at + interval '2 seconds' END,
       0,
       CASE WHEN task_type = 'StockSync' THEN (SELECT "ChannelId" FROM seed_channels WHERE n = g % 3) END,
       CASE WHEN task_type = 'StockSync' THEN md5('product-' || (1 + g % 500))::uuid END
FROM seed_tasks;

-- Attempts: one per started task, two for a task that failed or waits for a retry.
INSERT INTO "ProcessingAttempts" ("AttemptId", "TaskId", "WorkerName", "WorkerInstanceId", "AttemptNo", "Status", "ErrorCode", "ErrorMessage",
                                  "StartedAt", "FinishedAt")
SELECT md5('attempt-' || t.g || '-' || a.n)::uuid,
       t.task_id,
       CASE t.task_type WHEN 'OrderProcessing' THEN 'OrderWorker' WHEN 'StockSync' THEN 'StockSyncWorker' WHEN 'Invoice' THEN 'InvoiceWorker' ELSE 'NotificationWorker' END,
       'perf@host-' || (t.g % 2) || ':1',
       a.n,
       CASE WHEN t.status = 'Succeeded' THEN 'Succeeded' WHEN t.status = 'InProgress' THEN 'Running' ELSE 'Failed' END,
       CASE WHEN t.status = 'Failed' THEN (ARRAY['channel_rejected', 'channel_unavailable', 'database_unavailable'])[1 + t.g % 3]
            WHEN t.status = 'RetryScheduled' THEN 'channel_unavailable' END,
       CASE WHEN t.status IN ('Failed', 'RetryScheduled') THEN 'seeded failure' END,
       t.created_at + interval '1 second' * a.n,
       CASE WHEN t.status <> 'InProgress' THEN t.created_at + interval '1 second' * a.n + interval '500 milliseconds' END
FROM seed_tasks t
JOIN LATERAL generate_series(1, CASE WHEN t.status IN ('Failed', 'RetryScheduled') THEN 2 ELSE 1 END) a(n) ON t.status <> 'Pending';

-- 40,000 outbox rows: the message of every task, and inventory.changed events for the rest. 30 pending, 12 failed.
INSERT INTO "OutboxEvents" ("OutboxEventId", "EventType", "AggregateId", "Payload", "CorrelationId", "Status", "PublishedAt", "CreatedAt",
                            "UpdatedAt", "LastError", "PublishAttempts", "RoutingKey")
SELECT md5('message-' || g)::uuid,
       CASE WHEN g > 24000 THEN 'inventory.changed' WHEN g <= 8000 THEN 'order.created' WHEN g <= 22000 THEN 'stock.sync.requested'
            WHEN g <= 23000 THEN 'invoice.requested' ELSE 'email.requested' END,
       CASE WHEN g <= 24000 THEN md5('task-' || g)::uuid::text ELSE md5('product-' || (1 + g % 500))::uuid::text END,
       '{"seed": true}'::jsonb,
       'perf-' || g,
       CASE WHEN g % 1333 = 0 THEN 'Pending' WHEN g % 3333 = 1 THEN 'Failed' ELSE 'Published' END,
       CASE WHEN g % 1333 <> 0 AND g % 3333 <> 1 THEN now() - interval '30 days' * (((g * 7919) % 40000)::float8 / 40000) + interval '1 second' END,
       CASE WHEN g % 1333 = 0 THEN now() - interval '1 second' * (g % 300) ELSE now() - interval '30 days' * (((g * 7919) % 40000)::float8 / 40000) END,
       now() - interval '1 day',
       CASE WHEN g % 3333 = 1 THEN 'seeded' END,
       CASE WHEN g % 3333 = 1 THEN 10 ELSE 0 END,
       NULL
FROM generate_series(1, 40000) g;

-- 2,000 alerts: one per failed task (the first 720), low stock for the rest; a tenth still open.
INSERT INTO "Alerts" ("AlertId", "Type", "Status", "DedupKey", "ProductId", "ChannelId", "TaskId", "OrderId", "ErrorCode", "FirstOccurredAt",
                      "LastOccurredAt", "OccurrenceCount", "ResolvedAt")
SELECT md5('alert-' || a.g)::uuid,
       CASE WHEN f.task_id IS NOT NULL THEN 'ProcessingFailed' ELSE 'LowStock' END,
       CASE WHEN a.g % 10 = 0 THEN 'Open' ELSE 'Resolved' END,
       'perf:' || a.g,
       CASE WHEN f.task_id IS NULL THEN md5('product-' || (1 + a.g % 500))::uuid END,
       NULL,
       f.task_id,
       NULL,
       CASE WHEN f.task_id IS NOT NULL THEN 'channel_rejected' END,
       now() - interval '20 days' * (a.g::float8 / 2000),
       now() - interval '20 days' * (a.g::float8 / 2000),
       1,
       CASE WHEN a.g % 10 <> 0 THEN now() - interval '1 day' END
FROM generate_series(1, 2000) a(g)
LEFT JOIN (SELECT task_id, row_number() OVER (ORDER BY g) AS rn FROM seed_tasks WHERE status = 'Failed') f ON f.rn = a.g;

COMMIT;

ANALYZE;

SELECT 'orders' AS what, count(*) FROM "Orders"
UNION ALL SELECT 'tasks', count(*) FROM "ProcessingTasks"
UNION ALL SELECT 'tasks unfinished', count(*) FROM "ProcessingTasks" WHERE "Status" IN ('Pending', 'InProgress', 'RetryScheduled')
UNION ALL SELECT 'tasks stale (30 min)', count(*) FROM "ProcessingTasks"
    WHERE "Status" IN ('Pending', 'InProgress', 'RetryScheduled') AND COALESCE("UpdatedAt", "CreatedAt") < now() - interval '30 minutes'
UNION ALL SELECT 'attempts', count(*) FROM "ProcessingAttempts"
UNION ALL SELECT 'outbox', count(*) FROM "OutboxEvents"
UNION ALL SELECT 'outbox pending', count(*) FROM "OutboxEvents" WHERE "Status" = 'Pending'
UNION ALL SELECT 'outbox failed', count(*) FROM "OutboxEvents" WHERE "Status" = 'Failed'
UNION ALL SELECT 'alerts', count(*) FROM "Alerts";

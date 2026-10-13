-- Invariants of the reserve path after a load scenario (task 026b, Part E). Every row must show 0 in "violations".
-- Read-only. Run by scripts/load/run.ps1 inside the PostgreSQL container of the throwaway stack.
\pset footer off
SELECT 'stock: Available = Total - Reserved, nothing negative' AS invariant,
       count(*) AS violations
FROM "Inventories"
WHERE "AvailableStock" <> "TotalStock" - "ReservedStock" OR "AvailableStock" < 0 OR "ReservedStock" < 0 OR "TotalStock" < 0
UNION ALL
SELECT 'stock: Reserved = quantities of the lines of the Reserved orders, per product',
       count(*)
FROM "Inventories" i
LEFT JOIN (
    SELECT oi."ProductId", sum(oi."Stock") AS reserved
    FROM "OrderItems" oi JOIN "Orders" o ON o."OrderId" = oi."OrderId"
    WHERE o."Status" = 'Reserved'
    GROUP BY oi."ProductId") r ON r."ProductId" = i."ProductId"
WHERE i."ReservedStock" <> COALESCE(r.reserved, 0)
UNION ALL
SELECT 'movements: exactly one Reserve movement of the line''s quantity per line of a Reserved order',
       count(*)
FROM "OrderItems" oi
JOIN "Orders" o ON o."OrderId" = oi."OrderId" AND o."Status" = 'Reserved'
LEFT JOIN (
    SELECT m."ReferenceId", m."ProductId", count(*) AS movements, sum(m."Stock") AS quantity
    FROM "InventoryMovements" m
    WHERE m."Type" = 'Reserve' AND m."ReferenceType" = 'Order'
    GROUP BY m."ReferenceId", m."ProductId") m ON m."ReferenceId" = oi."OrderId"::text AND m."ProductId" = oi."ProductId"
WHERE COALESCE(m.movements, 0) <> 1 OR m.quantity <> oi."Stock"
UNION ALL
SELECT 'movements: no Reserve movement for an order that is not Reserved',
       count(*)
FROM "InventoryMovements" m
LEFT JOIN "Orders" o ON o."OrderId"::text = m."ReferenceId"
WHERE m."Type" = 'Reserve' AND m."ReferenceType" = 'Order' AND (o."OrderId" IS NULL OR o."Status" <> 'Reserved')
UNION ALL
SELECT 'orders: one order per channel and external order id',
       count(*)
FROM (SELECT 1 FROM "Orders" GROUP BY "ChannelId", "ExternalOrderId" HAVING count(*) > 1) d
UNION ALL
SELECT 'tasks: none unfinished',
       count(*)
FROM "ProcessingTasks" WHERE "Status" IN ('Pending', 'InProgress', 'RetryScheduled')
UNION ALL
SELECT 'tasks: none failed',
       count(*)
FROM "ProcessingTasks" WHERE "Status" = 'Failed'
UNION ALL
SELECT 'outbox: nothing waiting, nothing failed',
       count(*)
FROM "OutboxEvents" WHERE "Status" <> 'Published';

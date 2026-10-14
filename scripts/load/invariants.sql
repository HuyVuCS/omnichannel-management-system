-- Invariants of the reserve path after a load scenario (task 026b, Part E) and after the acceptance run (task 027,
-- scripts/smoke/task-027.ps1 -Mode Full). Every row must show 0 in "violations".
-- Read-only. Run by scripts/load/run.ps1 and task-027.ps1 inside the PostgreSQL container of the throwaway stack.
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
-- From the code (task 027): an inventory row starts at 0 (ProductService), an import adds to TotalStock
-- (InventoryImportService), a reservation adds to ReservedStock (OrderReservationService). Release and Deduct are defined
-- by InventoryMovementType (Release: reserved down; Deduct: total and reserved down) though no code writes them yet.
SELECT 'stock: Total = imports - deductions, Reserved = reservations - releases - deductions (the movement history)',
       count(*)
FROM "Inventories" i
LEFT JOIN (
    SELECT "ProductId",
           sum(CASE "Type" WHEN 'Import' THEN "Stock" WHEN 'Deduct' THEN -"Stock" ELSE 0 END) AS total,
           sum(CASE "Type" WHEN 'Reserve' THEN "Stock" WHEN 'Release' THEN -"Stock" WHEN 'Deduct' THEN -"Stock" ELSE 0 END) AS reserved
    FROM "InventoryMovements"
    GROUP BY "ProductId") m ON m."ProductId" = i."ProductId"
WHERE i."TotalStock" <> COALESCE(m.total, 0) OR i."ReservedStock" <> COALESCE(m.reserved, 0)
UNION ALL
-- Adjustment has no direction of its own (Stock is always positive): a correction would need one before it can count.
SELECT 'movements: no Adjustment (no code writes one; its sign is not defined)',
       count(*)
FROM "InventoryMovements" WHERE "Type" = 'Adjustment'
UNION ALL
-- One transaction (doc 7.3): every movement has its inventory.changed event, written in the same SaveChanges.
SELECT 'movements: every movement has exactly one inventory.changed outbox event',
       count(*)
FROM "InventoryMovements" m
WHERE (SELECT count(*) FROM "OutboxEvents" e WHERE e."InventoryMovementId" = m."InventoryMovementId" AND e."EventType" = 'inventory.changed') <> 1
UNION ALL
SELECT 'outbox: every inventory.changed event has its movement',
       count(*)
FROM "OutboxEvents" e
LEFT JOIN "InventoryMovements" m ON m."InventoryMovementId" = e."InventoryMovementId"
WHERE e."EventType" = 'inventory.changed' AND m."InventoryMovementId" IS NULL
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

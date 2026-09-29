# Task 003 — Step 4b: Inventory import, movement history, `inventory.changed` outbox events

Status: Open
Written by: Cowork, 2026-09-29
Depends on: 002 (paging contract, error mapping, integration-test infrastructure)

## Context
Every stock change must leave an `InventoryMovement` and, in the **same transaction**, an `OutboxEvent`
(`INVENTORY_MOVEMENT 1:0..1 OUTBOX_EVENT`, doc §9). The Outbox Publisher (step 8) will later publish the pending rows;
until then they simply stay `Pending`. This task introduces the first real stock mutation, so the concurrency rules
below are mandatory.

## Endpoints

### `POST /api/inventory/import` — ManagerOrAdmin
Request:
```json
{ "documentNumber": "GRN-000123", "documentSeries": "2026", "items": [ { "productId": "...", "stock": 50 } ] }
```
Validation: `documentNumber` required ≤ 64; `documentSeries` optional ≤ 32; `items` 1–200; `productId` non-empty and
**distinct**; `stock` 1–1,000,000.

Behaviour, all inside **one** explicit transaction (`Database.BeginTransactionAsync`):
1. Sort items by `ProductId` (consistent lock order → no deadlocks between concurrent multi-item imports).
2. For each item, a single atomic statement — **no read-modify-write in C#**:
   ```sql
   UPDATE "Inventories"
   SET "TotalStock" = "TotalStock" + @stock, "UpdatedAt" = @now, "UpdatedBy" = @actor
   WHERE "ProductId" = @productId
   RETURNING "TotalStock", "ReservedStock", "AvailableStock";
   ```
   Raw SQL bypasses the auditing interceptor, hence explicit `UpdatedAt/UpdatedBy` (`TimeProvider`, `ICurrentUser`).
   0 rows → unknown product → roll back everything, 400 validation error on `items[i].productId`.
3. Insert `InventoryMovement(Import, stock, ReferenceType=StockImport, ReferenceId, DocumentNumber, DocumentSeries)`
   with `ReferenceId = documentSeries is null ? documentNumber : $"{documentSeries}/{documentNumber}"`.
4. Insert `OutboxEvent(eventType "inventory.changed", aggregateId = productId, payload, correlationId, inventoryMovementId)`.
5. Commit. Do not enable `EnableRetryOnFailure`.

Response 200:
```json
{ "referenceId": "2026/GRN-000123",
  "items": [ { "productId": "...", "movementId": "...", "totalStock": 50, "reservedStock": 0, "availableStock": 50 } ] }
```

Idempotency: the same document must not be applied twice (client retry, double click).
- New migration `InventoryImportIdempotency`: replace index `UX_InventoryMovements_Reference_Reservation` with
  `UX_InventoryMovements_Reference` on the same columns and filter
  `"ReferenceId" IS NOT NULL AND "Type" IN ('Import', 'Reserve', 'Release')`.
- Unique violation on that index → whole transaction rolled back → 409 `stock_import_already_applied`.
- Update `ModelConfigurationTests` for the new index name/filter.

### `GET /api/inventory/movements` — AnyStaff
Paged (task 002 contract). Filters: `productId`, `type`, `referenceType`, `referenceId`, `from`, `to` (UTC, on `CreatedAt`).
Order: `CreatedAt desc, InventoryMovementId desc` (no `sortBy`). Item: `inventoryMovementId, productId, sku, productName,
type, stock, referenceType, referenceId, documentNumber, documentSeries, createdAt, createdBy`.

## Message contract (shared with workers)
`Omnichannel.Application/Common/Messaging/`:
- `EventTypes` constants: `OrderCreated = "order.created"`, `InventoryChanged = "inventory.changed"`,
  `InvoiceRequested`, `EmailRequested`, `ProcessingRetry` (doc §10 routing keys).
- `InventoryChangedPayload(productId, movementId, movementType, totalStock, reservedStock, availableStock, occurredAt)`.
- One `MessageJson` helper holding the `JsonSerializerOptions` (camelCase, enums as strings) used for every outbox payload.
The payload is a snapshot; the stock-sync worker (step 8) must still re-read current stock before calling a channel.

## Tests (required)
Integration:
1. 20 concurrent imports of +1 (distinct document numbers) for one product → `TotalStock = 20`, 20 movements,
   20 `Pending` outbox rows each linked to its movement.
2. Re-post the same document → 409 `stock_import_already_applied`; stock, movement and outbox counts unchanged.
3. Import with one valid and one unknown product → 400; valid product's stock unchanged, no movement, no outbox row.
4. Two concurrent imports each containing products A and B in opposite order → both 200 (no deadlock), totals correct.
5. Outbox payload deserialises to `InventoryChangedPayload` with matching values; `CorrelationId` equals the
   `X-Correlation-Id` sent with the request.
6. Movements endpoint: filter by `productId` and `referenceId`, newest first, paging.
7. Staff import → 403.
Unit: import request validator (duplicates, bounds).

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (report unit/integration counts).
- [ ] Migration generated + `has-pending-model-changes` clean; SQL of the new migration pasted in the report.
- [ ] No code path changes `TotalStock`/`ReservedStock` except through a movement written in the same transaction.

## Out of scope
Negative adjustments, stock deduction/release, low-stock alerts (step 11), publishing outbox rows (step 8).

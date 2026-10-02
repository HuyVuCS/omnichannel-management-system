# Task 017 — Alerts backend (low stock, insufficient stock, processing failures) + frontend test stability

Status: Open
Written by: Cowork, 2026-10-03
Depends on: 016b (accepted)

## Context
Doc §7.1 `AlertService` ("Low-stock và processing failure"), §8.2 ("InsufficientStock → Alert"), §11 `GET /api/alerts`,
§12 UI event `low_stock.created`, §16 "Low-stock alert hoạt động theo threshold". The Alerts page follows in 018.

**Decisions (user, 2026-10-03):** alerts are stored in a **new `Alerts` table** with a lifecycle
(`Open` → `Acknowledged` → `Resolved`), not in `SystemEventLog` alone; **acknowledge is Manager/Admin only**. Every
design decision of this task is in the implementation log, "Task 017 plan" — read it first. If the implementation needs a
decision that is not in this file or in the log (another alert type, a change of the reserve SQL, a new queue or
consumer, alerts written outside the transaction that causes them, a purge job, e-mail), stop and report (`Blocked`).

Read first: implementation log "Task 002 result" (low-stock rule), "Task 006 plan" (insufficient stock is a business
outcome), "Task 007 plan" (final failure), "Task 014 plan" (stock-sync task per change; only the newest task per
(channel, product) matters), "Task 015 plan/result" (hints), "Task 016b result" (frontend test stability); review 016b;
`InventoryPredicates.cs`, `InventoryImportService.cs`, `OrderReservationService.cs`, `ProductService.cs`,
`ProcessingAttemptStore.cs`, `StockSyncService.cs`, `ProcessingTaskService.cs` (manual retry), `RealtimeHints.cs`,
`RealtimeHintInterceptor.cs`, `DashboardService.cs`, `ExpectedUniqueViolations.cs`; `scripts/smoke/_common.ps1`;
`frontend/vite.config.ts`.

## Part A — Follow-ups from review 016b (separate commits, before Part B)

### A1. Frontend suite stable under load (P2)
1. **Smoke scripts run npm without hosts.** Every smoke script that runs `npm run build` / `npm test` (today 010, 011,
   012, 013, 016, 016b) runs them only **after** it has stopped the API / Workers hosts it started. One helper in
   `_common.ps1` (e.g. `Invoke-FrontendChecks`, called after the hosts are stopped and before `Complete-Smoke`), npm output
   kept in a temp log as in 016b, the result recorded with `Assert-Step`; a script still ends with one table and exit
   code 0/1. A host the script did **not** start (already running) is left alone — then the script prints one line that
   the frontend checks run next to a running host and may be slow.
2. **Worker count by measurement.** With PostgreSQL and RabbitMQ running and nothing else (no API, Workers, dev server,
   browser): three full runs with `maxWorkers: 4` and three with `maxWorkers: 3`; report wall time and failures of each
   run. Choose 3 when its median wall time is at most 5 % above that of 4, otherwise keep 4; record the numbers in the
   report and the choice in `vite.config.ts` (comment with the date and the reason).
3. **Split slow tests.** From a `--reporter=verbose` run of each test file **alone**, list every test that takes more
   than 5 s. Split each into tests that each check one step of the journey (shared setup in a helper, not a copied
   render sequence), so no test does a whole page journey. Every assertion of the old test exists in one of the new ones —
   the report maps old test → new tests. A test that cannot get below 5 s stays with a one-line reason in the report.
4. `testTimeout` stays 20 s (review 016b: raising it hides the next regression).
5. **Acceptance:** 5 consecutive full `npm test` runs green with PostgreSQL and RabbitMQ running (wall time of each in the
   report), and `task-016b.ps1` green end to end including its npm checks.

### A2. Strict `Assert-Step` (P3)
- `Assert-Step` passes only when expected and actual are of the same kind and equal:
  `$null` only equals `$null`; `[bool]` only equals `[bool]`; numbers (any numeric type: `Int32`, `Int64`, `Decimal`,
  `Double` — `ConvertFrom-Json` returns several) are compared by value; strings are compared **case-sensitively**
  (`-ceq`); any other type (array, object, hashtable) **throws** (the caller compares explicitly). A failed step shows
  both values and their type names in the table.
- Fix every existing call that relied on the loose comparison (`$true` against a non-boolean, a case difference, a number
  against a string) so the check keeps its meaning — never by casting with `[bool]` (`[bool]'False'` is `$true`). The
  report lists each changed call (script, line, old → new).
- `scripts/smoke/_assert-selftest.ps1` (ASCII, no API needed): exercises every rule above, including the known traps
  (`$true` vs `'any text'`, `1` vs `'1'`, `'Open'` vs `'open'`, `[int]5` vs `[long]5`, `$null` vs `''`); exit code 0/1.
- Run: the self-test, `task-016b.ps1` and `task-017.ps1`. The older scripts (009–016) are checked statically (every
  changed call listed); running them is optional for the user.

## Part B — `Alerts` table and the alert writer

### Schema (migration `Alerts`)
| Column | Type | Notes |
| --- | --- | --- |
| `AlertId` | uuid PK | UUIDv7, generated in the app |
| `Type` | varchar(32) | `AlertType`: `LowStock`, `InsufficientStock`, `StockSyncFailed`, `ProcessingFailed` |
| `Status` | varchar(32) | `AlertStatus`: `Open`, `Acknowledged`, `Resolved` |
| `DedupKey` | varchar(100), not null | see "Keys"; **unique partial index `WHERE "Status" <> 'Resolved'`** |
| `ProductId` | uuid null, FK → Products | LowStock, InsufficientStock, StockSyncFailed |
| `ChannelId` | uuid null, FK → Channels | StockSyncFailed |
| `TaskId` | uuid null, FK → ProcessingTasks | latest task of the alert (failures) |
| `OrderId` | uuid null, FK → Orders | latest order (InsufficientStock; ProcessingFailed when the task has one) |
| `ErrorCode` | varchar(100) null | latest failure's error code (failures only) |
| `FirstOccurredAt`, `LastOccurredAt` | timestamptz | |
| `OccurrenceCount` | int, ≥ 1 (CHECK) | |
| `AcknowledgedAt`, `AcknowledgedBy` | timestamptz null, varchar null | `AcknowledgedBy` = user id, like `CreatedBy` elsewhere |
| `ResolvedAt` | timestamptz null | |

- CHECKs: `Status = 'Resolved'` ⇔ `ResolvedAt IS NOT NULL`; `AcknowledgedAt IS NULL` ⇔ `AcknowledgedBy IS NULL`.
- FKs without cascade (nothing referenced is ever deleted). No `Payload` column, no audit columns besides the above.
- Indexes: the partial unique on `DedupKey`; one for the list's default order (e.g. `(Status, LastOccurredAt)`);
  `ProductId`. Add another only with a reason in the report.
- The partial unique index is registered in `ExpectedUniqueViolations` only if a code path can hit it as an expected
  conflict (with `ON CONFLICT` none should — say which).
- `omnichannel_app` gets DML on the new table through the default privileges of `01-create-roles.sh` — confirm it in an
  integration test running as the app role, if the test infrastructure has one; otherwise say how it was checked.

### Keys and lifecycle
| Type | `DedupKey` | Opened / refreshed by | Resolved by |
| --- | --- | --- | --- |
| `LowStock` | `low_stock:{productId:N}` | stock or threshold change leaving the product low (the **shared predicate** `AvailableStock <= LowStockThreshold`) | stock or threshold change leaving it not low |
| `InsufficientStock` | `insufficient_stock:{productId:N}` | an order stored as `InsufficientStock`, once per short product (requested > available in the existing read) | the next stock import of that product |
| `StockSyncFailed` | `stock_sync_failed:{channelId:N}:{productId:N}` | final failure of a `StockSync` task | any `StockSync` task of the same (channel, product) reaching `Succeeded` |
| `ProcessingFailed` | `processing_failed:{taskId:N}` | final failure of any other task type (today `OrderProcessing`) | that task reaching `Succeeded` (after a manual retry) |

- **Refresh** of an unresolved alert (same key, `Open` or `Acknowledged`): `LastOccurredAt`, `OccurrenceCount + 1`,
  latest `TaskId` / `OrderId` / `ErrorCode`; the status is kept (an acknowledged alert stays acknowledged). `LowStock` is
  a state, not a counter: an unresolved one is left unchanged.
- **Resolve:** `Status = Resolved`, `ResolvedAt`, from `Open` or `Acknowledged`. A condition that comes back later opens
  a **new** alert row (the partial index allows it).
- **Acknowledge:** `Open` → `Acknowledged` only; it does not resolve. Resolution is always by the system.
- If no product of an insufficient-stock order is short in the existing read (stock arrived in between), no
  `InsufficientStock` alert is written; the order and its `SystemEventLog` warning are unchanged.
- A new product starts with stock 0, so it is low at creation (threshold ≥ 0) and gets a `LowStock` alert — intended
  (the rule never hides an out-of-stock product); test it.

### Writer (`AlertStore` or similar, Infrastructure; interface in Application if a service outside Infrastructure needs it)
- **Every alert write is a single raw SQL statement in the transaction of the change that causes it**:
  `INSERT … ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved' DO UPDATE …` (or `DO NOTHING` for `LowStock`) for
  open/refresh, a conditional `UPDATE … WHERE "Status" <> 'Resolved' … RETURNING` for resolve, a conditional
  `UPDATE … WHERE "Status" = 'Open' … RETURNING` for acknowledge. Race-free without extra locks; never check-then-insert.
  Raw SQL bypasses the auditing interceptor: set every column explicitly.
- The writer asserts that an explicit transaction is open (`Database.CurrentTransaction`) and throws otherwise — an
  alert (and its hint) must never become visible without the change that caused it, nor survive its rollback.
- **Low stock is reconciled, not detected by transition:** `ReconcileLowStockAsync(productIds)` = two set-based
  statements over the given products (open where low, resolve where not low), evaluated from the current
  `Inventories` / `Products` rows **after** the stock update, under the inventory row locks the transaction already holds.
  The predicate is the shared one (`InventoryPredicates.IsLowStock`); a second definition of the rule in SQL text must be
  avoided or, if raw SQL makes it unavoidable, pinned by a test that compares it with the predicate on the same rows.
  **The reserve `UPDATE` statement itself does not change.**
- **Hints:** the writer sends its own `pg_notify('omnichannel_ui', …)` in the same transaction (delivered at commit,
  discarded by rollback) with the payload format of 015 (`RealtimeHintBatch`, same size cap):
  - `alert.changed` (`alertId`, `alertType`, `status`, `productId`, `channelId`) for every insert, refresh, resolve and
    acknowledge that changed a row (statements that changed nothing send nothing);
  - `low_stock.created` (`alertId`, `productId`) additionally when a `LowStock` row was **inserted** (doc §12).
  Add the two constants to `RealtimeEventTypes` and the factories to `RealtimeHint`. The `RealtimeHintInterceptor` does
  not capture `Alert` (one producer). If adding `alertId` / `alertType` to `RealtimeHint` conflicts with its shape, stop.

### Call sites (inside their explicit transactions, after the `SaveChanges` that stores the rows an alert references — FKs — and before commit)
| Path | Call |
| --- | --- |
| `InventoryImportService` (import) | reconcile low stock for the imported products; resolve `InsufficientStock` for them |
| `OrderReservationService` success | reconcile low stock for the reserved products |
| `OrderReservationService` insufficient stock | open/refresh `InsufficientStock` per short product |
| `OrderReservationService` success / insufficient / already-exists (every `task.Succeed`) | resolve `ProcessingFailed` of the task |
| `ProductService.CreateAsync` | explicit transaction; reconcile low stock for the new product |
| `ProductService.UpdateAsync` | explicit transaction; lock the inventory row (`FOR UPDATE`) **before** changing the threshold; reconcile |
| `ProcessingAttemptStore.RecordFinalFailureAsync` | open/refresh `StockSyncFailed` (StockSync tasks) or `ProcessingFailed` (others) |
| `StockSyncService.CompleteAsync` (task → `Succeeded`) | resolve `StockSyncFailed` of the task's (channel, product) |

- Manual retry (`ProcessingTaskService`) does not touch alerts: the alert stays until the retried task succeeds or fails
  again (refresh).
- Concurrent failure and success of **different** stock-sync tasks of the same (channel, product) are last-writer-wins;
  acceptable because every push sends the current stock (log 014). Say in the report whether a test covers it.
- `SystemEventLog` writes (`processing.failed`, `order.insufficient_stock`, …) stay exactly as they are; alert opening
  and resolution are not logged there (the table is the record). Acknowledge **is** audited (below).
- Any other path that changes stock or a threshold must be named in the report; there should be none (no release path
  exists yet — record in the log that a future release/cancel path must call the reconcile).

### Backfill (in the migration)
- Open `LowStock` alerts for every product that is low now (shared rule).
- Open `StockSyncFailed` alerts for every (channel, product) whose **newest** `StockSync` task is `Failed`, and
  `ProcessingFailed` alerts for every `Failed` task of another type; `FirstOccurredAt`/`LastOccurredAt` = the task's
  `UpdatedAt`, `ErrorCode` from its last attempt, `OccurrenceCount` 1.
- The backfill SQL lives in one constant used by the migration; an integration test runs that constant against seeded
  rows and checks the result (the test database is migrated empty, so the migration alone proves nothing).
- No `InsufficientStock` backfill (it is tied to future imports; old rejections are history in `SystemEventLog`).

## Part C — API

### `GET /api/alerts` (AnyStaff)
- Paging contract of the project (`page`, `pageSize`, `sortBy` whitelist `lastOccurredAt` (default, desc),
  `firstOccurredAt`, `occurrenceCount`, `sortDir`); filters: `status` (repeatable; **default = `Open` + `Acknowledged`**),
  `type` (repeatable), `productId`, `channelId`, `from`/`to` on `LastOccurredAt` (inclusive, ISO 8601 with offset,
  ≤ 92 days, same validation style as the dashboard). camelCase keys, localised messages for every new validation text.
- Item: `alertId`, `type`, `status`, `productId`, `productSku`, `productName`, `channelId`, `channelName`, `taskId`,
  `orderId`, `errorCode`, `firstOccurredAt`, `lastOccurredAt`, `occurrenceCount`, `acknowledgedAt`,
  `acknowledgedByEmail` (resolved in the same query, as `createdByEmail` on movements), `resolvedAt`; for `LowStock`
  also the **current** `availableStock` and `lowStockThreshold` (join, not a stored snapshot). No error message, no
  payload, no outbox data.
- One query for the page plus one count (no N+1).

### `GET /api/alerts/{alertId}` (AnyStaff)
Same item shape; 404 `alert_not_found`.

### `POST /api/alerts/{alertId}/acknowledge` (ManagerOrAdmin)
- `Open` → `Acknowledged` (conditional `UPDATE`, see Writer), `SystemEventLog` Info `alert.acknowledged`
  (`alertId`, `type`, actor) in the same transaction, `alert.changed` hint; returns 200 with the item.
- Already `Acknowledged` → 200 with the item, nothing written (idempotent). `Resolved` → 409 `alert_resolved`.
  Unknown → 404 `alert_not_found`. Staff → 403.
- New `errorCode`s get client translations later (018); list them in the report.

### Dashboard summary
`alerts` section in `GET /api/dashboard/summary` (current, ignores the range): `open`, `acknowledged`, and `byType`
(every `AlertType`, zeros included: `type`, `open`, `acknowledged`). One aggregate statement. README and the `.http`
file updated.

## Tests (required)
Unit: keys per type; validator of the list query (defaults, repeatable filters, range, sort whitelist, keys); hint
factories and payload.
Integration (Testcontainers):
1. **Low stock:** product created → `LowStock` Open + `low_stock.created`; import above the threshold → Resolved; order
   reserving down to the threshold (inclusive) → a **new** Open row; threshold lowered below the stock → Resolved;
   raised again → new Open; an unchanged low state across further reservations → still one row, no hint. Concurrent
   reservations of the same product (≥ 10 parallel) crossing the threshold → every reservation succeeds (no 23505, no
   retry caused by the alert write) and exactly one unresolved `LowStock` row exists.
2. **Insufficient stock:** order exceeding availability → `InsufficientStock` for the short product only (multi-item
   order with one short item); second rejected order → same row, `OccurrenceCount` 2, latest `OrderId`; import of that
   product → Resolved; import of another product → unchanged.
3. **Stock sync:** final failure (`SIM-PERM-`) → `StockSyncFailed` Open with channel, product, task, `errorCode`; a second
   failed task for the same (channel, product) → refresh; a succeeding task for the same pair → Resolved; for another
   channel → unchanged.
4. **Order processing:** final failure of an `OrderProcessing` task → `ProcessingFailed`; manual retry → still Open;
   retried task succeeds → Resolved.
5. **Transactions:** a rolled-back import / reservation leaves no alert and sends no alert hint; the writer throws without
   an explicit transaction.
6. **API:** list defaults (resolved rows hidden), every filter, sort, paging, `acknowledgedByEmail`, current stock on
   `LowStock` items; get 404; acknowledge as Manager → 200 + audit event + hint, again → 200 unchanged, on Resolved → 409,
   as Staff → 403, anonymous → 401; summary `alerts` numbers incl. zeros.
7. **Backfill constant** against seeded rows (low products, newest-task-failed vs older-failed-then-succeeded pairs,
   failed order task).
8. Existing tests green without modification (if one must change — e.g. a hint count in a realtime test — deviation with
   reason). Mutation spot checks (report them): reconcile skipped in reserve; refresh that resets `Acknowledged`; resolve
   keyed by task instead of (channel, product) for stock sync.

## Acceptance criteria
- [ ] Part A: the A1 numbers (worker measurement, slow-test list and split map, 5 green runs with wall times) and the A2
      list of changed calls in the report; self-test green.
- [ ] Backend build 0 warnings, all tests pass; frontend changes limited to Part A (state it); typecheck, lint, build clean.
- [ ] Migration `Alerts` (schema + backfill); applying it with the migrator role listed under "Manual steps".
- [ ] `scripts/smoke/task-017.ps1` (API and Workers in Development, ASCII, `_common.ps1`, strict `Assert-Step`): smoke
      Manager and Staff; product with threshold 5 → `LowStock` Open; import 10 → Resolved; order of 6 on `website` →
      `LowStock` Open again; Staff acknowledge → 403; Manager acknowledge → `Acknowledged`, again → 200; import 10 →
      Resolved; acknowledge it → 409; order of 100 → `InsufficientStock` Open, import → Resolved; mapping on `website`
      with external SKU `SIM-PERM-…` → `StockSyncFailed` Open (wait for the task to fail), change the external SKU to a
      normal one → initial sync succeeds → Resolved; summary `alerts` reflects the open count; default list hides resolved
      rows; lock the smoke users. Run it and paste the table.
- [ ] README: alerts section (types, keys, lifecycle, who acknowledges, hints `alert.changed` / `low_stock.created`,
      endpoints, summary section); `.http` samples.
- [ ] Report: the doc deviations (below) confirmed or corrected; the list of call sites as implemented; which statements
      run in the reserve transaction now (count per order).

## Deviations from the architecture document introduced by this task
15. `Alerts` table (the ERD has no alert entity).
16. `GET /api/alerts/{alertId}` and `POST /api/alerts/{alertId}/acknowledge` (doc §11 lists only `GET /api/alerts`).
17. Hint `alert.changed` (in addition to the doc's `low_stock.created`).

## Out of scope
Alerts page and the `alert.changed` / `low_stock.created` → tag mapping in the frontend (018; until then the client
ignores the two types — unknown hint types map to nothing), alerts for outbox `Failed` rows or DLQ contents, e-mail or other
notification of alerts (024), purge/retention of resolved alerts (026), alert thresholds other than the product's
`LowStockThreshold`, manual resolve or reopen, a stock release/cancel path.

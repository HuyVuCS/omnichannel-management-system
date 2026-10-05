# Task 022 — Reconciliation Worker: find channel stock drift, create stock-sync tasks; simulated channel-side stock; Part A (review 021)

Status: Open
Written by: Cowork, 2026-10-05
Depends on: 021 (accepted)

## Context
Doc §8.5 ("Định kỳ đọc central inventory và trạng thái channel; nếu phát hiện lệch, tạo StockSync task mới.
Reconciliation không tự ý sửa Source of Truth nếu chưa có rule nghiệp vụ."), §13 ("Stock lệch → Reconciliation tạo sync
task"), §4 (Workers: … Reconciliation), §15 step 13, §1.2 (PostgreSQL is the source of truth; one failing channel must
not stop the others); CLAUDE.md.

**Decisions (user, 2026-10-05):** the simulated channels keep their channel-side stock **in a PostgreSQL table**
(written by an accepted push, read by the new read call); a reconciliation run is **periodic only** (no "run now"
endpoint). Every other decision of this task is in the implementation log, "Task 022 plan" — read it first; doc
deviations 26–28 are listed there.

Why it matters beyond the doc: since 014, a long channel outage ends its stock-sync tasks in `Failed` (the open circuit
counts toward the attempt budget) and the log names reconciliation as the recovery path (log "Task 014 result", "Open
circuit vs attempt budget"). The simulator was stateless, so "what does the channel show?" had no answer — Part B gives
it one; Part C uses it.

Read first: implementation log "Task 022 plan", "Task 014 plan" and "Task 014 result", "Task 017 plan" (how a
`StockSyncFailed` alert ends), "Task 018b result" (allowlist; "Successful orders": no event for the normal flow),
"Task 020 plan" (a periodic job in the Workers host); review 021; backend `IChannelStockClient.cs`,
`ResilientChannelStockClient.cs`, `SimulatedChannelStockClient.cs`, `ChannelSimulationOptions.cs`,
`StockSyncRequests.cs`, `StockSyncService.cs` (`CompleteAsync` resolves the pair's alert), `StockSyncMessageHandler.cs`,
`ChannelService.cs` (initial sync), `ProcessingTaskService.StockSyncRetryMessageAsync`, `RabbitMqTopology.StockSyncQueueOf`,
`ReportExportCleanupService.cs` / `ReportExportCleanup.cs`, `WorkerHostSetup.cs`, `SystemEventDetails.cs`,
`RealtimeHintInterceptor`, `ProcessingErrorClassifier`; tests `Integration/Workers/WorkersFixture.cs`,
`RecordingChannelClient.cs`, `StockSyncWorkerTests.cs`, `QueryShapeTests.cs`; frontend `features/logs/eventSummary.ts`,
`systemEventsApi.ts`, `test/systemEvents.ts`, `app/navigation.tsx`, `app/router.tsx`; `scripts/smoke/_common.ps1`,
`task-014.ps1`; README §6, §7, "System events".

**Stop and report (`Blocked`)** if: a run would have to write anything other than `ProcessingTasks`, `OutboxEvents` and
one `SystemEventLogs` row (the source of truth stays untouched — no rule allows reconciliation to change it); the app
role cannot take a PostgreSQL advisory lock; the migration needs anything but additions; the candidate query cannot be
made acceptable (C4 `EXPLAIN`) without a change to an existing table; or a decision is needed that is not in this file
or the log.

## Part A — Follow-ups of review 021 (one commit, first)

### A1. Remove the placeholder page (P3, review 021 question 2)
`shared/components/ComingSoon.tsx`, `NavigationItem.plannedTask`, the fallback in `router.tsx` (every menu item has a
built page; a menu item without one is a programming error — make `builtPages` typed so that a missing page fails the
typecheck, or keep a runtime guard that throws in development; say which), and the `comingSoon` blocks of `vi.json` /
`en.json` (parity kept). Tests that referred to them are updated.

### A2. README §4 "Database migrations" (P3, review 021)
State both ways: the command as today, and that Claude Code applies additive migrations itself as the migrator on
development, under the guardrails of `docs/tasks/README.md` "Development steps" (link), with
`OMNICHANNEL_MIGRATOR_CONNECTION` set once by the user. Nothing else in the section changes.

### A3. Load-sensitive tests (P3, review 021 finding 1)
- README "Frontend", testing part: the pattern to avoid — role-and-name queries over large tables inside `waitFor`
  (slow under load; use a narrow container, `within`, or a DOM query on a stable attribute) — and the rule for slow
  runs: the Browser pane closed and the machine otherwise idle; a test red in such a run is not attributed to load
  without a second run of that file alone (both results in the report).
- Apply the pattern to the first test of `productImages.test.tsx` *if* it is the cause of its red in 021's slow run 2
  (say what you found). No other test is touched for this item.

## Part B — Channel-side stock (read contract + simulator state)

### B1. Read contract
- `IChannelStockClient.ReadAvailableStockAsync(ChannelStockQuery query, CancellationToken)` →
  `IReadOnlyList<ChannelStockReading>`:
  - `ChannelStockQuery(string ChannelSlug, IReadOnlyList<ChannelStockItem> Items)`; `ChannelStockItem(ProductId, Sku,
    ExternalProductId, ExternalSku)`; at most `ChannelStockQuery.MaxItems` = 100 items (argument check).
  - `ChannelStockReading(ProductId, int? AvailableStock)` — one per requested item, in any order; `null` = the channel
    holds no stock value for that product (it never received one).
  - A failed call throws `ChannelApiException` with the existing codes (timeout / unavailable / rate limited / circuit
    open → transient; rejected → permanent). No per-item errors.
- `ResilientChannelStockClient` runs reads through the **same** per-channel pipeline as pushes (timeout + circuit
  breaker): a read failure says the same about the channel's health as a push failure. No retry inside (as for pushes).
- `RecordingChannelClient` (tests) gets the read side (scripted readings / failures, recorded calls).

### B2. Simulated channel-side stock — table `SimulatedChannelStocks` (migration)
**Decision (user, 2026-10-05):** the simulator's channel-side state is persisted in PostgreSQL.
- Entity/table `SimulatedChannelStocks`: `ChannelSlug` varchar(64), `ExternalKey` varchar(140), PK
  (`ChannelSlug`, `ExternalKey`); `AvailableStock` int not null (CHECK ≥ 0); `LastRequestId` varchar(100) not null;
  `UpdatedAt` timestamptz not null. No FK, no audit fields: it is **the channel's** data, keyed the way a channel knows a
  product, not business data of this system. `ExternalKey` = `id:` + external product id when the mapping has one,
  otherwise `sku:` + external SKU (one helper, used for writing and reading).
- Additive migration `SimulatedChannelStocks`, **no backfill**: until a push reaches it, the simulated channel holds no
  value for a mapping. On development the first reconciliation run therefore creates one stock-sync task per Active
  mapping (bounded by `MaxTasksPerRun`) — that is the truth of a channel that never stored anything; README says so.
- `SimulatedChannelStockClient`:
  - an **accepted** push upserts the row (one `INSERT … ON CONFLICT DO UPDATE` statement, last write wins) — after the
    mode / SKU-rule decision and the latency, never for a push that fails, times out or is cancelled;
  - `ReadAvailableStockAsync`: the configured mode and latency apply as for a push (`TransientError` →
    `channel_unavailable`, `PermanentError` → `channel_rejected`, `Timeout` → never answers); otherwise one `SELECT`
    for the requested keys, missing rows → `null`. SKU rules do not apply to reads;
  - new Development-only SKU rule (`SkuRules` on): external SKU prefix **`SIM-LOST-`** — the push is accepted and logged
    ("accepted, not stored") but the row is not written: a channel that loses updates, i.e. permanent drift for the
    demo and the smoke script;
  - database access in a scope of its own, never inside a transaction of the caller (the adapter stands for an
    external system); a database failure of the simulator surfaces as `channel_unavailable` (transient), its exception
    logged at Warning without connection details.
- Nothing outside `Infrastructure/ExternalChannels` (and the migration / model configuration) knows the table. A real
  adapter replaces the simulator without touching it; the table then stays empty.

## Part C — Reconciliation

### C1. Options `Reconciliation` (Workers host, validator + `ValidateOnStart`)
`Enabled` (true; false = the service does not run), `Interval` (15 min; ≥ 10 s), `BatchSize` (100;
1..`ChannelStockQuery.MaxItems`), `MaxTasksPerRun` (500; ≥ 1). `appsettings.Development.json` of the Workers:
`Interval` 1 min. First round after `min(Interval, 60 s)` (the pattern of `ReportExportCleanupService`).

### C2. One run (`StockReconciliation` in Infrastructure, run by `ReconciliationService : BackgroundService` in Workers)
1. **One run at a time across instances:** `pg_try_advisory_lock(<named constant>)` on a dedicated connection held for
   the run, released in `finally` (closing the connection releases it too). Not acquired → the round is skipped (Debug
   log). No new table, no new infrastructure.
2. `runId` = `Guid.CreateVersion7()`; correlation id of the run **`reconciliation-{runId:N}`** (valid for the API's
   correlation-id rule, ≤ 100) — set in the scope's `ICorrelationContext`, on every task and message of the run and on
   its system event, so the run can be followed on the Sync & Processing and Logs pages.
3. **Candidates** (no transaction, no lock), per channel, keyset-paged in batches of `BatchSize`: Active mappings of
   Active channels whose slug has a stock-sync queue (`RabbitMqTopology.StockSyncQueueOf`), with SKU, external ids,
   the current `AvailableStock`, and per pair:
   - `hasUnfinishedSync`: a `StockSync` task of the pair in `Pending` / `InProgress` / `RetryScheduled` → **skipped**
     (that task pushes the current stock anyway);
   - `lastFailedPermanently`: the pair's most recently finished `StockSync` task (`Succeeded`/`Failed`, latest
     `UpdatedAt` — the definition of the 017 backfill) is `Failed` and its last attempt's error code is
     `channel_rejected` → **skipped** (a new task would fail the same way, every round; the pair's
     `StockSyncFailed` alert is open; changing the mapping starts a new sync, a manual retry is possible). A final
     failure with a transient code (`channel_circuit_open`, `channel_timeout`, `channel_unavailable`,
     `channel_rate_limited`, database errors) is **not** skipped — this is the recovery path of long outages (log
     014, "Open circuit vs attempt budget").
4. **Channel read**, outside any transaction, one call per batch of the remaining pairs. A failed read
   (`ChannelApiException` of any code) ends this channel for the run (its remaining batches are not read; counted as
   unreadable with the error code); the other channels go on.
5. **Drift** = the reading is `null` or differs from the central `AvailableStock` read in step 3. Races are accepted:
   a false drift costs one push of the current value (every stock-sync push sends the value of that moment), a missed
   one is found by the next run. One pure function decides (unit-tested).
6. **Tasks:** per batch, one transaction: for every drifted pair a new `StockSync` task + directed `stock.sync.requested`
   to the channel's queue (`StockSyncRequests.NewTask`, extended with the idempotency key
   `reconcile:{runId:N}:{channelId:N}:{productId:N}`), until `MaxTasksPerRun` tasks were created in the run; the rest is
   counted as deferred (the next run finds it again). The realtime hint comes from the existing interceptor.
7. **Never modifies the source of truth:** the run writes `ProcessingTasks`, `OutboxEvents` and (C3) one
   `SystemEventLogs` row — nothing else (no inventory, product, mapping, order, movement, alert).
8. Cancellation (host stopping) ends the run between steps; a batch transaction is committed whole or not at all.
   A database error ends the run (Error log); the next round starts over.
9. Serilog: one Information line per run (run id, counts, duration; `WorkerName` = `ReconciliationWorker`,
   `WorkerInstanceId`).

### C3. System event `reconciliation.completed`
Written once at the end of a run **only when it created a task or could not read a channel** (a run that finds
everything in sync writes nothing — `SystemEventLog` records exceptions, log 018b "Successful orders"). Level Warning,
correlation id of the run, `WorkerName` `ReconciliationWorker`. Payload, all fields in the allowlist
(`SystemEventDetails`): `runId`, `checked`, `inSync`, `drifted`, `tasksCreated`, `deferred`, `skippedUnfinished`,
`skippedRejected`, `unreadable` (list of `{ channel, errorCode, pairs }`). Event-type constant in Application.

### C4. Tests (required)
Unit: drift decision (equal, different, `null`); skip rules; options validator; the key and correlation-id formats fit
their columns (200 / 100).
Integration (Testcontainers, the Workers fixture):
- Simulator: an accepted push stores the value (and a second push replaces it); `TransientError`, `PermanentError`,
  `Timeout`, `SIM-PERM-`, `SIM-TEMP-` (attempt 1) store nothing; `SIM-LOST-` is accepted and stores nothing; a read
  returns stored values and `null`; read modes through the resilient client (`channel_unavailable`, `channel_timeout`).
- Reconciliation, one fixture with website / pos / digital-platform, a channel without a queue, an Inactive channel,
  an Inactive mapping: in sync → no task; value differs → exactly one task per pair with the run's correlation id,
  `Pending`, channel and product set, one directed message with the channel's queue as routing key; no value → task;
  unfinished task → none; newest task `Failed` with `channel_rejected` → none; newest task `Failed` with
  `channel_circuit_open` → task; no task for the channel without a queue, the Inactive channel, the Inactive mapping;
  one channel unreadable → no task for it, the others reconciled, the event lists it; `MaxTasksPerRun` → exactly that
  many tasks, `deferred` counted; `BatchSize` → the number of read calls; a clean run writes no event, a run with
  drift writes exactly one with the counts; **source of truth untouched** (rows of `Inventories`, `Products`,
  `ChannelProducts`, `Orders`, `OrderItems`, `InventoryMovements`, `Alerts` identical before and after, `xmin`
  included); two runs started at the same time → one works, the other skips (advisory lock).
- End to end (Stock Sync Worker running): drift → task → push → the simulated row holds the central value → next run
  creates nothing; the pair's `StockSyncFailed` alert is resolved by the reconciliation task's success; `SIM-LOST-` →
  a task in every run.
- Query shape: `EXPLAIN` of the candidate query on a volume like 1,000 mappings / 50,000 stock-sync tasks; an index only
  if the plan needs one (then in the same migration, reported with the plan).
- Frontend (C5): the summary line in vi and en, with and without unreadable channels and with `deferred`.
- Mutations: about ten hand-made mutants of the drift decision, the skip rules, the channel filter and the cap —
  each killed by a test (list in the report).

### C5. Logs/Audit label (frontend)
`reconciliation.completed` in `EVENT_TYPES` with a one-line summary in vi/en: tasks created, drifted / checked, and the
unreadable channels by name with the error-code label (e.g. "12 of 340 products differ · 12 sync tasks created ·
not readable: POS (channel unavailable)"); `deferred` > 0 adds "N left for the next run". The Logs filter test counts
one more type; `test/systemEvents.ts` gets a sample. No other frontend change in Part C.

### C6. README
New section after §7: "Reconciliation" — what a run does and never does, the options, the skip rules, the
`reconciliation-{runId}` correlation id (find a run's tasks on Sync & Processing, its event on Logs/Audit), the
simulator's table and the `SIM-LOST-` rule, the first run after the migration (one task per Active mapping), and how
to see drift in development (map a product with an external SKU starting `SIM-LOST-`). §7 gets one line pointing to it.

### C7. Smoke script `scripts/smoke/task-022.ps1`
Strict `Assert-Step`, `Start-Smoke` + `Start-SmokeWorkers` (a Workers host in Development: SKU rules on, interval
1 min — a running host of an older build fails the first wait with a clear message). Smoke Manager (and the users the
pattern needs), random suffix:
1. Product `SMOKE-022-<n>` + stock import of 10; mapping on `pos` with external SKU `SIM-LOST-<n>` → 201; its initial
   sync task `Succeeded` (accepted, not stored).
2. Within 150 s a `StockSync` task of (pos, product) whose correlation id starts with `reconciliation-` appears and
   ends `Succeeded`; `GET /api/system-events?eventType=reconciliation.completed&correlationId=<that id>` → one event,
   level `Warning`, `tasksCreated` ≥ 1.
3. Mapping changed to external SKU `SMOKE-022-<n>` → its initial sync `Succeeded` (stored now); during the next 150 s
   **no** task with a `reconciliation-` correlation id is created for the pair.
4. Inventory of the product unchanged (total 10, reserved 0) — reconciliation touched no stock.
5. Clean-up: the mapping set `Inactive` (no permanent drift left on development); smoke users locked.
Then `Invoke-FrontendChecks`.

## Acceptance criteria
- [ ] Part A in its own commit, first; Parts B–C may share commits (migration in the commit that adds the entity).
- [ ] Backend build 0 warnings, all tests pass (C4 complete); mutation list in the report.
- [ ] Migration `SimulatedChannelStocks` additive only; applied on a throwaway database by the tests, then on
      **development** as the migrator (Development steps of `docs/tasks/README.md`: `migrations list` shows exactly
      this one pending, then `database update`).
- [ ] Frontend typecheck, lint, build pass; the five-run series is **not** required (no new page, no new asynchronous
      behaviour): **two consecutive full `npm test` runs and one full `npm run test:slow`**, all green, under A3's
      conditions; first-load set before/after.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity.
- [ ] `scripts/smoke/task-022.ps1` on development after the migration; table in the report. Also report what the first
      reconciliation run on development did (the event's counts: tasks created for the existing mappings, pairs
      skipped as rejected).
- [ ] README: C6, A2, A3.
- [ ] "Development steps (applied)" in the report; "Manual steps for the user": none, or only a step that could not be
      applied, with the reason.

## Out of scope
A "run now" endpoint or button (user decision); reconciliation of anything but stock (orders, prices); changing
central stock from channel values (no business rule — doc §8.5); resolving or opening alerts from a run (the
`StockSyncFailed` alert still ends by a succeeded task of the pair); per-pair "last reconciled" bookkeeping or a
fairness order beyond the cap; a channel-state view in the UI; real channel adapters; cleaning
`SimulatedChannelStocks` of rows of deleted or changed mappings; dashboard numbers for reconciliation.

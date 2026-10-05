# Report 022 — Reconciliation Worker: find channel stock drift, create stock-sync tasks; simulated channel-side stock; Part A (review 021)

Status: Done
Commit: `5c8d3c1` (Part A), `c8d6cad` (Parts B–C); this report in its own commit

## Summary
The Workers host now reconciles channel stock on a timer: for every Active mapping of every Active, synced channel it
compares the central `AvailableStock` with what the channel shows (new batch read on `IChannelStockClient`, through the
same timeout and circuit breaker as pushes) and creates a `StockSync` task + directed `stock.sync.requested` where the
value differs or is missing. A run writes only `ProcessingTasks`, `OutboxEvents` and at most one `SystemEventLogs` row
(`reconciliation.completed`); a test compares every other table row for row, `xmin` included. The simulated channels
keep the value of every accepted push in `SimulatedChannelStocks` (additive migration, no backfill) and answer reads
from it; `SIM-LOST-` gives permanent drift for demos. Part A removed the placeholder page (a menu item without a page
now fails the typecheck) and added the two README items. Migration applied on development by me; smoke 23/23 there.

## Files changed
**Part A (`5c8d3c1`)**
- `frontend/src/app/navigation.tsx` — menu items `as const`; `NavigationPath` = the union of the menu's paths; `plannedTask` gone.
- `frontend/src/app/router.tsx` — `menuPages: Record<Exclude<NavigationPath, '/'>, …>`; no fallback, no `ComingSoon`.
- `frontend/src/shared/components/ComingSoon.tsx` — deleted. `frontend/src/shared/i18n/vi.json`, `en.json` — `comingSoon` blocks removed (parity kept).
- `README.md` — §4 both ways of applying a migration (A2); "Frontend": pages line without the placeholder, the pattern to avoid and the conditions of a slow run (A3).

**Parts B–C (`c8d6cad`)**
- `backend/src/Omnichannel.Application/Channels/IChannelStockClient.cs` — `ReadAvailableStockAsync`, `ChannelStockQuery` (`MaxItems` 100, argument checks), `ChannelStockItem`, `ChannelStockReading`.
- `backend/src/Omnichannel.Application/Channels/StockReconciliationContracts.cs` — new: `ReconciliationOptions` + rules, `ReconciliationEvents.Completed`, `ReconciliationIds` (correlation id, idempotency key), `StockReconciliationRules` (`IsDrift`, `SkipOf`, `TasksAllowed`), `ReconciliationRunResult`, `UnreadableChannel`.
- `backend/src/Omnichannel.Application/SystemEvents/SystemEventDetails.cs` — allowlist entry of the new event.
- `backend/src/Omnichannel.Infrastructure/ExternalChannels/SimulatedChannelStock.cs` — new: the entity and the one key rule (`id:` / `sku:`).
- `…/ExternalChannels/SimulatedChannelStockStore.cs` — new: `ISimulatedChannelStockStore` + PostgreSQL store (own connection per call, one statement each: upsert / `= ANY`).
- `…/ExternalChannels/SimulatedChannelStockClient.cs` — stores accepted pushes, read side, `SIM-LOST-`, store failure → `channel_unavailable` (Warning without the exception).
- `…/ExternalChannels/ResilientChannelStockClient.cs` — reads through the same per-channel pipeline (one generic `ExecuteAsync`).
- `…/ExternalChannels/ChannelSimulationOptions.cs`, `ExternalChannelsServiceCollectionExtensions.cs` — `LostUpdateSkuPrefix`; store registration.
- `…/Persistence/Configurations/SimulatedChannelStockConfiguration.cs`, `…/Migrations/20261005122743_SimulatedChannelStocks*.cs`, model snapshot — the table.
- `…/Persistence/Channels/StockReconciliation.cs` — new: one run (advisory lock, candidate query, channel read, tasks per batch, event).
- `…/Persistence/Channels/ReconciliationOptionsValidator.cs` — new. `…/Channels/StockSyncRequests.cs` — `NewTask` takes an idempotency key.
- `backend/src/Omnichannel.Workers/ReconciliationWorker/ReconciliationService.cs` — new background service. `WorkerHostSetup.cs`, `Program.cs` (comment), `appsettings.Development.json` (`Reconciliation:Interval` 1 min).
- Tests: `Workers/StockReconciliationRulesTests.cs` (new), `Workers/ChannelStockClientTests.cs`, `Workers/MessageRejectionTests.cs`, `SystemEvents/SystemEventDetailsTests.cs`, `Integration/SystemEventEndpointsTests.cs`, `Integration/Workers/SimulatedChannelStockTests.cs` (new), `ReconciliationTests.cs` + `ReconciliationTests.EndToEnd.cs` (new), `ReconciliationQueryShapeTests.cs` (new), `RecordingChannelClient.cs` (read side), `WorkersFixture.cs` (isolated database, reconciliation off by default), `CapturingLoggerProvider.cs` (keeps the exception).
- Frontend: `features/logs/systemEventsApi.ts`, `eventSummary.ts`, `logs.test.tsx`, `test/systemEvents.ts`, `shared/i18n/vi.json`, `en.json`.
- `scripts/smoke/task-022.ps1` — new. `README.md` — section 8 "Reconciliation", §6 table, §7 pointer, event table, frontend note.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend, state of `c8d6cad`) | 0 errors, 0 warnings |
| `dotnet test` (backend, state of `c8d6cad`) | **1396 passed**, 0 failed (2 m 26 s) — 96 more than 021 |
| `dotnet test --filter FullyQualifiedName~Reconciliation` (run alone, with the plan) | 78 passed (55 s) |
| Mutation run, 23 hand-made mutants, one targeted test filter each | **23 of 23 killed**; files restored, build 0 after it |
| `npm run typecheck` / `npm run lint` (final state) | exit 0 / exit 0 |
| `npm test` × 2 consecutive (final state) | **2 of 2 green**, 454 tests, 30 files; 229 s, 222 s |
| `npm run test:slow` (full, final state) | **green at the first attempt**, 454 / 454 (239 s) |
| `npm run test:slow -- src/features/products/productImages.test.tsx` (A3, alone) | 36 / 36 green; first test 1.9 s |
| `npm run build` + `npm run measure` | first-load set **971.00 → 969.55 kB raw**, 318.40 → 317.94 kB gzip; main chunk 171.80 → 170.68 kB |
| Typecheck with a menu page removed / a page for a non-menu path added (A1, files restored) | exit 2 both times (`TS2741 Property '"/logs"' is missing`, `TS2353 … ''/nowhere'' does not exist`) |
| `dotnet ef migrations list` / `database update` on development | exactly `20261005122743_SimulatedChannelStocks (Pending)` → applied → none pending |
| `scripts\smoke\task-022.ps1` on development | **PASSED: all 23 checks** (table below) |

Conditions of the frontend runs (A3): no Browser pane was opened in this session; no API / Workers host, no backend
build or test and no dev server ran next to them (the development containers were up, as in every earlier series).
"Before" of the bundle is 021's number: `frontend/` did not change between `896d27c` and `74e4c4c`.

**Query shape (C4).** 1,000 Active mappings of website, 50,000 finished stock-sync tasks (50 per pair) and 50,000
attempts in a database of their own, `ANALYZE`d; `EXPLAIN (ANALYZE, BUFFERS)` of `StockReconciliation.CandidateSql`
for a page of 100. **No new index**: the existing ones carry it.
```
Limit  (actual time=0.538..6.020 rows=100 loops=1)  Buffers: shared hit=5873
  ->  Nested Loop Left Join
        ->  Merge Left Join … Index Scan using "IX_ChannelProducts_ChannelId_ProductId" on "ChannelProducts" m
              Index Cond: (("ChannelId" = …) AND ("ProductId" > …))   Filter: (("Status")::text = 'Active'::text)
            Index Scan using "PK_Products" …   Index Scan using "PK_Inventories" …
        ->  Limit (rows=1 loops=100) -> Sort (top-N heapsort)
              ->  Bitmap Heap Scan on "ProcessingTasks" t (rows=50 loops=100)
                    ->  Bitmap Index Scan on "IX_ProcessingTasks_ProductId"  Index Cond: ("ProductId" = m."ProductId")
        SubPlan 2 (unfinished)  ->  Index Scan using "IX_ProcessingTasks_TaskType_Status_CreatedAt" (rows=0 loops=1)
        SubPlan 3 (last error)  ->  Index Scan Backward using "IX_ProcessingAttempts_TaskId_AttemptNo" (rows=1 loops=100)
Planning Time: 0.629 ms   Execution Time: 6.066 ms
```
The test asserts: no sequential scan of `ProcessingTasks` or `ProcessingAttempts`, under 500 ms, and the page's content.

**Mutants** (each killed by the named test; `…Rules` = `StockReconciliationRulesTests`, the others `ReconciliationTests` / `SimulatedChannelStockTests`):
| # | Mutant | Killed by |
| --- | --- | --- |
| 01 | drift: a channel without a value is not drift | `A_channel_that_holds_no_value_for_a_product_is_drift…` |
| 02 | drift: only a lower channel value is drift | `A_differing_value_creates_exactly_one_pending_task…` |
| 03 | drift: equal values are drift too | `Pairs_that_are_in_sync_get_no_task…` |
| 04 | skip: an unfinished sync is not skipped | `A_pair_with_an_unfinished_sync_gets_no_task…` (3 of 3) |
| 05 | skip: every failed pair is skipped, whatever the code | `A_pair_whose_newest_sync_failed_for_good_with_a_transient_code_gets_a_task` (5 of 5) |
| 06 | skip: a rejected pair is not skipped | `A_pair_whose_newest_finished_sync_was_rejected_by_the_channel_gets_no_task` |
| 07 | skip: error code compared ignoring case | `…Rules` |
| 08 | query: `RetryScheduled` is not unfinished | `A_pair_with_an_unfinished_sync…` (that case) |
| 09 | query: an unfinished task of another channel counts | `A_pair_with_an_unfinished_sync…` |
| 10 | query: the finished task of another channel counts | `A_rejection_that_is_not_the_newest_finished_sync_does_not_hide_the_pair` |
| 11 | query: the oldest finished task decides | same |
| 12 | query: the first attempt decides the error code | `A_pair_whose_newest_finished_sync_was_rejected…` |
| 13 | filter: inactive mappings are looked at | `A_channel_without_a_queue_an_inactive_channel_and_an_inactive_mapping…` |
| 14 | filter: inactive channels are looked at | same |
| 15 | filter: channels without a queue are looked at | same |
| 16 | paging: `>=` instead of `>` in the keyset | `The_batch_size_decides_the_number_of_reads…` |
| 17 | cap: no cap | `A_run_creates_at_most_its_cap_of_tasks…` |
| 18 | cap: counted per batch, not per run | same |
| 19 | cap: one more than the cap | `A_cap_of_one_creates_one_task` |
| 20 | unreadable: the channel is asked again after a failed read | `A_channel_that_cannot_be_read_ends_for_the_run…` |
| 21 | event: written for every run | `Pairs_that_are_in_sync…a_clean_run_writes_nothing` |
| 22 | lock: every run gets it | `Of_two_runs_at_the_same_time_one_works_and_the_other_is_skipped` |
| 23 | simulator: `SIM-LOST-` stores the value | `The_lost_update_rule_accepts_the_push_and_stores_nothing` |

The first pass of the runner matched no test (`Name~…` is not a filter property of this test adapter): 22 "no test
matched", rerun with `FullyQualifiedName~…` — the table is the second pass.

**A3, `productImages.test.tsx`.** Its first test is **not** the pattern: its wait is `await screen.findByText('TEA-01')`
(a text query, 5 s limit) and its role-and-name queries run once, after the wait, outside any `waitFor`. What it waits
for is the first render of the file - the lazy page chunk is imported and transformed then. Alone in slow mode the
test took 1.9 s; in 021's run 2 (Browser pane open, 1.9 GB free) that first render outlasted the 5 s. The test was not
touched; no other test was touched for this item.

**Smoke, development** (`omnichannel_app` role, Workers host started by the script in Development):
```
OK    Step                                                             Expected                  Actual
True  01 create smoke manager                                          201                       201
True  02 create smoke product                                          201                       201
True  03 import 10 units                                               200                       200
True  04 the pos channel exists                                        True                      True
True  05 map on pos (SIM-LOST-)                                        201                       201
True  06 its initial sync task (accepted by the channel, not stored)   Succeeded                 Succeeded
True  07 a reconciliation run created a stock-sync task for (pos,      True                      True
      product) within 150 s
True  08 ... its correlation id is reconciliation-{run id}             True                      True
True  09 ... the task ends                                             Succeeded                 Succeeded
True  10 ... pushed by the StockSyncWorker through a directed message  StockSyncWorker           StockSyncWorker
                                                                       stock.sync.requested      stock.sync.requested
True  11 the task list filtered by the run finds the task              True                      True
True  12 one reconciliation.completed event of the run                 1                         1
True  13 ... level                                                     Warning                   Warning
True  14 ... it counts the task it created and the drift it found      True                      True
True  15 ... written by                                                ReconciliationWorker      ReconciliationWorker
True  16 change the external SKU (no SIM-LOST- any more)               200                       200
True  17 its initial sync task (stored by the channel now)             Succeeded                 Succeeded
True  18 no reconciliation task for the pair during the next 150 s     0                         0
True  19 stock of the product unchanged (total / reserved / available) 10 / 0 / 10               10 / 0 / 10
True  20 deactivate the smoke mapping                                  200                       200
True  21 lock the smoke manager                                        200                       200
True  23 npm run build                                                 0                         0
True  24 npm test                                                      0                         0
PASSED: all 23 checks.
```
(Step 22 is `npm ci`, recorded only when packages are missing.)

**The first reconciliation run on development** (Workers log of the smoke run, 60 s after the host started):
`1 checked, 0 in sync, 1 drifted, 1 sync tasks created, 0 deferred, 0 skipped (sync unfinished), 0 skipped (rejected
by the channel), 0 channels not readable; 132 ms`. The one pair is the smoke mapping: at that moment development had
**no other Active mapping** of an Active, synced channel (the counts of a run cover all of them), so the expected
"one task per existing mapping" was one task, and nothing was skipped as rejected. Runs 2 and 3 (after the SKU
change): `1 checked, 1 in sync, … 0 sync tasks created` (19 ms, 28 ms) - no event. The Workers log has no warning
and no error; the lock was taken as `omnichannel_app` (no extra privilege needed).

## Deviations from the task
1. **`Reconciliation:Interval` has an upper bound too (1 day).** The task names only "≥ 10 s"; a timer cannot wait much
   longer than a month, and a longer pause is a typing error.
2. **`unreadable[].channel` is the channel's name**, not its slug: the Logs page builds its line from the returned
   details alone (no lookups), and C5 wants the channel "by name". `pairs` = the pairs of that channel the run would
   have compared and did not: the failed batch and the later ones. Later batches are still paged from the database
   (so `skippedUnfinished` / `skippedRejected` stay complete) but the channel is not asked again.
3. **After the cap is reached the run keeps reading the channels** - it creates nothing more, but counts `deferred`
   exactly (the test asserts 7 products read for a cap of 5).
4. **The simulator's store uses plain Npgsql on a connection of its own**, not the `DbContext`: outside any caller's
   transaction by construction (a test rolls back around a push and the row stays). The entity
   (`Infrastructure/ExternalChannels`) and its configuration exist for the model and the migration; the context has no
   `DbSet` for it.
5. **The lock connection is not pooled** (`Pooling=false`) in addition to the explicit unlock in `finally`: closing it
   ends the session for certain.
6. **Options and rules live in `Application/Channels`** (the `ReportsOptions` pattern), the validator in
   Infrastructure; they are bound, and `StockReconciliation` is registered, by the Workers host only (the API has no
   channel client).
7. **The simulator logs "accepted" after the value is stored** (before: before anything was kept), so a store failure
   is never logged as an accepted push.
8. **Channel error-code labels are new** (`logs.channelErrors.*`, `CHANNEL_ERROR_CODES`): C5 asks for "the error-code
   label", and no label for these codes existed (the processing pages show them raw; unchanged).
9. **A2 also shows the one-time command** that sets the user-level variable (placeholder password); the rest of §4 is
   unchanged. **A1:** the compile-time guard was chosen (typed `menuPages`), no runtime guard.
10. **Existing tests changed:** `SystemEventDetailsTests` (17 constants, the class list), `SystemEventEndpointsTests`
    (a detail case for the new entry - its guard test demanded it), `ChannelStockClientTests` (the store parameter;
    the settings-file test replaces the store, as there is no database there), `MessageRejectionTests` (the double
    implements the read).
11. **Reconciliation tests use a database of their own per test** (`WorkersFixture.CreateIsolatedDatabaseAsync`): a run
    looks at every mapping, and the shared test database holds the mappings of every other test. Test hosts have
    `Reconciliation:Enabled=false` unless a test asks for the service.

## Blocked / questions for Cowork
None blocking. For the review:
1. **A run that is cancelled or fails after it created tasks writes no event** (its tasks carry the run's correlation
   id; the Error / shutdown is in the log). The task says "at the end of a run". Should an interrupted run still write
   its event?
2. **A read that fails with something that is no `ChannelApiException`** (an adapter bug, an unknown simulation mode)
   ends the whole run with an Error log, every round - the task names `ChannelApiException` only. Treat any failure of
   a read as "this channel is unreadable" (with which code)?
3. Deviations 2 and 3 (the meaning of `channel` / `pairs`; reading on after the cap) - accept, or stop reading at the
   cap and report `deferred` as "at least"?
4. **Two products of one channel mapped with the same external SKU and no external product id** share one key on the
   simulated channel (as on a real one): each push overwrites the other's value, so they drift against each other for
   good whenever their stocks differ. Nothing prevents such mappings today (only the external product id is unique).
   Leave it, or a unique index / validation in a later task?
5. **Development runs a reconciliation every minute now.** Older smoke scripts that leave Active mappings behind
   (014 leaves three) will get reconciliation tasks for them while a Workers host runs; `SIM-PERM-` pairs are skipped
   as rejected. Development had none when this task ran. Fine as it is?

## Development steps (applied)
1. Key helper `scripts\setup-object-storage.ps1`: not run - neither the script nor the storage keys changed.
2. `docker compose up -d`: not needed - no service definition changed (PostgreSQL, RabbitMQ and the object storage
   were up and healthy).
3. Migration, as the migrator, development database (connection from the user-level `OMNICHANNEL_MIGRATOR_CONNECTION`,
   set in the process environment by a scratch script that never prints it):
   - `dotnet ef migrations list -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api --no-build` → ten applied,
     **`20261005122743_SimulatedChannelStocks (Pending)`** - exactly this task's migration;
   - additive only (one `CreateTable` with its primary key and check constraint; no backfill); applied before on
     throwaway databases by the integration tests (`MigrateAsync` of every isolated database);
   - `dotnet ef database update … --no-build` → `Applying migration '20261005122743_SimulatedChannelStocks'. Done.`
   - `migrations list` again → nothing pending.
4. Hosts: none was running before the build; the smoke script started and stopped its own API and Workers host; none
   is running now (checked by process list).
5. `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-022.ps1` on development → PASSED, 23 of 23 (above).
   The smoke mapping is `Inactive` and the smoke manager is locked.

## Manual steps for the user
None.

## Out of scope / follow-ups
- Rows of `SimulatedChannelStocks` for changed or deactivated mappings stay (out of scope by the task); the smoke
  run left one on development (`pos`, `sku:SMOKE-022-…` - by the Workers log: one push "accepted", three "accepted,
  not stored"; the table itself was not queried).
- No real-browser check was made: the frontend change is one summary line of an existing page, covered by tests in
  both languages. The Logs page of development now has `reconciliation.completed` events to look at.
- `scripts/smoke/_common.ps1` has no helper for "tasks of a product": the task list has no product filter, so smoke
  022 pages through the channel's stock-sync tasks since its start. A `productId` filter on
  `GET /api/processing/tasks` would make this (and an operator's search) direct.

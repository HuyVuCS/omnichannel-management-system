# Review 022 — Reconciliation Worker; simulated channel-side stock; Part A (review 021)

Reviewer: Cowork, 2026-10-05
Verdict: **Accepted** — no P0–P2 finding. Three P3 follow-ups go into **023 Part A** (all backend, one with an additive
migration); two observations are recorded for 026. The five questions of the report are answered below.

## Checked
- Report and commits `5c8d3c1` (Part A), `c8d6cad` (Parts B–C), `cbfe86f` (report). One migration
  (`20261005122743_SimulatedChannelStocks`: one `CreateTable` with PK and check constraint, no backfill), no new package.
- Read in full: `StockReconciliation.cs` (lock, `CandidateSql`, run loop, task creation, event),
  `StockReconciliationContracts.cs` (options and rules, ids, `SkipOf`, `IsDrift`, `TasksAllowed`, result),
  `ReconciliationService.cs`, `SimulatedChannelStockStore.cs`; the diffs of `SimulatedChannelStockClient.cs`,
  `ResilientChannelStockClient.cs`, `navigation.tsx`, `router.tsx`, `eventSummary.ts`; `ChannelProductConfiguration`
  (indexes) for the keyset and question 4.
- Checked by Cowork against the code:
  - **Source of truth untouched.** The run's only writes are `ProcessingTasks` + `OutboxEvents` (one `SaveChanges`
    per page) and the one `SystemEventLogs` row; the simulator writes only its own table on a connection of its own.
    The test compares every other table row for row, `xmin` included.
  - **No transaction across a channel call.** Candidates are read without a transaction, the channel is read
    afterwards, tasks are written in a short transaction per page; the simulator's store never joins a caller's
    transaction (own connection; a test rolls back around a push and the row stays).
  - **One run at a time.** `pg_try_advisory_lock` on a non-pooled connection of its own, unlocked in `finally`,
    released by the closing connection otherwise; works as `omnichannel_app`. A lost lock connection could let a second
    instance start a run — the cost is duplicate tasks with different idempotency keys, i.e. one extra push each:
    harmless.
  - **Keyset paging** by `ProductId` within a channel is complete: `(ChannelId, ProductId)` is unique.
  - **Raw SQL** takes its values as parameters (`SqlQueryRaw` positional parameters; Npgsql `$n` in the store); nothing
    is concatenated.
  - **Skip rules** match the plan: unfinished sync → skipped; newest finished task (latest `UpdatedAt`, as the 017
    backfill) `Failed` with `channel_rejected` → skipped; transient final failures are not skipped. 23 of 23 mutants
    killed cover the rule, the query predicates, the filters, the cap and the lock.
  - **Simulator.** Only an accepted push is stored, after the mode / SKU decision and the latency; a store failure is
    answered as `channel_unavailable` without the exception text; reads honour the modes, not the SKU rules.
  - **Resilience.** Reads run through the same per-channel pipeline as pushes (timeout + breaker), so an unreadable
    channel and a failing push see the same breaker — intended.
  - **Frontend.** `menuPages` keyed by the menu's own paths makes a missing page a compile error (shown by the report's
    two typecheck probes); the event summary reads only the returned details.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1396/1396; frontend typecheck and lint clean, 2 of
  2 full runs green (454 tests) and the full slow run green at the first attempt under A3's conditions; first-load set
  971.00 → 969.55 kB raw; `EXPLAIN (ANALYZE)` of the candidate page at 1,000 mappings / 50,000 tasks 6 ms with index
  scans only (no new index); migration listed (only this one pending) and applied on development as the migrator;
  smoke 022 23/23 on development.
- **Doc deviations 26–28 confirmed.**

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `Interval` ≤ 1 day | Accepted. |
| 2 | `unreadable[].channel` is the name; `pairs` = the pairs not compared | Accepted — the Logs line needs no lookup; the event is a snapshot. The slug is not needed by any reader today. |
| 3 | After the cap the run keeps reading to count `deferred` exactly | Accepted — every run reads every pair anyway (that is what an in-sync check is); the cap protects the queues, not the channel. |
| 4 | Simulator store on plain Npgsql, entity only for the model | Accepted — outside any caller transaction by construction. |
| 5 | Lock connection not pooled | Accepted. |
| 6 | Options and rules in `Application/Channels`, bound by the Workers host only | Accepted (the `ReportsOptions` pattern). |
| 7 | "accepted" logged after the value is stored | Accepted. |
| 8 | New labels for channel error codes on the Logs page | Accepted. |
| 9 | A2 shows the one-time variable command; A1 compile-time guard | Accepted. |
| 10 | Existing tests adapted | Accepted. |
| 11 | Isolated database per reconciliation test; service off in test hosts by default | Accepted — a run looks at every mapping, so isolation is the only correct setup. |

## Questions of the report
1. **An interrupted run writes no event.** **Leave it.** The tasks it created carry the run's correlation id (visible on
   Sync & Processing), the failure or shutdown is in the Workers log, and the next run reports whatever is still
   drifted. A best-effort event on failure would mostly fail for the same reason the run did (database down).
2. **A read that fails with something other than `ChannelApiException` ends the whole run.** **Treat it as an
   unreadable channel** (023 A1): doc §1.2 — one failing channel must not stop the others, and today a typo in one
   channel's simulation mode stops reconciliation of every channel, every round. Code `unexpected` (the classifier's
   code for unknown failures), logged at **Error with the exception** (it is a bug, unlike a channel answer). Host
   shutdown (`OperationCanceledException` on the stopping token) still ends the run; a failure of the candidate query
   (database) still ends the run.
3. **Deviations 2 and 3.** Accepted as they are (table above).
4. **Two mappings of one channel with the same external SKU and no external product id.** **Prevent it** (023 A3): such
   mappings make two products write the same item on the channel — on a real channel as much as on the simulated one —
   and reconciliation would create two tasks every run for good. Same rule as the external product id: a partial
   unique index on `(ChannelId, ExternalSku) WHERE "ExternalProductId" IS NULL`, mapped to 409
   `external_sku_taken` on the field (registered in `ExpectedUniqueViolations`), checked against the development data
   before the migration (stop if a duplicate exists).
5. **Reconciliation every minute on development.** **Fine.** Pairs left Active by older smokes are synced once and then
   stay in sync; `SIM-PERM-` pairs are skipped; only `SIM-LOST-` pairs drift for good, and smoke 022 deactivates its
   own. README §8 explains how to switch it off (`Reconciliation:Enabled`).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **A non-`ChannelApiException` read failure stops the whole run** (question 2). | **023 A1.** Tests: a channel whose adapter throws `InvalidOperationException` → that channel unreadable with `unexpected`, the other channels reconciled, an Error log with the exception; shutdown during a read still ends the run without an event. |
| P3 | **An in-sync pair keeps an open `StockSyncFailed` alert.** The alert of a pair ends only with a `Succeeded` task of the pair (017). When the newest finished task `Failed` with a transient code (open circuit, timeout, database after the push) but the channel already shows the central value — the push landed before the failure was recorded, or the stock returned to the pushed value — the run finds no drift and creates no task, so the alert stays open although nothing is wrong. Log 014 promises recovery by reconciliation for exactly these pairs. | **023 A2:** a pair whose newest finished task `Failed` with a **transient** code gets one task even when it is in sync (counted separately: `failedRetried` in the result, the event and the Logs line), within the cap. Its success resolves the alert through the existing `CompleteAsync`; the run itself still writes no alert. Tests: in sync + transient final failure → one task, the alert `Resolved` after it succeeds (end to end); in sync + `channel_rejected` → skipped as today; in sync + newest `Succeeded` → nothing. |
| P3 | **Duplicate external SKU per channel** (question 4). | **023 A3.** |
| — | **A pair with a non-terminal task that never ends is never reconciled.** The skip rule trusts an unfinished task to push; a task whose message can never be delivered (e.g. its outbox row `Failed` after the publisher's attempts) stays `Pending` for good. The dashboard already counts failed outbox rows. | Accepted — recorded for **026**: detection of stale non-terminal tasks (age threshold per status) before reconciliation trusts them. |
| — | **No product filter on `GET /api/processing/tasks`** (report follow-up): smoke 022 pages through a channel's tasks; an operator looking for one product does the same. | Recorded for **026** (with the frontend filter). |

## Assessment
The task delivers the plan with the guarantees that matter: the source of truth is provably untouched, no transaction
spans a channel call, one run at a time across instances with nothing but PostgreSQL, and the skip rules keep a
permanently rejected pair from producing a failing task every round. The candidate query was measured at a realistic
volume instead of guessed, and the mutation list shows the tests guard the predicates that decide which pairs are
touched. The simulator's state is honest (only accepted pushes are kept, stored outside any caller transaction), and
`SIM-LOST-` makes drift demonstrable. The findings are at the edges: one failure class that is not isolated per
channel, one alert that the recovery path cannot reach when the channel is already right, and one mapping rule that
the new channel-side state exposed.

## Manual check
None for the user. Optional: the Logs/Audit page on development shows the `reconciliation.completed` events of the
smoke run (filter by event type).

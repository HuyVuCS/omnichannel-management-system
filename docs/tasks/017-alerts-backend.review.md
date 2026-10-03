# Review 017 — Alerts backend + frontend test stability

Reviewer: Cowork, 2026-10-03
Verdict: **Accepted** — no P0–P1 finding in the new code. One P1 **pre-existing** defect found by the task (form dialog
reopened while closing shows the previous record's values; question 1) goes to 018 Part A as its first item. Questions
2–6 decided below.

## Checked
- Report and commits `e13fe35`, `0f5a935`, `822e3ce`, `3fc799a` (Part A), `056e1a3` (Parts B, C), `dd5efb1` (two split
  tests), `e2bf1b8` (report). One migration (`20261002215102_Alerts`), no new dependency.
- Read in full: `AlertStore.cs`, `AlertBackfill.cs`, `AlertService.cs`, `AlertsController.cs`; the diffs of
  `InventoryImportService`, `OrderReservationService`, `ProductService`, `ProcessingAttemptStore`, `StockSyncService`,
  `InventoryPredicates`; `useResetFormOnOpen.ts`; `ChannelService.UpdateProductAsync` (for question 3).
- Checked by Cowork against the code (not only the report):
  - **One writer, one statement per write, inside the cause's transaction:** every `AlertStore` method calls
    `EnsureTransaction()` first; every call site runs after the `SaveChanges` that stores the referenced rows and before
    `CommitAsync`. `pg_notify` of the writer is sent on the same connection, only when a statement returned rows.
  - **Race-freedom:** open/refresh is `INSERT … ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved'` with the partial
    unique index as arbiter (a concurrent resolve makes PostgreSQL retry the speculative insert, which then inserts a new
    row — the intended "comes back after resolution" case). Low-stock statements run only under the inventory row locks
    (reserve, import, product update now locks the row `FOR UPDATE` before changing the threshold, create has a new row).
    Multi-row resolves lock in `DedupKey` order (deviation 8) — good, an import resolving several insufficient-stock
    alerts and a rejected multi-line order could otherwise deadlock.
  - **Status kept on refresh:** `DO UPDATE SET` does not touch `Status`; the mutant "refresh resets Acknowledged" is
    killed.
  - **Stock-sync resolution per (channel, product):** `CompleteAsync` resolves `stock_sync_failed:{channel}:{product}`
    only when this call completed the task (`completed`), not on a duplicate delivery.
  - **Rule in SQL text exists once** (`IsLowStockSql`, next to the LINQ predicate) and is pinned by a test on rows on both
    sides of the boundary; writer and backfill both use it.
  - **Backfill:** "newest" = most recently *finished* stock-sync task (deviation 7) — correct, a running task decides
    nothing; idempotent (`ON CONFLICT DO NOTHING`); tested by running the constant against seeded rows.
  - **API:** list = one count + one page statement (asserted by a test); `acknowledgedByEmail` by the same text join as
    `createdByEmail` (index work already planned for 026); acknowledge is a conditional `UPDATE … WHERE Status = 'Open'`
    + audit row in one transaction; no error message or payload in responses.
- **Verification:** the report's runs — backend 820/820, 0 warnings, 15/15 mutants killed; frontend typecheck, lint,
  build clean, first-load set 968 kB raw (unchanged); 5 of 5 full runs green in the second series; smoke `task-017.ps1`
  53/53 on a throwaway environment as `omnichannel_app`, `task-016b.ps1` 25/25 on development. **The user (2026-10-03)
  applied migration `Alerts` to the development database and ran the manual steps** (table not pasted). Not re-run by
  Cowork (no dotnet/npm in the user's VM).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | A1 order: tests split before the worker measurement | Accepted — the choice must hold for the suite as it is. |
| 2 | One slow test not split (single step, 6.3 s → 2.5 s by the fake clock) | Accepted. |
| 3 | Step numbers of six smoke scripts moved | Accepted. |
| 4 | `RealtimeHintProducerTests`: alert hint types added to the allowed list + one assertion | Accepted — the failed sync of that test now really announces its alert. |
| 5 | Three FK indexes by EF convention (`ChannelId`, `TaskId`, `OrderId`) | Accepted; `ChannelId` serves a filter. The other two: 026 with the other index work. |
| 6 | Backfill ids UUIDv4 | Accepted (PostgreSQL 17 has no v7 generator; ids are opaque). |
| 7 | Backfill "newest" = most recently finished stock-sync task | Accepted (see Checked). |
| 8 | Resolve locks rows in key order; upserts in key order | Accepted — prevents a real deadlock. |
| 9 | `AlertStore` and its records `public` | Accepted — needed by the tests; `AlertService` stays internal. |
| 10 | `status=99` refused by model binding under key `status` | Accepted. |
| 11 | Every sort field descending unless `sortDir=asc` | Accepted (most recent / most frequent first is what an operator wants). |
| 12 | Two split tests fixed after the first series (ended with a request in flight) | Accepted; the lesson is in the handoff gotchas ("a frontend test must end with its requests settled"). |
| 13 | `task-016b.ps1` run on the Part A state | Accepted. |

## Questions of the report
1. **Form dialog reopened while closing shows the previous record's values (P1, pre-existing since 016b's mechanism).**
   The trace in the report is convincing: the dialog library renders the content of a closing dialog one commit after
   its parent, so the parent's effect resets the form against the *previous* `initialValues`; AntD never applies
   `initialValues` again to a mounted form, and saving writes record A's values onto record B. Same defect class as
   016b deviation 1, so P1 even though the window is short (Cancel, then another record within the close animation).
   **Decision: 018 Part A1, first commit.** The reset moves inside the `<Form>` subtree (a small shared component using
   `Form.useFormInstance()`), so it always runs after the render that gave the form its new `initialValues`;
   `useResetFormOnOpen` is removed from all seven dialogs; `clearOnDestroy` stays. It must also reset when the dialog is
   reopened for the **same** record (edited, then Cancel, then reopened while closing) — a dependency on the record
   value alone does not see that, because the memoised content may never render the closed state. The 016b test becomes
   deterministic (repeated runs, numbers in the report) and the production-build browser check repeats the scenario.
2. **Insufficient-stock alert from a stale availability read.** Accepted as is (P3). The alert states a fact — an order
   *was* rejected for lack of stock —, it can be acknowledged, and the next import of the product resolves it. Closing
   the window needs `FOR SHARE` locks in the rejection path for a cosmetic gain. Recorded in the log; revisit if
   operators report stale insufficient-stock alerts.
3. **Stock-sync alert of a mapping deactivated afterwards.** **Decision (user, 2026-10-03): deactivating a mapping
   resolves the `StockSyncFailed` alert of its (channel, product)** in the same transaction; re-activating starts an
   initial sync that opens a new alert if it fails. Channel deactivation does not touch alerts (rare; re-activating a
   channel does not re-sync its mappings, so the alert keeps its meaning). → 018 Part A6. Known edge, accepted: an
   attempt already running when the mapping is deactivated can still fail finally and reopen the alert; a manual retry
   then completes without a push (015 Part A) and resolves it.
4. **Backfill opens many `SMOKE-*` low-stock alerts on development.** Expected; the alerts page filters by product and
   type. No action.
5. **`Complete-Smoke` cuts the `OK` column for long values.** → 018 Part A5: `OK` first, values never cut off the table.
6. **Every new product opens a low-stock alert.** As decided (017 plan). No action; bulk product creation does not exist.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P1 | Question 1 (pre-existing): stale values in a dialog reopened while closing → silent overwrite of another record. | 018 Part A1. |
| P3 | Question 3: alert of a deactivated mapping stays open. | 018 Part A6 (user's decision). |
| P3 | Question 5: smoke table drops the `OK` column. | 018 Part A5. |
| P3 | `IX_Alerts_TaskId`, `IX_Alerts_OrderId` serve nothing; `acknowledgedByEmail` uses the text join. | 026 (index work, with the `createdByEmail` lookup). |
| P3 | Question 2: stale-read insufficient-stock alert. | Accepted, recorded in the log. |

## Assessment
The alert design holds up under concurrency without a single extra lock in the hot path: the reserve transaction gains
three statements (two set-based reconcile statements per order, one resolve) and the reserve `UPDATE` is unchanged.
Everything that could drift — the low-stock rule in SQL, the backfill, the "status kept on refresh" rule, per-pair
resolution — is pinned by a test or a killed mutant. Part A delivered what review 016b asked for and, in doing so,
isolated the last source of red frontend runs to a real product defect rather than test noise.

## Manual check
Done by the user on 2026-10-03: migration `Alerts` applied with the migrator role, API and Workers restarted,
`task-017.ps1` run (reported as done; table not pasted).

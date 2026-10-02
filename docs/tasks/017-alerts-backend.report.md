# Report 017 — Alerts backend (low stock, insufficient stock, processing failures) + frontend test stability

Status: Done (manual step: apply the migration `Alerts` to the development database). **One finding in existing code
needs Cowork's eyes:** a form dialog reopened right after Cancel can show the previous record's values (question 1) -
it is also the only remaining source of red frontend runs I found.
Commit: `e13fe35` (A1 smoke helper), `0f5a935` (A2), `822e3ce` (A1 test split), `3fc799a` (A1 worker count),
`056e1a3` (Parts B and C), `dd5efb1` (A1 fix of two split tests), plus this report.

## Summary
- **Part A1:** the six smoke scripts that run npm now do it through one helper (`Invoke-FrontendChecks`) after stopping
  the hosts they started; the seven tests that took more than 5 s alone are split (11 more tests, slowest test now
  4.6 s); 4 workers stay (3 workers measured 13 % slower). `task-016b.ps1` green end to end. Five consecutive full
  runs green - in the **second** series: the first had two red runs caused by two of my split tests (fixed in
  `dd5efb1`) and one by an existing race in the form dialogs (question 1).
- **Part A2:** `Assert-Step` is strict (null / boolean / number by value / string case-sensitive; anything else throws).
  No existing call had to change; self-test 51/51.
- **Part B:** `Alerts` table (migration `Alerts` with backfill), `AlertStore` as the only writer - one SQL statement per
  alert write inside the transaction of the cause, hints `alert.changed` / `low_stock.created` sent with `pg_notify` in
  that transaction - and the eight call sites of the task.
- **Part C:** `GET /api/alerts`, `GET /api/alerts/{alertId}`, `POST /api/alerts/{alertId}/acknowledge`, `alerts` section
  of the dashboard summary, README section, `.http` samples, `scripts/smoke/task-017.ps1`.
- Backend 820/820 (752 + 68), 0 warnings; 15 of 15 mutants killed; smoke 53/53 on a throwaway environment running as
  `omnichannel_app`. Frontend changes are limited to Part A (four test files and the comment in `vite.config.ts`).

## Files changed
Backend
- `Omnichannel.Domain/Entities/Alert.cs`, `Enums/AlertType.cs`, `Enums/AlertStatus.cs` (new).
- `Omnichannel.Application/Alerts/AlertContracts.cs` (new: `AlertResponse`, `AlertListQuery`, `AlertErrorCodes`,
  `AlertEvents`, `AlertDedupKey`, `IAlertService`), `AlertListQueryValidator.cs` (new).
- `Omnichannel.Application/Realtime/RealtimeHints.cs` — event types `alert.changed`, `low_stock.created`; members
  `alertId`, `alertType`; factories `AlertChanged`, `LowStockCreated`.
- `Omnichannel.Application/Dashboard/DashboardContracts.cs` — `DashboardAlerts`, `DashboardAlertTypeCount`.
- `Omnichannel.Infrastructure/Persistence/Alerts/AlertStore.cs` (new, the writer), `AlertService.cs` (new: list, get,
  acknowledge), `AlertBackfill.cs` (new: the backfill SQL as one constant).
- `Persistence/Configurations/AlertConfiguration.cs` (new), `OmnichannelDbContext.cs` (`Alerts`),
  `Migrations/20261002215102_Alerts*.cs` (new), `OmnichannelDbContextModelSnapshot.cs`.
- `Persistence/Inventories/InventoryPredicates.cs` — `IsLowStockSql` (the rule as SQL text, next to the predicate).
- Call sites: `Inventories/InventoryImportService.cs`, `Orders/OrderReservationService.cs`,
  `Products/ProductService.cs`, `Processing/ProcessingAttemptStore.cs`, `Channels/StockSyncService.cs`.
- `Persistence/Dashboard/DashboardService.cs` (alerts aggregate), `DependencyInjection.cs`.
- `Omnichannel.Api/Controllers/AlertsController.cs` (new), `Omnichannel.Api.http`.
- Tests: `Omnichannel.Tests/Alerts/AlertUnitTests.cs` (new, 34), `Integration/AlertEndpointsTests.cs` (new, 20),
  `Integration/Workers/AlertLifecycleTests.cs` (new, 14), `Integration/Workers/RealtimeHintProducerTests.cs` (one
  assertion, deviation 4).

Frontend (Part A only)
- `features/orders/orderEntry.test.tsx`, `features/processing/processing.test.tsx`,
  `features/realtime/realtime.test.tsx`, `features/channels/channelDetail.test.tsx` — split tests.
- `vite.config.ts` — comment with the measurement; `maxWorkers` unchanged (4).

Other
- `scripts/smoke/_common.ps1` — `Invoke-Npm`, `Invoke-FrontendChecks`, strict `Assert-Step` (`Get-AssertKind`,
  `Test-AssertEqual`), `Stop-Smoke` waits for the stopped hosts to exit.
- `scripts/smoke/task-010.ps1`, `011`, `012`, `013`, `016`, `016b` — npm steps moved behind the clean-up, via the helper.
- `scripts/smoke/_assert-selftest.ps1`, `scripts/smoke/task-017.ps1` (new).
- `README.md` — section "Alerts", hint table, summary section, how alert hints are sent.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` (backend, final state) | 0 errors, 0 warnings |
| `dotnet test` (backend, final state) | **820 passed, 0 failed** (539 unit, 281 integration; 752 before) |
| Mutation spot checks (15 mutants, one targeted run each) | 15 killed, 0 survived (list below) |
| `dotnet ef migrations add Alerts` | generated; applied only to throwaway databases (see "Manual steps") |
| `npm run typecheck`, `npm run lint`, `npm run build` (final state) | clean |
| `npm run measure` | first-load set 968.23 kB raw / 317.42 kB gzip (016b: 968 / 317); main chunk 169.07 kB |
| each frontend test file alone, `--reporter=verbose` (before the split) | 281 tests; 7 slower than 5 s (list below) |
| the four changed test files alone (after the split) | 77 tests; slowest 4.6 s |
| full frontend suite, 3 × `--maxWorkers=4`, 3 × `--maxWorkers=3` | 6 of 6 green, 292/292 each (numbers below) |
| `npm test` × 5 consecutive, 1st series (after `3fc799a`) | 3 green; runs 3 and 5 red (details below) |
| `npm test` × 5 consecutive, 2nd series (final state, after `dd5efb1`) | **5 of 5 green**, 292/292 each |
| `products.test.tsx -t "reopened before its close animation"` × 12 | 11 green, 1 red (question 1) |
| `scripts\smoke\_assert-selftest.ps1` | PASSED: all 51 cases |
| `scripts\smoke\task-016b.ps1` (development environment, script started and stopped both hosts, state of the Part A commits) | **PASSED: all 25 checks**, incl. `npm run build` and `npm test` |
| `scripts\smoke\task-017.ps1` (throwaway environment) | **PASSED: all 53 checks** (two runs; the first, 51 checks, before two wide steps were split) |
| `scripts\smoke\task-009.ps1`, `task-014.ps1`, `task-015.ps1` (throwaway environment, strict `Assert-Step`) | PASSED: 22 / 38 / 27 checks |

Not executed: `task-010/011/012/013/016.ps1` (changed in A1, checked statically; they need the development environment
with the new migration, or run their npm checks next to hosts they did not start) and the smoke run on the development
database (the migration has to be applied first).

### A1 — worker count
PostgreSQL and RabbitMQ containers running, no API, Workers host, dev server or browser of mine; the user's VS Code
(C# Dev Kit) and the Claude desktop app were open, about 0.7–1.2 GB RAM free. Full `npx vitest run --maxWorkers=N`,
alternating, on the suite **after** the split (292 tests):

| Run | Workers | Wall time | Result |
| --- | --- | --- | --- |
| 1 | 4 | 207 s | 292/292 |
| 2 | 3 | 217 s | 292/292 |
| 3 | 4 | 183 s | 292/292 |
| 4 | 3 | 207 s | 292/292 |
| 5 | 4 | 178 s | 292/292 |
| 6 | 3 | 195 s | 292/292 |

Median 4 workers 183 s, 3 workers 207 s (+13 %). Rule of the task: 3 only when at most 5 % above 4 → **4 stays**; the
comment in `vite.config.ts` records date, numbers and reason.

Five consecutive `npm test` runs with the committed configuration (same conditions):

| Series | Run 1 | Run 2 | Run 3 | Run 4 | Run 5 |
| --- | --- | --- | --- | --- | --- |
| 1st (after `3fc799a`) | 186 s, 292/292 | 175 s, 292/292 | 174 s, **290/292** | 175 s, 292/292 | 175 s, **291/292** |
| 2nd (after `dd5efb1`, final) | 183 s, 292/292 | 170 s, 292/292 | 167 s, 292/292 | 176 s, 292/292 | 173 s, 292/292 |

The red runs of the 1st series were assertions, not timeouts:
- runs 3 and 5: `orderEntry` "says so when the channel had already accepted the order …" counted **two** order requests.
  The test before it - my new "submits the order … as it was filled in …" - ended as soon as the outcome view appeared,
  while its order request was still on its way; MSW answered it with the next test's handlers, which counted it. Fixed
  in `dd5efb1`: that test, and "polls while the connection is lost …" (which ended while polling), now end on the
  settled outcome. My mistake in the split, not a property of the suite.
- run 3 also: `products` "dialog reopened before its close animation has finished …" showed "Trà xanh" instead of "Cà
  phê" - an existing race in the product code, see question 1.

### A1 — tests slower than 5 s alone, and what became of them
| Old test (time alone) | New tests | Notes |
| --- | --- | --- |
| `orderEntry` "submits the order of the website panel, follows its task until it succeeded, loads the order once and refreshes the lists once" (7.4 s) | "submits the order … as it was filled in, and the outcome replaces the form" (1.9 s); "outcome by polling › shows each state of the task …" (1.8 s); "outcome by polling › refreshes the lists once, when the task has finished …" (2.0 s) | id format, body, form replaced → 1st; texts, tags, total, link, button width, "no order request before the end" → 2nd; list counters (1, 1, 2, still 2), three task requests, order loaded once → 3rd |
| `orderEntry` "outcome by push › asks for the task once and then only when a hint for it arrives; the order is loaded once" (9.1 s) | "does not poll while live …" (1.8 s); "asks for the task again only when a hint for it arrives; the order is loaded once" (2.0 s); "refreshes the lists once when the order is finished, by the hints …" (2.1 s) | |
| `orderEntry` "outcome by push › keeps the outcome when the connection is lost and comes back while waiting …" (12.9 s) | "polls while the connection is lost, in the same outcome view" (1.8 s); "stops polling when the connection is back - one refetch for what was missed - and the push finishes the order in the same view" (2.4 s) | its first assertion (one request while live) is the test "does not poll while live" |
| `orderEntry` "outcome by push › refreshes the lists once when the connection was lost while waiting and the task is finished by polling" (6.3 s) | same name (2.5 s) | **not split**: it is one step; its three waits are jumped with the fake clock |
| `processing` "writes each filter to the address, starts on page 1 again, and "Clear filters" removes them" (7.5 s) | "filters › writes each select filter …" (2.2 s); "… sends a text filter once per pause in typing …" (3.0 s); "… filters by a range of local days …" (1.5 s); "… "Clear filters" removes every filter …" (1.0 s) | later steps start from the address the earlier ones produced, so the address assertions are unchanged |
| `realtime` "refetches a list on screen at most once per second during a burst and removes a cached list nobody shows" (6.7 s) | "refetches a list on screen at once when idle, and at most once per second during a burst" (3.5 s); "asks for nothing on a hint about something that is not shown anywhere" (3.0 s); "removes a cached list nobody shows instead of refetching it" (3.6 s) | |
| `channelDetail` "sends the mapping, refreshes the list, and shows the stock-sync task the API started - live" (5.4 s) | "sends the mapping, refreshes the list, and shows the stock-sync task the API started" (2.6 s); "follows the sync task live: only a hint for this task fetches it again, and the mapping list is left alone" (2.4 s) | |

Times are of the runs alone before `dd5efb1`; the two tests fixed there now also wait for their settled outcome (about
0.2–0.5 s more). Shared setup is in helpers (`useJumpableClock`, `answerInThreeSteps`, `renderLive`, `submitWhileLive`,
`loseConnection`, `renderFiltered`, `renderOrdersLive`, `renderUnmappedLive`, `fillTeaMapping`), not copied. In
`orderEntry` the waits for a polling interval or a flush window are jumped with the fake clock the file already used for
the 60-second window (`setTimeout` / `Date` fake, real time still advances): the same simulated time passes, the test
does not sit through it. Check that the reworked tests still bite: with `pollingInterval` no longer switched off while
live, "does not poll while live", "stops polling when the connection is back …" and the waiting-window test fail.
`testTimeout` is unchanged (20 s).

### A2 — changed calls
None. Every `Assert-Step` call of `task-009` … `task-016b` was read against the new rules: status codes and counts are
numbers on both sides (`[int]` / JSON numbers), JSON booleans are real booleans (`isLocked`, `isLowStock`, `duplicate`),
comparisons written as `$true (<expression>)` use `-eq` / `-ceq` / `-like` / `[bool]…` and yield a boolean, strings have
the API's exact case (`Staff`, `Succeeded`, `vi`, SKUs upper-case). No call passed `$true` against a non-boolean, a
number against a string or relied on case-insensitivity; `task-016b` already compared `stockSyncEnabled` as text and is
left as it is. Confirmed by running: `task-016b` (development), `task-009`, `task-014`, `task-015`, `task-017`
(throwaway). `task-010` … `013`, `016`: statically only. Self-test: null, boolean, number (Int32 / Int64 / Decimal /
Double, JSON numbers), string and "throws" cases, and what a failed step shows (`True [Boolean]` vs `any text [String]`).

### Backend — integration tests of the task
| # | Requirement | Test |
| --- | --- | --- |
| 1 | low stock | `AlertLifecycleTests.Low_stock_alert_follows_stock_and_threshold…` (create → Open + both hints in one notification; import → Resolved; order down to the threshold → new Open; further order → unchanged, no hint; threshold down → Resolved; up → third alert; exact hint sequence), `New_product_with_threshold_zero…`, `Concurrent_reservations_crossing_the_threshold…` (12 orders, 2 hosts × 8: 12 attempts, all first and Succeeded, one unresolved alert), `Low_stock_rule_in_sql_text_selects_the_same_rows_as_the_shared_predicate` |
| 2 | insufficient stock | `Rejected_order_opens_an_alert_for_the_short_product_only…` (two-line order, one short; refresh with count 2 and latest order; other product's import → unchanged; own import → Resolved; later rejection → new alert; hint sequence) |
| 3 | stock sync | `Stock_sync_failure_alert_is_per_channel_and_product…` (failure → Open with channel, product, task, error; acknowledged; second failed task → refresh, still Acknowledged; success on another channel → unchanged; a third task of the pair succeeds → Resolved) |
| 4 | order processing | `Failed_order_task_opens_an_alert_per_task_that_survives_the_manual_retry…`, `Task_that_fails_again_after_a_manual_retry_refreshes_its_alert…` |
| 5 | transactions | `Rolled_back_transaction_leaves_no_alert_and_its_hint_is_never_delivered`, `Rolled_back_import_changes_no_alert…`, the rolled-back reservation inside test 2 (no low-stock alert for the product that had enough), `Alert_writer_refuses_to_write_outside_an_explicit_transaction` (all four methods) |
| 6 | API | `AlertEndpointsTests` (default hides resolved, names, current stock, each filter, repeatable `status` / `type`, inclusive range, three sort fields, paging, 11 invalid queries, 92 days in en / vi, item members, 404, acknowledge 200 + audit + hint, again 200 unchanged, Resolved → 409, Staff 403, anonymous 401, summary against independent counts and deltas) |
| 7 | backfill | `Backfill_opens_alerts_for_low_products_for_pairs_whose_last_finished_sync_failed…` (runs `AlertBackfill.Sql` twice inside a transaction that is rolled back) |
| 8 | existing tests | green; one assertion of `RealtimeHintProducerTests` changed (deviation 4) |
| — | cost | `Reservation_runs_three_alert_statements_in_its_transaction…`, `Alert_list_is_a_count_plus_one_page_statement…` |

Concurrent failure and success of **different** stock-sync tasks of one (channel, product): no test (last writer wins,
as the task accepts); the sequential orders failure → failure → success are covered by test 3.

Mutants (all killed): reconcile skipped in reserve; refresh resets `Acknowledged`; stock-sync success resolves by
task instead of (channel, product); low stock counted (`DO UPDATE`); SQL rule `<` instead of `<=`; writer without the
transaction check; product update without reconcile; import without resolving insufficient stock; acknowledge without
`Status = 'Open'`; default list incl. resolved; insufficient-stock alert for every line; stock-sync failure as per-task
alert; succeeded order task not resolving its alert; summary counting resolved; backfill ordered the wrong way.

### Smoke table (`task-017.ps1`, throwaway environment, second run)
PostgreSQL 17 initialised by `docker/postgres/init/01-create-roles.sh` (generated passwords), migrations applied with
`omnichannel_migrator`, API and Workers from the build output as `omnichannel_app` (environment `Smoke`: no
user-secrets; `ChannelSimulation__SkuRules=true`). After the migration:
`Alerts: dml=true truncate=false owner=omnichannel_migrator rows=0` (`has_table_privilege` for the application role).
```
Step                                                              Expected          Actual            OK
01 create smoke manager                                           201               201               True
02 create smoke staff                                             201               201               True
03 create smoke customer                                          201               201               True
04 create smoke product (threshold 5)                             201               201               True
05 new product: one LowStock alert                                1                 1                 True
06 ... which is Open                                              Open              Open              True
07 ... with the current stock and the threshold                   0 5               0 5               True
08 import 10 units (above the threshold)                          200               200               True
09 the LowStock alert is Resolved                                 Resolved          Resolved          True
10 staff submits an order (website, 6 units)                      202               202               True
11 order task                                                     Succeeded         Succeeded         True
12 the reservation opened a second LowStock alert                 2                 2                 True
13 ... which is Open                                              Open              Open              True
14 ... and is not the first alert again                           True              True              True
15 staff cannot acknowledge                                       403               403               True
16 ... the alert is still Open                                    Open              Open              True
17 manager acknowledges                                           200               200               True
18 ... the alert is Acknowledged                                  Acknowledged      Acknowledged      True
19 ... by the smoke manager                                       (the smoke manager's e-mail, equal) True
20 acknowledge again                                              200               200               True
21 ... changes nothing (same acknowledgedAt)                      True              True              True
22 summary: alerts.acknowledged = acknowledged alerts in the list 1                 1                 True
23 ... and counts the smoke alert                                 True              True              True
24 summary: alerts.open = open alerts in the list                 0                 0                 True
25 summary: every alert type listed                               (the four types, equal)             True
26 import 10 more units                                           200               200               True
27 the acknowledged alert is Resolved                             Resolved          Resolved          True
28 acknowledge a resolved alert                                   409               409               True
29 ... errorCode                                                  alert_resolved    alert_resolved    True
30 staff submits an order of 100 units                            202               202               True
31 order task                                                     Succeeded         Succeeded         True
32 ... order recorded as InsufficientStock                        InsufficientStock InsufficientStock True
33 InsufficientStock alert                                        Open              Open              True
34 ... names the order                                            (order id, equal)                   True
35 import of the product                                          200               200               True
36 ... resolves the InsufficientStock alert                       Resolved          Resolved          True
37 the website channel exists                                     True              True              True
38 map the product on website (SIM-PERM-)                         201               201               True
39 its sync task (SkuRules must be on)                            Failed            Failed            True
40 StockSyncFailed alert                                          Open              Open              True
41 ... names the channel                                          (channel id, equal)                 True
42 ... the failed task                                            (task id, equal)                    True
43 ... and its error code                                         channel_rejected  channel_rejected  True
44 correct the external SKU                                       200               200               True
45 the new sync task                                              Succeeded         Succeeded         True
46 ... resolves the StockSyncFailed alert                         Resolved          Resolved          True
47 default list of the product: resolved alerts hidden            0                 0                 True
48 ... and listed when asked for                                  4                 4                 True
49 unknown alert                                                  404               404               True
50 ... errorCode                                                  alert_not_found   alert_not_found   True
51 deactivate the smoke mapping                                   200               200               True
53 lock the smoke manager                                         200               200               True
54 lock the smoke staff                                           200               200               True
PASSED: all 53 checks.
```
(Columns narrowed and the ids / e-mail replaced by a description for this report; step 52 is the clean-up that only runs
after a failure.)

## What was built (answers the report items of the task)
**Doc deviations 15–17: confirmed as planned** — `Alerts` table; `GET /api/alerts/{alertId}` and
`POST /api/alerts/{alertId}/acknowledge`; hint `alert.changed` next to `low_stock.created`.

**Call sites as implemented** (each inside its explicit transaction, after the `SaveChanges` that stores what the alert
refers to, before the commit):

| Path | Call |
| --- | --- |
| `InventoryImportService.ImportAsync` | `ReconcileLowStockAsync(imported products)`; `ResolveAsync(insufficient_stock keys of them)` |
| `OrderReservationService.ReserveAsync`, reserved | `ReconcileLowStockAsync(reserved products)`; `ResolveAsync(processing_failed of the task)` |
| `…StoreInsufficientStockOrderAsync` | `OpenOrRefreshAsync(InsufficientStock per product with requested > available in the existing read)`; resolve `processing_failed` of the task |
| `…CompleteWithExistingOrderAsync` | resolve `processing_failed` of the task, when this call completed it |
| `ProductService.CreateAsync` | now an explicit transaction; `ReconcileLowStockAsync(new product)` |
| `ProductService.UpdateAsync` | now an explicit transaction; `SELECT … FROM "Inventories" … FOR UPDATE` before the product is loaded and changed; reconcile |
| `ProcessingAttemptStore.RecordFinalFailureAsync` | `OpenOrRefreshAsync(StockSyncFailed)` for a stock-sync task, `ProcessingFailed` otherwise |
| `StockSyncService.CompleteAsync` | resolve `stock_sync_failed` of the task's (channel, product), when this call completed the task |

No other path changes stock or a threshold (checked: the only `UPDATE "Inventories"` statements are import and
reservation; `LowStockThreshold` is only set in `Product`'s constructor and `Update`). Manual retry does not touch
alerts. The note for a future release / cancel path is in the log ("Task 017 plan", low stock, "Revisit when") and now
also in the README.

**Statements in the reserve transaction** (one-line order, counted by a test with a command interceptor): 5 before
(lock task, load task, reserve `UPDATE`, the `SaveChanges` batch, its `pg_notify`), **8 now**: + `INSERT … SELECT …
ON CONFLICT DO NOTHING` (open where low), + `UPDATE … FROM "Inventories"` (resolve where not low), + `UPDATE` (resolve
the task's `processing_failed`). A 9th (`pg_notify` of the alert writer) only when an alert row changed. The two
reconcile statements are per order, not per line (set-based over the reserved products). An insufficient-stock order
adds one upsert per short product, the resolve and one `pg_notify`. The reserve `UPDATE` itself is unchanged (asserted).

**Writer.** Open / refresh: `INSERT … ON CONFLICT ("DedupKey") WHERE "Status" <> 'Resolved' DO UPDATE` (status not in
the `SET` list); low stock: `INSERT … SELECT … ON CONFLICT … DO NOTHING` + `UPDATE … RETURNING`; resolve and
acknowledge: conditional `UPDATE … RETURNING`. One `pg_notify` per writer call, only when a row changed. The rule in SQL
text is one constant (`InventoryPredicates.IsLowStockSql`, next to the predicate), used by the writer and the backfill
and pinned by a test on rows on both sides of the boundary.

**Partial unique index** `UX_Alerts_DedupKey_Unresolved`: **not** registered in `ExpectedUniqueViolations`. The three
inserts (low stock, open / refresh, backfill) name it as their `ON CONFLICT` arbiter, and no `UPDATE` changes a key or
moves a row back into the index, so no code path can see a 23505 on it (a model test asserts that it is not registered).

**Indexes:** the three of the task plus `IX_Alerts_ChannelId`, `IX_Alerts_TaskId`, `IX_Alerts_OrderId`, which EF Core's
foreign-key convention creates (deviation 5).

**Application role:** the test infrastructure has no application role (Testcontainers run as the container's owner).
Checked in the throwaway environment instead, see the smoke section: DML yes, `TRUNCATE` no, through the default
privileges of `01-create-roles.sh`; the API and the Workers ran the whole smoke as `omnichannel_app`.

**New `errorCode`s** (client translations in 018): `alert_not_found` (404), `alert_resolved` (409).

## Deviations from the task
1. **A1 order:** the tests were split first and the worker count measured on the split suite (the task lists the
   measurement first). The choice has to hold for the suite as it is now.
2. **A1.3, one test not split** ("refreshes the lists once when the connection was lost …"): it is a single step; it
   went from 6.3 s to 2.5 s by jumping its waits. The other order-entry tests use the same fake clock instead of real
   waits (see above).
3. **A1, step numbers of the six smoke scripts:** the clean-up steps now come before the npm steps, so their numbers
   moved (e.g. `task-016b`: lock users 23 / 24, npm 25–27).
4. **Existing test changed:** `RealtimeHintProducerTests` listed the three hint types that may appear; the failed stock
   sync of that test now also announces its alert. The list has the two alert types, and one assertion on that
   `alert.changed` was added. No other existing test was touched.
5. **Three more indexes** than the task lists (`ChannelId`, `TaskId`, `OrderId`): created by EF Core's foreign-key
   convention, which cannot be switched off per relationship (removing the index re-creates it). `ChannelId` serves the
   `channelId` filter; the other two serve nothing today. Dropping them by hand in the migration would leave the model
   snapshot and the database different.
6. **Backfill ids are UUIDv4** (`gen_random_uuid()`): PostgreSQL 17 cannot generate v7. Alerts written by the
   application have v7 ids.
7. **"Newest stock-sync task" in the backfill = the most recently finished one** (`Failed` or `Succeeded`, by
   `UpdatedAt`): that is the order in which the live system would have opened and resolved the alert; tasks still
   waiting or running decide nothing.
8. **Resolve locks its rows in key order** (`UPDATE … FROM (SELECT … ORDER BY "DedupKey" FOR UPDATE)`, still one
   statement), and several upserts of one call run in key order: an import that resolves the insufficient-stock alerts
   of several products and a rejected order that opens them could otherwise deadlock.
9. **`AlertStore` is `public`** (as `AlertOccurrence`, `AlertChange`, `AlertBackfill`): the test project has no access
   to internals, and the tests of the writer need it. `AlertService` stays internal behind `IAlertService`.
10. **A number that is no status / type** (`status=99`) is refused by model binding with the key `status`, not
    `status[1]`; the validator's `IsInEnum` is kept for callers of the service.
11. **Every sort field of the list is descending unless `sortDir=asc`** (the task names the default only for
    `lastOccurredAt`); `acknowledgedBy` is `system` when there is no user (cannot happen through the API).
12. **Two split tests fixed after the first five-run series** (`dd5efb1`): they ended with a request in flight, see
    "Verification". The split map above describes the final tests.
13. **`task-016b.ps1` was run on the state of the Part A commits** (the unfinished backend work stashed meanwhile): the
    development database does not have the `Alerts` table yet.

## Blocked / questions for Cowork
1. **Finding in existing code (016b mechanism): a form dialog reopened right after Cancel can show the previous record's
   values.** `products.test.tsx` "dialog reopened before its close animation has finished …" (016b's regression test)
   failed in one full run and in 1 of 12 repetitions alone; it is not a timeout: the dialog opened for coffee shows
   tea's name. A temporary probe (not committed; the product files were restored and `git status` was clean afterwards)
   repeated the test's scenario 40 times per run: 2 of 40 in each of three runs. With a trace in `useResetFormOnOpen`
   and inside the `Form`:
   - passing round: `ProductFormModal renders (COF) → Form subtree renders (COF) → hook effect: resetFields → name "Cà phê"`;
   - failing round: `ProductFormModal renders (COF) → hook effect: resetFields → name "Trà xanh" → Form subtree renders (COF)`.

   The hook resets in the dialog component's effect, but the dialog library may render the content of a closing dialog
   one commit later than its parent (the children of a dialog that is not yet visible again are memoised). The reset
   then uses the previous record's `initialValues`, and AntD never applies `initialValues` again to a mounted form:
   the dialog for record B shows record A's pristine values, and **saving would write them onto B** - the defect class
   of 016b deviation 1, in the "reopened while closing" case. In jsdom about 5 % of the attempts; in the browser the
   window is the moment right after Cancel while the close animation runs (a quick keyboard user). Not fixed here: it
   is product code of the seven dialogs and needs the production-build browser check of the log - a task of its own.
   Proposal: do the reset from inside the form subtree (a small component rendered inside `<Form>` whose effect
   depends on what the dialog is open for), so it always follows the render that gave the form its new
   `initialValues`; the 016b test then becomes deterministic. Until then that test fails about once in 12–15 runs.
2. **Insufficient stock, stale read:** `StoreInsufficientStockOrderAsync` reads the availability without a lock. An
   import that commits between that read and the alert write resolves first; the rejection then opens an alert although
   the stock has arrived. It stays until the next import of the product. Closing it needs a lock on the inventory rows
   in that path (`FOR SHARE`), which the task did not ask for.
3. **Stock-sync alert of a mapping that is deactivated afterwards** stays open: no new task is created for an inactive
   mapping. It ends when the failed task is retried manually (the attempt completes without a push) or the mapping is
   activated again and syncs. Should deactivating a mapping (or a channel) resolve the pair's alert?
4. **Backfill on the development database:** every product that is low now gets an alert - that includes the many
   `SMOKE-*` products without stock. Expected, but the first look at the alert list will be mostly those.
5. **`Complete-Smoke` cuts columns** when a step has long values (`Out-String -Width 200`): in the first `task-017` run
   the `OK` column was missing (the summary line and the exit code were right). I shortened the steps of 017; with the
   strict `Assert-Step` a failed step shows type names and gets wider. A wider table or the `OK` column first would fix
   it for every script.
6. **A new product always opens a low-stock alert** (as decided). A bulk creation of products will open as many; the
   hints of one transaction are capped (reduced to "all of this type" above 8000 bytes), the alerts are not.

## Manual steps for the user
1. Apply the migration to the development database (README section 4), then restart the API and the Workers host:
   ```powershell
   cd backend
   $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
   dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
   ```
   The migration creates `Alerts` and opens alerts for the current state (question 4).
2. `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-017.ps1` (Workers host in Development).
3. Optional: `scripts\smoke\_assert-selftest.ps1`; `task-010` … `013`, `016` after the migration (their npm checks now
   run after the hosts they started are stopped).

## Out of scope / follow-ups
- Alerts page and the `alert.changed` / `low_stock.created` → tag mapping in the frontend (018); translations of the two
  error codes; the dashboard does not show the `alerts` section yet.
- Question 1 (dialog reset from inside the form subtree, 018 Part A?); questions 2, 3 and 5 if accepted.
- `IX_Alerts_TaskId` / `IX_Alerts_OrderId` could go if the model stops declaring those two foreign keys as relationships
  (026, with the other index work).

# Report 018b — Logs/Audit: `GET /api/system-events` + Logs/Audit page; Part A (finish 018, in-flight request guard)

Status: Done
Commit: `b84ef98` (A1, "task 018: report"), `1267307` (A2), `0f8259c` (Part B), `0570c8b` (Part C, smoke script, README),
plus the commit of this report.

## Summary
- **A1:** the 018 report has its five consecutive full `npm test` runs (5 of 5 green, 338 tests, 292 / 294 / 435 / 391 /
  376 s) and is committed with the 018 README changes as the first commit.
- **A2:** the test setup fails a test that ends with a request still unanswered, naming method and URL. On a quiet
  machine the guard flags nothing; with every answer delayed by 120 ms (temporary diagnostic) it flagged **66 tests in
  19 files** - the tests that were red only under load. All of them now end settled. One named opt-out exists and is
  not used anywhere.
- **Part B:** `GET /api/system-events` (Manager/Admin; `user.*` events for an Admin only, enforced by the server);
  `details` through a per-event-type allowlist; `actorEmail` / `targetEmail` from one user lookup per page. No
  migration, no new index, no new dependency.
- **Part C:** the Logs/Audit page `/logs` with filters, sort direction, page and time window in the address, one
  summary line per event type in vi and en, and the trace link.
- Backend build 0 warnings, **890 / 890** tests (825 + 65), 19 of 19 valid mutants killed. Frontend typecheck, lint and
  build clean, **373 tests** (338 + 35), **5 of 5 consecutive full runs green** with the guard (281 / 269 / 243 / 234 / 241 s). First-load set 969.15 → 969.88 kB raw
  (317.69 → 317.98 kB gzip); `LogsPage` is a lazy chunk of 11.82 kB. Smoke script **37 of 37 on the development
  environment**; real-browser check on the production build done.

## Files changed
Backend
- `Omnichannel.Application/SystemEvents/SystemEventContracts.cs` (new) — `SystemEventResponse`,
  `SystemEventListQuery`, `SystemEventVisibility.AdminOnlyPrefix`, `ISystemEventService`.
- `Omnichannel.Application/SystemEvents/SystemEventDetails.cs` (new) — the allowlist (one entry per event type
  constant), `Filter`, `ActorIdOf`, `TargetIdOf`.
- `Omnichannel.Application/SystemEvents/SystemEventListQueryValidator.cs` (new).
- `Omnichannel.Application/Common/Messaging/OutboxEvents.cs` (new) — `PublishFailed`;
  `Omnichannel.Workers/OutboxPublisher/OutboxPublisherService.cs` — its constant refers to it (deviation 4).
- `Omnichannel.Application/Resources/FieldNames.resx`, `.vi.resx` — `level`, `eventType`.
- `Omnichannel.Infrastructure/Persistence/SystemEvents/SystemEventService.cs` (new), `DependencyInjection.cs`.
- `Omnichannel.Api/Controllers/SystemEventsController.cs` (new), `Omnichannel.Api.http` (five samples).
- Tests: `Omnichannel.Tests/SystemEvents/SystemEventDetailsTests.cs` (unit, 22),
  `Integration/SystemEventEndpointsTests.cs` (40), `Integration/Workers/SystemEventQueryShapeTests.cs` (1).

Frontend
- `features/logs/` (new): `systemEventsApi.ts`, `paths.ts`, `range.ts`, `eventSummary.ts`, `LogsPage.tsx`,
  `components/EventLevelTag.tsx`, `logs.test.tsx` (35 tests); `test/systemEvents.ts` (fixtures, one per event type).
- `app/navigation.tsx` (roles, `plannedTask` removed), `app/router.tsx` (lazy `LogsPage`), `shared/api/baseApi.ts`
  (tag type `SystemEvent`), `shared/components/layout.ts` (`ID_FILTER_WIDTH`), `features/dashboard/range.ts`
  (`presetLocalDays` exported, used by both pages), `shared/i18n/vi.json`, `en.json` (`logs.*`, 53 keys each).
- A2: `test/pendingRequests.ts` (new), `test/setup.ts`, `test/interactions.ts` (`settled()`), 19 test files.

Other
- `scripts/smoke/task-018b.ps1` (new); `README.md` — "System events (Logs/Audit)", the Logs page and the test rule in
  "Frontend"; `docs/tasks/018-frontend-alerts-page.report.md` (A1).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| A1: `npm test` × 5 on the 018 state | 5 of 5 green, 338 passed each; 292 / 294 / 435 / 391 / 376 s (in the 018 report) |
| `dotnet build` (backend, final state) | 0 errors, 0 warnings |
| `dotnet test` (backend, final state) | **890 passed, 0 failed** (1 m 30 s); 825 before. Existing tests unmodified |
| 20 mutants of Part B, each against the 63 system-event tests | 19 killed, 1 did not compile (table below); one survivor of the first run killed after the counter was fixed |
| `npm run typecheck`, `npm run lint` (final state) | clean |
| `npm run build`, `npm run measure` (final state) | built; first-load set 969.88 kB raw / 317.98 kB gzip; `LogsPage` 11.82 kB / 4.78 kB gzip |
| A2: temporary test file (7 cases) | fails the three it must (delayed answer pending; request sent in the last tick; test after an opt-out), passes the four it must. Not committed |
| A2: full suite, guard active, tests of 018 unchanged | 338 passed - the guard flags nothing on a quiet machine |
| A2: the same with every answer delayed by 120 ms (temporary) | 66 failed (51 with the guard's message alone, 15 also on an assertion that assumed an instant answer) |
| A2: full suite after the fixes, with the delay | 3 failed: counts of list refetches in `orderEntry.test.tsx` "outcome by push" (not a request in flight; question 3) |
| A2: full suite after the fixes, no delay | 338 passed (168 s) |
| `logs.test.tsx` alone, with and without the delay | 35 passed both ways (10 of its tests were flagged with the delay first and fixed) |
| `npm test` × 5 consecutive, final state, guard active | **5 of 5 green**, 373 passed each (28 files); 281 / 269 / 243 / 234 / 241 s |
| `scripts\smoke\task-018b.ps1` on the **development** environment | **PASSED: all 37 checks** (first run), incl. `npm run build` and `npm test` |
| `EXPLAIN (ANALYZE)` on a throwaway database with 60,000 events | page queries: index scan backward on `IX_SystemEventLogs_CreatedAt`, no sequential scan (below) |
| Real-browser check, production build, development environment, 1280 × 800 | done (below) |

Not executed: a keyboard-only pass in the browser (the Browser pane does not deliver keys to AntD reliably; controls
were driven by DOM clicks). `EXPLAIN` of the exact SQL text EF Core emits - the statements below are the same queries
written by hand (deviation 13).

### Five consecutive full frontend runs (final state, guard active)
Five consecutive `npm test` runs (one script, each run a new process), final state of this task (commit `0570c8b`),
guard active, PostgreSQL and RabbitMQ running; no API, no Workers host, no preview server, no build:

| Run | Result | Wall time | vitest `Duration` |
| --- | --- | --- | --- |
| 1 | 28 files, 373 passed, exit 0 | 281 s | 279.58 s |
| 2 | 28 files, 373 passed, exit 0 | 269 s | 267.34 s |
| 3 | 28 files, 373 passed, exit 0 | 243 s | 242.15 s |
| 4 | 28 files, 373 passed, exit 0 | 234 s | 232.02 s |
| 5 | 28 files, 373 passed, exit 0 | 241 s | 239.88 s |

During run 1 I committed Part C (`git commit`) and wrote this report's draft outside the repository; after that
nothing but a waiting loop ran next to the series. A single run right after the A2 fixes took 168 s and the first run
with the Logs page 188 s; the series is slower than those, without any test near its 20 s limit failing.

### A2 — what the guard is, and why not MSW's life-cycle events
`src/test/pendingRequests.ts` wraps `fetch` (after `server.listen()`, so it sits on top of MSW's patched `fetch`) and
counts a request from the call until its promise settles; `setup.ts` asks for what is left as the first thing of
`afterEach` and throws at its end:
```
Error: The test ended with 1 request(s) still unanswered: GET http://localhost:3000/api/slow. End the test on its
settled state (wait for what the answer puts on screen); a request in flight is answered while the next test runs.
```
The task asked for MSW's life-cycle events. I read the API of the installed MSW 3.0.1
(`server.events.on('request:start' | 'request:end' | 'unhandledException' | …)`, `HttpNetworkFrameEventMap`), built the
guard on it first and measured it with a probe:
```
33.2 fetch called            44.5 after 21 microtasks        63.1 request:start
65.7 after 1 macrotask       112.0 request:end               119.8 fetch resolved
```
MSW reports a request about 19 ms - one macrotask - after the call. With the events alone, a test that sends a request
in its last tick ended "clean", and the request was then held against the **next** test (my temporary cases C and D:
C passed, D failed for C's request). Counting at the `fetch` call closes that gap, attributes every request to the test
that sent it, and its window contains MSW's (`request:end` comes before the promise resolves). Deviation 1.

Temporary test (deleted, not committed), final guard:
| Case | Expected | Result |
| --- | --- | --- |
| A: ends 50 ms into a 400 ms answer | fail with the message | failed, message names `GET …/api/slow` |
| B: the test after A | pass | passed |
| C: ends in the tick of the `fetch` call | fail | failed |
| D: awaits the answer | pass | passed |
| E: `allowUnansweredRequests('…')`, then like A | pass | passed |
| F: the test after E, like A | fail | failed |
| G: a request that fails (network error) | pass | passed |

**Opt-out:** `allowUnansweredRequests(reason)`; used nowhere (no test of the suite leaves a request unanswered on
purpose).

**How the flagged tests were found.** Whether a request is still open when a test ends depends on a few milliseconds,
which is why the red runs came only under load. To see every such test in one run, the wrapper delayed each answer by
120 ms (an environment variable, removed before the commit). Fix: a shared `settled()` (`test/interactions.ts`) - no
request on its way and nothing on screen loading (`.ant-spin-spinning`, `.ant-btn-loading`) - as the last statement,
or the text the last answer puts on screen where the test has one. No `testTimeout` change.

Fixed tests (file, test, the request that was open when it ended; all now end with `settled()` unless noted):
- `app/codeSplitting.test.tsx`
  - "shows the shell and the loading fallback first, then the page" - open at the end: `GET /customers?page=1&pageSize=20`
  - "loads the chunk of a page when the user navigates to it through the menu" - open at the end: `GET /channels`
- `app/routeErrors.test.tsx`
  - "tells the user to reload when the code of a page cannot be loaded, and the menu still works" - open at the end: `GET /products?page=1&pageSize=20`
  - "shows a generic message, a way back and the reload when a page throws while rendering" - open at the end: `GET /dashboard/summary?`
- `features/alerts/alerts.test.tsx`
  - "status: "Resolved" and "all" are explicit choices; each change starts on page 1 again" - open at the end: `GET /alerts?…`
  - "type, product and channel go to the address next to each other and start on page 1 again" - open at the end: `GET /alerts?…`
  - "filters by a range of local days on the last occurrence and starts on page 1 again" - open at the end: `GET /alerts?…`
  - ""Clear filters" removes every filter from the address and from the controls; the sort stays" - open at the end: `GET /alerts?…`
  - "sorts through the address by the server's three fields, each most recent / most frequent first" - open at the end: `GET /alerts?…`
  - "asks again on "Refresh" and whenever the page is opened" - open at the end: `GET /alerts?page=1&pageSize=20`
- `features/auth/auth.test.tsx`
  - "stores the session in sessionStorage and lands on the dashboard" - open at the end: `GET /dashboard/summary?`
  - "sends an unauthenticated visitor of /users to the login page and back after login" - open at the end: `GET /users?page=1&pageSize=20`
  - "never redirects to another site after login" - open at the end: `GET /dashboard/summary?`
  - "shows the Users menu item to an Admin" - open at the end: `GET /auth/me`, `GET /dashboard/summary?`
  - "logs out and returns to the login page when the API answers 401" - open at the end: `GET /auth/me`, `GET /users?page=1&pageSize=20`; an assertion in the middle assumed an instant answer
- `features/channels/channelDetail.test.tsx`
  - "shows the channel as the API sends it, with the link to its stock-sync tasks, under the Channels menu item" - open at the end: `GET /channels/{id}/products?…`
  - "says that an inactive channel gets no stock; whether it is synced at all is still what the API says" - open at the end: `GET /channels/{id}/products?…`
  - "says that a channel without a stock-sync queue is never synced, and offers no task link" - open at the end: `GET /channels/{id}/products?…`
  - "never shows the previous channel, its mappings or the result of its last save while the next channel loads" - open at the end: `GET /channels/{id}/products?…`
  - "shows both hints for an inactive channel without a queue" - open at the end: `GET /channels/{id}/products?…`
  - "restores search, sort and paging from the address (reload) and writes changes back to it, starting on page 1 again" - open at the end: `GET /channels/{id}/products?…`
  - "fetches the mapping list again whenever the page is opened" - open at the end: `GET /channels/{id}/products?…`
- `features/channels/channels.test.tsx`
  - "activating a channel or renaming it needs no confirmation" - open at the end: `GET /channels`
- `features/customers/customers.test.tsx`
  - "restores paging, sorting and search from the address (reload) and writes changes back to it" - open at the end: `GET /customers?…`
- `features/dashboard/dashboard.test.tsx`
  - "keeps the range in the address: presets send local days, the default sends nothing" - open at the end: `GET /dashboard/summary?`
  - "asks again on "Refresh" and whenever the page is opened, and never polls" - open at the end: `GET /dashboard/summary?`
  - "opens the alert list from a number of the alert card, filtered as the number is" - open at the end: `GET /alerts?…`, `GET /channels`
- `features/inventory/inventory.test.tsx`
  - "restores paging, sorting, search and "low stock only" from the address (reload) and writes changes back" - open at the end: `GET /inventory?…`
- `features/inventory/movements.test.tsx`
  - "a stock-import reference filters the history by that document; a manual adjustment is plain text" - open at the end: `GET /inventory/movements?…`
  - "is readable by every role" - open at the end: `GET /auth/me`, `GET /inventory/movements?page=1&pageSize=20`
- `features/inventory/stockImport.test.tsx`
  - "imports a document with two lines after confirmation, shows the stock the API returned and refreshes the lists" - open at the end: `GET /inventory/movements?…`
  - "ignores a productId in the address that is not an id" - open at the end: `GET /auth/me`
  - "answers Staff with the no-access page and sends no request" - open at the end: `GET /auth/me`
  - "opens for Admin" - open at the end: `GET /auth/me`
  - "removes the list error of the previous attempt on the next submit" - open at the end: `POST /inventory/import`; an assertion in the middle assumed an instant answer
  - "shows a document that was already imported on the document number, and the stock limit above the form" - open at the end: `POST /inventory/import`; an assertion in the middle assumed an instant answer
  - "opens for Manager" - open at the end: `GET /auth/me`
- `features/orders/orderDetail.test.tsx`
  - "shows the not-found view for an id that is no GUID, without asking the API" - open at the end: `GET /orders?page=1&pageSize=20`, `GET /channels`
  - "leads to the movements of this order, and "Back" goes to the list when the detail was not opened from it" - open at the end: `GET /orders?page=1&pageSize=20`, `GET /channels`
  - "asks the API again whenever the page is opened" - open at the end: `GET /orders/{id}`
- `features/orders/orderEntry.test.tsx`
  - "shows API errors where they belong, keeps the external order id for the resend, and "New order" gives a new one" - open at the end: `GET /processing/tasks/{id}`
  - "lets the user type the external order id or ask for a new number, and sends it as it is" - open at the end: `GET /processing/tasks/{id}`
  - "does not ask while the tab is hidden and continues when it is shown again" - open at the end: `GET /processing/tasks/{id}`
  - "refreshes the lists once when the order is finished, by the hints: the outcome invalidates nothing while live" - an assertion in the middle assumed an instant answer
  - "leaves the lists alone when it sees the task finished while live: only hints refresh them" - an assertion in the middle assumed an instant answer
  - "refreshes the lists once when the connection was lost while waiting and the task is finished by polling" - an assertion in the middle assumed an instant answer
  - "changes text only while an outcome is being polled: no extra request, and the polling is not restarted" - open at the end: `GET /processing/tasks/{id}`
- `features/orders/orders.test.tsx`
  - "writes search and each filter to the address, starts on page 1 again, and "Clear filters" removes the filters" - open at the end: `GET /orders?page=1&pageSize=20&search=ZZ99`
  - "sorts through the address: created ascending, then the other fields, and back to the default order" - open at the end: `GET /orders?…`
  - "opens the detail with the list address, and "Back" on the detail returns to the same page, sort and filters" - open at the end: `GET /orders/{id}`
  - "asks the API again on "Refresh" and whenever the page is opened" - open at the end: `GET /orders?page=1&pageSize=20`
- `features/processing/processing.test.tsx`
  - "writes each select filter to the address and starts on page 1 again" - open at the end: `GET /processing/tasks?…`
  - "sends a text filter once per pause in typing, and starts on page 1 again" - open at the end: `GET /processing/tasks?…`
  - "filters by a range of local days on the creation time, next to the other filters, and starts on page 1 again" - open at the end: `GET /processing/tasks?…`
  - ""Clear filters" removes every filter from the address and from the controls" - open at the end: `GET /processing/tasks?page=1&pageSize=20`
  - "sorts through the address: created ascending and back to the default, updated newest first" - open at the end: `GET /processing/tasks?…`
  - "opens the detail with the list address, and "Back" returns to the same page, sort and filters" - open at the end: `GET /processing/tasks/{id}`
- `features/processing/processingTask.test.tsx`
  - "goes back to the plain list when it was not opened from it, and asks again on "Refresh"" - open at the end: `GET /processing/tasks?page=1&pageSize=20`
  - "says that the task does not exist any more (404)" - open at the end: `GET /processing/tasks/{id}`
- `features/products/products.test.tsx`
  - "restores paging, sorting and search from the address (reload) and writes changes back to it" - open at the end: `GET /products?…`
  - "accepts an expiry date typed as DD/MM/YYYY and sends it as YYYY-MM-DD" - open at the end: `POST /products`
- `features/realtime/realtime.test.tsx`
  - "opens exactly one connection under StrictMode" - open at the end: `GET /auth/me`
- `features/users/users.test.tsx`
  - "pages, sorts, searches (debounced) and filters on the server and keeps that state in the address" - open at the end: `GET /users?…`
  - "resets a password and shows policy errors on the new-password field" - open at the end: `GET /users?page=1&pageSize=20`
  - "shows the correlation id this client sent when the server cannot be reached" - open at the end: `GET /auth/me`
  - "changes our texts, AntD texts and the Accept-Language header, and stores the choice" - open at the end: `GET /users?page=1&pageSize=20&search=x`
- Besides `settled()`:
  - `auth.test.tsx` "logs out and returns to the login page when the API answers 401" waits for the address
    `/login?returnUrl=%2Fusers` before it looks for the e-mail field (the Users page has an "E-mail" of its own while
    the 401 is on its way).
  - `stockImport.test.tsx` (two tests, three assertions): `findByRole('alert')` took the warning of the confirmation
    while the answer was on its way; they wait until the alert has the expected text.
  - `orderEntry.test.tsx` "lets the user type the external order id …" waits for the outcome's final text ("Đã giữ
    hàng cho đơn hàng."), as `dd5efb1` did for its neighbours.
  - `auth.test.tsx` "goes to the plain login page on logout" (flagged in the second diagnostic run): `settled()`.
- `logs.test.tsx` (new in this task): 13 of its 35 tests end with `settled()` (10 were flagged by the delay).

### Part B — mutants (each: apply, build, run the 63 system-event tests, restore)
| Mutant | Result |
| --- | --- |
| the controller treats every caller as an Admin | killed |
| the controller treats a Manager as an Admin | killed |
| the admin-only prefix is `user` (no dot) | killed (the `users.exported` look-alike) |
| details are the whole payload | killed (17 tests) |
| `lastError` allowed for `outbox.publish_failed` | killed (unit + integration) |
| `reason` allowed for `order.message_rejected` | killed (unit + integration) |
| an unknown event type returns its payload | killed |
| the tie-break is always ascending | killed |
| ascending is the default order | killed |
| the range excludes its start / its end | killed / killed |
| the level filter is ignored | killed |
| the event type filter matches a prefix | killed |
| the task filter / the order filter is ignored | killed / killed |
| the target is resolved from the actor field | killed |
| users are looked up one by one | **survived the first run**: the lookup was synchronous and the command counter counted asynchronous commands only. The counter now counts both; killed |
| the 92-day limit is 93 days | killed |
| the count ignores the filters | killed |
| the role rule is never applied (`if (false)`) | did not compile (unreachable code is an error); covered by the two controller mutants |

### Part B — query plans (throwaway PostgreSQL 17, all migrations, 60,000 events over 60 days, `ANALYZE`d)
```
== 1. default page, Admin (no filter, page 1) ==
 Limit (actual rows=20 loops=1)
   ->  Incremental Sort (actual rows=20 loops=1)
         Sort Key: "CreatedAt" DESC, "LogId" DESC
         Presorted Key: "CreatedAt"
         ->  Index Scan Backward using "IX_SystemEventLogs_CreatedAt" on "SystemEventLogs" s (actual rows=21 loops=1)
 Execution Time: 0.086 ms

== 2. default page, Manager (no user.* events) ==
         ->  Index Scan Backward using "IX_SystemEventLogs_CreatedAt" on "SystemEventLogs" s (actual rows=21 loops=1)
               Filter: (("EventType")::text !~~ 'user.%'::text)
               Rows Removed by Filter: 1
 Execution Time: 0.180 ms

== 3. eventType + range (two types, the last 7 days), page 1 ==
 Limit (actual rows=20 loops=1)
   ->  Incremental Sort (actual rows=20 loops=1)
         Sort Key: "CreatedAt" DESC, "LogId" DESC
         Presorted Key: "CreatedAt"
         ->  Index Scan Backward using "IX_SystemEventLogs_CreatedAt" on "SystemEventLogs" s (actual rows=21 loops=1)
               Index Cond: (("CreatedAt" >= (now() - '7 days'::interval)) AND ("CreatedAt" <= now()))
               Filter: ((("EventType")::text !~~ 'user.%'::text) AND (("EventType")::text = ANY ('{processing.failed,inventory.imported}'::text[])))
               Rows Removed by Filter: 26
 Execution Time: 0.119 ms

== 5. COUNT of the page's default view (the last 24 hours) ==
   ->  Index Scan using "IX_SystemEventLogs_CreatedAt" on "SystemEventLogs" s (actual rows=949 loops=1)
 Execution Time: 0.264 ms

== 6. COUNT without any filter (the whole table) ==
   ->  Seq Scan on "SystemEventLogs" s (actual rows=60000 loops=1)
 Execution Time: 12.904 ms

== 7. trace: one correlation id ==
         ->  Index Scan using "IX_SystemEventLogs_CorrelationId" on "SystemEventLogs" s (actual rows=3 loops=1)
 Execution Time: 0.136 ms
```
No sequential scan for a page query, so **no new index**. The tie-break on `LogId` costs an incremental sort over rows
of the same instant. Two things for 026: the unfiltered `COUNT` reads the table (13 ms at 60,000 rows; the page never
asks without a window), and a rare event type inside a long range is found by walking `IX_…_CreatedAt` with a filter -
the planner did not choose `(EventType, CreatedAt)` here because the two types are 45 % of the rows.

### Smoke table (`scripts\smoke\task-018b.ps1`, development environment, API and Workers started by the script)
```
OK    Step                                                             Expected                           Actual
--    ----                                                             --------                           ------
True  01 create smoke manager                                          201                                201
True  02 create smoke staff                                            201                                201
True  03 staff cannot read the events                                  403                                403
True  04 ... nor an anonymous caller                                   401                                401
True  05 create smoke product                                          201                                201
True  06 manager imports 10 units                                      200                                200
True  07 manager sees one inventory.imported event of that request     1                                  1
True  08 ... details.referenceId is the document                       SMOKE-018B-GRN-84323               SMOKE-018B-GRN-84323
True  09 ... with the number of lines and units                        1 10                               1 10
True  10 the website channel exists                                    True                               True
True  11 map the product on website (SIM-PERM-)                        201                                201
True  12 its sync task (SkuRules must be on)                           Failed                             Failed
True  13 one processing.failed event of the task                       1                                  1
True  14 ... level                                                     Error                              Error
True  15 ... details.errorCode                                         channel_rejected                   channel_rejected
True  16 ... the task id (column and details)                          01a10204-5e09-73e0-89e9-16b72adcee 01a10204-5e09-73e0-89e9-16b72adcee
                                                                       63 01a10204-5e09-73e0-89e9-16b72ad 63 01a10204-5e09-73e0-89e9-16b72ad
                                                                       cee63                              cee63
True  17 ... it has a correlation id                                   True                               True
True  18 found again by its correlation id                             1                                  1
True  19 manager acknowledges the stock-sync alert                     200                                200
True  20 one event of that request: alert.acknowledged                 1 alert.acknowledged               1 alert.acknowledged
True  21 ... actorEmail is the smoke manager                           smoke-018b-mgr-84323@example.com   smoke-018b-mgr-84323@example.com
True  22 ... details.alertId is the alert                              01a10204-63e7-7af4-a2f1-dad4fa9430 01a10204-63e7-7af4-a2f1-dad4fa9430
                                                                       ed                                 ed
True  23 admin locks the smoke staff                                   200                                200
True  24 admin sees one event of that request: user.locked             1 user.locked                      1 user.locked
True  25 ... targetEmail is the smoke staff                            smoke-018b-staff-84323@example.com smoke-018b-staff-84323@example.com
True  26 ... actorEmail is the admin                                   True                               True
True  27 manager: the same request has no event                        200 0 0                            200 0 0
True  28 ... also when asking for user.locked: an empty page           200 0 0                            200 0 0
True  29 manager: no user.* event at all                               200 0 0                            200 0 0
True  30 admin sees the creation of both smoke users                   True                               True
True  31 no answer names lastError                                     0                                  0
True  32 ... or the reason of a rejected message                       0                                  0
True  33 set the smoke mapping Inactive                                200                                200
True  35 lock the smoke manager                                        200                                200
True  36 lock the smoke staff                                          200                                200
True  38 npm run build                                                 0                                  0
True  39 npm test                                                      0                                  0



PASSED: all 37 checks.
```

### Real-browser check (production build + `vite preview`, development environment, 1280 × 800)
Data: a smoke Manager and a smoke Staff, the test Admin `ui-check-61563` (unlocked for the check, locked again), a smoke
product mapped on the website with a `SIM-PERM-` SKU, a stock import of the Manager, an order of 500 units by the
Staff, two role changes by the Admin. Sessions were put into `sessionStorage` from the API's login answers (no
password was typed into the browser).
- **Manager, vi:** menu item "Nhật ký / kiểm toán", no "Người dùng". `/logs` became
  `/logs?since=2026-10-02T14%3A06%3A51Z`, 16 rows. Table 977 px in a 977 px area, no horizontal scrollbar on the page
  or the table; columns 140 / 126 / 160 / 240 / 130 / 180 px, no header cut, every row 40 px high (the three links
  "Đơn hàng Tác vụ Luồng" are 154 px on one line), widest level tag 86 px. Filters: presets 480 px, level and event
  type 360 px, the three id fields 320 px, on three lines. No row of a `user.*` type.
- **Manager, en:** the same widths, the table starts at the same height (319 px), the address unchanged; rows e.g.
  "Order short of stock | Requested 500, available 10", "Task failed | Stock sync · attempt 1 · channel_rejected",
  "Stock import | Document SMOKE-UI-018B-GRN-1835 · 1 lines · 10 units". Widest links 110 px.
- **Trace:** "Trace" on the row of the import leads to
  `/logs?since=…&correlationId=smoke-ui-018b-import-1835`; one request with that correlation id and the same window;
  two rows: the import and the `processing.failed` of the stock sync it caused, whose "Task" link is the failed task.
  The correlation field shows the id.
- **Admin:** the same list also has the user events of the run - "Role changed | Role: Staff → Manager |
  ui-check-61563@… → smoke-ui-018b-staff-1835@…", "Password reset", "User unlocked", "User created" (13 rows with
  "actor → target" on the first page); menu has "Users".
- **Staff:** no menu item; `/logs` shows "Không có quyền truy cập"; no request to `/api/system-events`.
- Finding fixed during the check: with the first widths (summary 200 px) 14 of 16 summaries were cut; summary is 240 px
  now (event type 160, who 130). Long lines are still cut with the full text in the tooltip.
- Clean-up: smoke mapping Inactive, the two smoke users and the test Admin locked, hosts and preview stopped, the
  temporary session file and `.claude/launch.json` deleted.

### Bundle (`npm run measure`)
| | first-load set raw | gzip | main chunk raw / gzip |
| --- | --- | --- | --- |
| before (018 report; A2 changed tests only) | 969.15 kB | 317.69 kB | 169.98 / 54.82 kB |
| after | 969.88 kB | 317.98 kB | 170.69 / 55.09 kB |

+0.73 kB raw: the route, the menu roles and the paths module of the page. `LogsPage` is its own chunk (11.82 kB raw /
4.78 kB gzip), loaded when `/logs` is opened.

## What was built
### Part B
- `SystemEventsController` (`ManagerOrAdmin`) passes `User.IsInRole(Admin)` to
  `ISystemEventService.ListAsync(query, includeUserEvents, …)`; `ICurrentUser` is untouched.
- The role rule is the first `WHERE` (`NOT EventType LIKE 'user.%'`), so it holds for the page and the count and
  whatever `eventType` asks for.
- Statements: `COUNT`, page (nine columns, the payload among them), and one `SELECT Id, Email … WHERE Id IN (…)` only
  when the page's returned details name a user. The stored payload is parsed and reduced in memory and never leaves
  the service.
- **Allowlist against the writers** (every `SystemEventLogs.Add` read): all fields of the task's table exist as
  written, with two exceptions decided as the task allows - `reason` is left out for both `…message_rejected` events:
  the parsers put `JsonException.Message` into it ("'p' is an invalid start of a value. Path: $ …") and the event type
  of the rejected message ("Unexpected event type '…'"), i.e. exception text and fragments of a body the application
  did not write. `errorMessage` of `processing.failed` is the attempt's sanitised message, the one the attempts API
  already returns.
- `SystemEventDetailsTests` finds the event classes by reflection (public static `…Events` classes of the Application
  assembly; pinned to the seven that exist) and fails for a constant without an entry and for an entry without a
  constant.
- **Doc deviation 18 confirmed:** `GET /api/system-events` exists as planned (paging contract, filters level / event
  type / correlation id / order / task / range ≤ 92 days, Manager and Admin, `user.*` Admin only, allowlisted
  `details`). No detail endpoint, no write.

### Part C
- Address: `?since=…` (default window) or `?range=today|7d|30d` or `?from=…&to=…`, `level` / `eventType` (one entry per
  value, in the order of the API's lists), `correlationId`, `orderId`, `taskId`, `sortDir`, `page`, `pageSize`.
- Summary lines come from `details` only (`eventSummary.ts`), with the labels of task types, alert types and roles
  the other pages use; a value without a label is shown as it is, a missing field as "—", an unknown type as its raw
  name without a summary.
- No rule in the client: which events a role reads and which fields exist is the server's. The page only decides which
  type options it offers (`user.*` for an Admin).

## Deviations from the task
1. **A2: the guard counts at the `fetch` call, not with MSW's life-cycle events** (measured reason above). The events
   were read from the installed version and used for the measurement.
2. **A2: the flagged tests were found with a temporary 120 ms answer delay**, because the plain guard flags none on a
   quiet machine. Most fixes are the shared `settled()` helper rather than a text per test.
3. **`reason` is not returned** for `order.message_rejected` and `stock_sync.message_rejected` (the task's footnote).
4. **`outbox.publish_failed` got a constant in Application** (`OutboxEvents.PublishFailed`); the map must reference
   constants and Application cannot reference Workers. The publisher's `PublishFailedEventType` now equals it (same
   value; existing tests unchanged).
5. **Time window of the page.** "The default is written to the address" is implemented as a start instant
   `?since=<UTC instant>` (now − 24 h), **without an end**: a reload asks for the same window, and "Refresh" shows events
   that arrived later. A fixed end would have hidden everything newer than the first opening. The menu link, "Clear
   filters" and choosing the preset again count the 24 hours from now. Question 1.
6. **Range validation = the alerts list** (inverted range and 92 days, the shared messages). The processing list, which
   the task names, only checks the inverted range.
7. **Presets are the dashboard's controls and constants, not a shared component** (there is none; the dashboard builds
   them inline). `presetLocalDays` was extracted in `dashboard/range.ts` so both pages compute local days the same way.
8. **A Manager's `user.*` type in the address is not sent** (it is not one of the Manager's options); the API would
   answer an empty page.
9. **The event-type filter renders without virtual scrolling** (14 options): with it jsdom shows 10 of them.
10. **Trace link keeps the time window, the sort direction and the page size**, drops the other filters and the page.
11. **The statement-count test lives in the Workers collection** with its own command counter (the only fixture that
    can add a DbContext interceptor); it counts synchronous commands too.
12. **Field names `level`, `eventType`** added to `FieldNames` (every validated field needs both names).
13. **`EXPLAIN` on hand-written SQL** equivalent to the EF queries, on a throwaway database (not development: 60,000
    seeded rows).
14. **Column widths** 140 / 126 / 160 / 240 / 130 / 180 after the browser check; `ID_FILTER_WIDTH` (320 px) is new in
    `layout.ts` so a whole GUID is visible.
15. **A1:** while the five runs were going I read files and wrote backend sources of 018b (no build, no test, nothing
    under `frontend/`); said in the 018 report. Its `Commit:` line was completed as well.
16. **Browser check:** driven by DOM clicks and session injection, on the development environment; no keyboard pass.

## Blocked / questions for Cowork
1. **Default window (deviation 5).** Accept the open-ended `since`, or should the window have a fixed end (then
   "Refresh" needs to move it)?
2. **Guard at the `fetch` boundary (deviation 1).** Accept? The alternative that keeps MSW's events needs the same
   wrapper to close the 19 ms gap.
3. **Three order-entry tests count list refetches differently under latency** (`orderEntry.test.tsx` "outcome by
   push": 3 instead of 2, 2 instead of 1 with a 120 ms answer delay; green without it, 5 of 5). Probably the resync of
   a new connection arriving while the first answers are still on their way - behaviour of the application, not a
   request in flight. Not changed. Worth a look in a Part A?
4. **A successful order writes no system event.** A trace of a normal order therefore shows nothing on this page (its
   task is on the processing page); only rejected orders, failures, retries, imports, acknowledgements and user
   management are events. Is an event for a reserved order wanted (a new event type, out of scope here)?
5. **`reason` of rejected messages** is not shown at all. If operators need it on the page, the writers would have to
   store a code next to the text (a change of what an event writes - out of scope).

## Manual steps for the user
None required: no migration, the smoke script ran on the development environment.
Optional (3 minutes, `npm run dev` + API + Workers, a Manager account): open **Nhật ký / kiểm toán**, import stock for
a product, press "Làm mới" - the import is the first row; click "Luồng" on it; use Tab / Enter through the filters once
(the keyboard pass I could not do).

## Out of scope / follow-ups
- The three latency-sensitive counts of question 3.
- 026: `COUNT` over wide ranges, an index for rare event types in long ranges, retention of `SystemEventLogs`.
- Export of logs (021), realtime hints for events, a detail route.
- The DLQ keeps one `SIM-PERM-` message per smoke run (known, review 016b); this task's smoke and browser runs added
  four smoke users (locked), two smoke products and one smoke customer to the development database.

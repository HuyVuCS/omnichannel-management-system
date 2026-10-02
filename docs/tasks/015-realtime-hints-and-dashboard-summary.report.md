# Report 015 — Realtime hints (PostgreSQL NOTIFY → SignalR) + dashboard summary

Status: Done
Commits: `55993e4` (A1 stale stock-sync task), `47ff45a` (A2 GET of one mapping), `a41165a` (A3 README section 6),
`f74251b` (Parts B and C with their tests), `d15281e` (README realtime section, A4 `.http` samples, smoke script),
`945e9a8` (one test strengthened after the mutation run)

## Summary
- **Part B:** `RealtimeHintInterceptor` (Infrastructure, API and Workers) captures order, stock and task changes of every
  unit of work and sends one `pg_notify('omnichannel_ui', …)` after the save; `PostgresHintListener` (API) listens on its
  own connection, batches the hints (`Realtime:BatchWindow`, 250 ms) and pushes them to the hub
  `/api/hubs/notifications` as message `hints` `{ events, resync }`. JWT from the `access_token` query parameter for the
  hub only.
- **Part C:** `GET /api/dashboard/summary` — nine SQL aggregates, no entity loaded.
- **Part A:** A1 fixed with three integration tests (the old order of the checks fails all three), A2 endpoint, A3 and A4
  text.
- Backend: build 0 warnings / 0 errors, **745/745** tests (505 unit, 240 integration). **Frontend untouched** (no file
  under `frontend/` changed; not built or tested). **No migration.**
- `scripts/smoke/task-015.ps1`: **27/27**, against the real API and Workers hosts on a throwaway PostgreSQL + RabbitMQ
  pair, with a database role that has DML rights only (so `LISTEN` / `pg_notify` are shown to need no grant).
- 15 mutations (14 in Parts B/C, 1 for A1), all caught; one of them only after a test was strengthened (below).
- One thing went wrong on the way and touched the development environment for a few seconds — see "Incident".

## Part A
- **A1 (`55993e4`, review 014 P2):** `StockSyncService.ResolveInventoryChangeAsync` looks the task up by its idempotency
  key first; the channel/mapping check only decides whether a *new* task is created. The handler then starts an attempt
  and `PushCurrentStockAsync` completes the inactive target as `Succeeded` without a push; the final-failure path uses the
  same method and now fails the existing task. Tests (`StockSyncDeactivationTests.cs`, a partial of
  `StockSyncWorkerTests`): mapping deactivated while a retry waits; channel deactivated while a retry waits; delivery
  limit on a message of a deactivated mapping → task `Failed` (`delivery_limit_reached`), `processing.failed`, DLQ.
  Mutation (old order of the checks): all three failed. Cost: a message for a product the channel does not sell now
  takes two small queries (channel, then task by key) instead of one.
- **A2 (`47ff45a`):** `GET /api/channels/{channelId}/products/{channelProductId}` (AnyStaff), same item shape as the
  list, 404 `channel_product_not_found` for an unknown mapping, another channel's mapping and an unknown channel. Test:
  the `Location` of a 201 is followed by a Staff client and returns the mapping.
- **A3 (`a41165a`):** README section 6 describes the three workers; "no consumer yet" is gone.
- **A4 (in `d15281e`):** `Omnichannel.Api.http` has requests for the mapping endpoints, the stock-sync task filter, the
  summary and the hub's negotiate (token from the login response, sent as header; no secret, no token in a URL).

## Part B — what exists
- `Application/Realtime/RealtimeHints.cs`: channel name, event types, `RealtimeHint`, `RealtimeHintBatch` (payload of
  one `NOTIFY`), `RealtimeHintMessage` (hub message), `RealtimeJson`.
- `Infrastructure/Realtime/RealtimeHintInterceptor.cs`, registered in `AddInfrastructure` after the auditing interceptor.
  Capture in `SavingChanges` (one pass over the change tracker), send in `SavedChanges`, forget on failure/cancel.
- `Api/Hubs/`: `NotificationsHub` (no methods, `AnyStaff`), `PostgresHintListener` (hosted service), `RealtimeHintBatcher`,
  `RealtimeOptions` (+ validator, 10 ms – 5 s). `Api/Middleware/AccessTokenRedactionEnricher.cs`.
  `AuthServiceCollectionExtensions`: `OnMessageReceived` for the hub path. `Program.cs`: SignalR, hub mapping with
  `CloseOnAuthenticationExpiration`.

**Which `pg_notify` case applies to which producer**

| Producer | Unit of work | Case |
| --- | --- | --- |
| Stock import (`InventoryImportService`) | explicit transaction | delivered at the commit; a failing notify fails the transaction |
| Order Worker: start attempt, reservation (order + movements + task), insufficient-stock order, transient / final failure | explicit transactions (`ProcessingAttemptStore`, `OrderReservationService`) | same |
| Stock Sync Worker: start attempt, complete, transient / final failure | explicit transactions | same |
| Manual retry (`ProcessingTaskService.RetryAsync`) | explicit transaction | same |
| Order intake (`OrderIntakeService`) | one `SaveChanges`, no explicit transaction | sent after EF Core's commit; a failure is logged at Warning and the request succeeds |
| Mapping create / update (`ChannelService`) | one `SaveChanges` | same |
| Stock-sync task created from `inventory.changed` (`ResolveInventoryChangeAsync`) | one `SaveChanges` | same |
| Outbox Publisher, users, catalog, rejected-message log rows | — | nothing captured, no `NOTIFY` sent |

Both statements were verified against the running database (`RealtimeNotifyTests`): inside a transaction nothing is
delivered and no other connection sees the row until the commit, and a rollback delivers nothing; without a transaction
the row is visible to another connection at the moment the notification arrives. The mutant that sends the notification
on a second connection (i.e. outside the transaction) fails both transaction tests.

Other answers the task asked for:
- **`LISTEN` needs no grant.** Test: a role without any table privilege listens and receives, and is denied a `SELECT`.
  Smoke: API and Workers ran with a role that has only SELECT/INSERT/UPDATE/DELETE.
- **Rate limiting:** there is no global limiter; the only policy (`login`) is attached to the login endpoint.
  `negotiate` is not rate limited.
- **CORS:** the hub is covered by the existing frontend policy (`UseCors` is global); nothing was changed.
- **Payload size:** at most 100 product ids per `inventory.changed`, more → `all: true`. In addition, if the whole
  payload of one save still does not fit (many tasks or orders in one unit of work: 25 task hints fit, 30 do not), every
  event type is reduced to one `{type, all: true}`.

**The token and the logs.** What logs the request URL: `Microsoft.AspNetCore.Hosting.Diagnostics` ("Request starting /
finished", with the query string) at Information. `appsettings.json` puts `Microsoft.AspNetCore` at Warning, so today it logs nothing; but one
configuration change would put tokens into the log. `AccessTokenRedactionEnricher` replaces the value of every
`access_token=` in any text property of a log event. Serilog's request logging logs the path without the query; SignalR
and HTTP-connection logs at Debug contain connection ids, no URL. The test host captures these categories at Information /
Debug and the test asserts: the token (and its signature part alone) is in no event, and a hosting entry with
`access_token=***` exists. Without the enricher that test fails (mutant M06).

API log of the smoke run (Development, `Omnichannel.Api.Hubs` at Debug, hosting diagnostics at Information):
```
[17:19:46 INF] Microsoft.AspNetCore.Hosting.Diagnostics: Request starting HTTP/1.1 POST http://localhost:5081/api/hubs/notifications/negotiate?negotiateVersion=1&access_token=*** - application/json 0 {"EventId": {"Id": 1}, "RequestId": "0HNP0DQN4QJ9E:00000009", "RequestPath": "/api/hubs/notifications/negotiate", ...}
[17:19:46 INF] Microsoft.AspNetCore.Hosting.Diagnostics: Request finished HTTP/1.1 POST http://localhost:5081/api/hubs/notifications/negotiate?negotiateVersion=1&access_token=*** - 200 316 application/json 4.3352ms {...}
[17:19:47 INF] Omnichannel.Api.Hubs.PostgresHintListener: Listening for realtime hints on channel omnichannel_ui {"Application": "Omnichannel.Api"}
[17:19:47 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hints received: inventory.changed {"Application": "Omnichannel.Api"}
[17:19:47 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hints received: processing.task.changed {"Application": "Omnichannel.Api"}
[17:19:47 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hint batch sent to the clients: 2 events (inventory.changed, processing.task.changed), resync False {"Application": "Omnichannel.Api"}
[17:19:50 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hints received: processing.task.changed {"Application": "Omnichannel.Api"}
[17:19:50 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hints received: order.created, inventory.changed, processing.task.changed {"Application": "Omnichannel.Api"}
[17:19:50 DBG] Omnichannel.Api.Hubs.PostgresHintListener: Hint batch sent to the clients: 4 events (processing.task.changed, order.created, inventory.changed), resync False {"Application": "Omnichannel.Api"}
```
The whole log of that run has four lines with `access_token`, all `access_token=***`, and no line with a JWT (`eyJ`).
The first batch is the stock import and the order intake (two units of work, one message); the second is the Order
Worker's "attempt started" and its reservation (one notification with three hints).

## Part C — what exists
`Application/Dashboard` (contracts, validator, `DashboardRange.Resolve`, `IDashboardService`),
`Infrastructure/Persistence/Dashboard/DashboardService.cs`, `Api/Controllers/DashboardController.cs`. Nine statements:
channels, orders by channel and status, product count, low-stock count (`InventoryPredicates.IsLowStock`, the predicate
of the inventory list's filter), out-of-stock count, tasks by type and status, stock sync per channel, outbox pending,
outbox failed. Lists are completed with zeros in memory (every order status, task type, channel).

**Plans** (throwaway database after the smoke run plus seeded rows: 8,001 orders over 116 days, 24,001 tasks, 40,003
outbox rows; `ANALYZE` run; SQL exactly as EF Core generates it, with `now()` for the parameters):
```
1. orders, last 24 hours                                              Execution Time: 0.081 ms
 HashAggregate (actual rows=6)  Group Key: "ChannelId", "Status"
   ->  Index Scan using "IX_Orders_CreatedAt" on "Orders" o (actual rows=69)
         Index Cond: (("CreatedAt" >= (now() - '24:00:00'::interval)) AND ("CreatedAt" <= now()))

1b. orders, 92 days                                                    Execution Time: 2.665 ms
 HashAggregate (actual rows=6)
   ->  Seq Scan on "Orders" o (actual rows=6309)   Rows Removed by Filter: 1692

2. tasks by type and status, last 24 hours                             Execution Time: 4.599 ms
 GroupAggregate (actual rows=7)  Group Key: "TaskType", "Status"
   ->  Sort (actual rows=246)
         ->  Seq Scan on "ProcessingTasks" p (actual rows=246)   Rows Removed by Filter: 23755

3. stock sync per channel                                              Execution Time: 3.862 ms
 HashAggregate (actual rows=3)  Group Key: "ChannelId"
   ->  Bitmap Heap Scan on "ProcessingTasks" p (actual rows=12000)   Filter: ("TaskType" = 'StockSync')
         ->  Bitmap Index Scan on "IX_ProcessingTasks_ChannelId" (actual rows=12000)

4. outbox pending                                                      Execution Time: 0.018 ms
   ->  Index Scan using "IX_OutboxEvents_Pending_CreatedAt" on "OutboxEvents" o

5. outbox failed                                                       Execution Time: 3.041 ms
   ->  Seq Scan on "OutboxEvents" o (actual rows=8)   Rows Removed by Filter: 39995
```
The whole endpoint on that database, as logged by the API: 23–32 ms (three calls, 24 hours), 30 ms for a 91-day range.
**No index was added:** nothing
measured justifies one at this size. Three of the statements grow with the table, not with the range — see follow-ups.

## Files changed
- Production: `Omnichannel.Application/Realtime/RealtimeHints.cs`, `Application/Dashboard/*` (new);
  `Application/Channels/IChannelService.cs`, `IStockSyncService.cs` (doc); `Application/Common/Validation/ValidationMessages.cs`,
  `Resources/ValidationMessages(.vi).resx` (`DateRangeEmpty`, `DateRangeTooLong`);
  `Omnichannel.Infrastructure/Realtime/RealtimeHintInterceptor.cs`, `Persistence/Dashboard/DashboardService.cs` (new);
  `Persistence/Channels/StockSyncService.cs`, `ChannelService.cs`, `DependencyInjection.cs`;
  `Omnichannel.Api/Hubs/*` (new), `Middleware/AccessTokenRedactionEnricher.cs`, `Controllers/DashboardController.cs` (new),
  `Controllers/ChannelsController.cs`, `Auth/AuthServiceCollectionExtensions.cs`, `Program.cs`, `Omnichannel.Api.http`.
- Tests (new): `Realtime/RealtimeHintUnitTests.cs`, `Validation/DashboardSummaryQueryTests.cs`,
  `Integration/HintProbe.cs`, `RealtimeNotifyTests.cs`, `RealtimeHubTests.cs`, `DashboardSummaryTests.cs`,
  `Integration/Workers/RealtimeHintProducerTests.cs`, `StockSyncDeactivationTests.cs`; changed: see deviation 9.
- `Directory.Packages.props`, `Omnichannel.Tests.csproj`: `Microsoft.AspNetCore.SignalR.Client` 10.0.12 (test project only).
- `scripts/smoke/task-015.ps1` (new), `README.md`.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend, final code) | **745 passed**, 0 failed (1 m 24 s) |
| `dotnet test --filter "Category!=Integration"` | 505 passed |
| existing suite with the interceptor and the listener, before any new test | 673 passed (669 of 014 + 4 of Part A) |
| `dotnet ef database update` | throwaway database only (to create it); no new migration exists |
| `scripts/smoke/task-015.ps1` (throwaway environment) | **PASSED: all 27 checks** |
| frontend | not built, not tested: no file changed |

Smoke run (PostgreSQL 17 + RabbitMQ 4 containers started for it, API on port 5081 and Workers from the build output,
database role with DML rights only; containers removed afterwards):
```
== Task 015 - realtime hub and dashboard summary ==
Using the API already running at http://localhost:5081 (restart it first if it runs an older build).
Using the Workers host already running (restart it first if it runs an older build).
Waiting for the Order Worker (up to 60 s)...
Step                                                 Expected    Actual   OK
----                                                 --------    ------   --
01 create smoke manager                                   201       201 True
02 create smoke staff                                     201       201 True
03 hub negotiate without a token                          401       401 True
04 hub negotiate with the token in the header             200       200 True
05 hub negotiate with the token in the query string       200       200 True
06 ... answers with a connection                         True      True True
07 another path ignores a token in the query string       401       401 True
08 staff reads the summary                                200       200 True
09 ... default range is 24 hours                           24        24 True
10 ... lists the three stock-sync channels                  3         3 True
11 create smoke customer                                  201       201 True
12 create smoke product                                   201       201 True
13 import 5 units                                         200       200 True
14 staff submits an order (website, 2 units)              202       202 True
15 order task                                       Succeeded Succeeded True
16 summary after the order                                200       200 True
17 orders.total grew by 1                                   1         1 True
18 website: total grew by 1                                 1         1 True
19 website: reserved grew by 1                              1         1 True
20 sales amount grew by 2 x 15000                       30000  30000.00 True
21 OrderProcessing succeeded grew                        True      True True
22 outbox.failed unchanged                                  0         0 True
23 products grew by 1                                       1         1 True
24 from after to                                          400       400 True
25 ... reported on "to"                                  True      True True
26 lock the smoke manager                                 200       200 True
27 lock the smoke staff                                   200       200 True
PASSED: all 27 checks.
```
The script's own start of the Workers host was not exercised this time (it was in 014); its start of the API was, by
accident — see "Incident".

### Mutations (mutate, build, run the test named, restore)
| # | Mutation | Caught by |
| --- | --- | --- |
| A1 | mapping check before the lookup of the existing task | the three tests of `StockSyncDeactivationTests.cs` |
| M01 | hint for every modified task row (status not compared) | `RealtimeHintCaptureTests` "Changed task status gives a hint and an unchanged status does not" — **only after the test was extended** (`945e9a8`): its first version never produced a modified row with an unchanged status |
| M02 | no hint for a new task | "Added task gives processing task changed with its ids" |
| M03 | product list not capped | "More products than the cap …", "Stock import is one notification whose product list is capped" (200 lines) |
| M04 | oversized payload not reduced | "A unit of work whose hints do not fit is reduced …" |
| M05 | query-string token accepted on every path | "Negotiate needs a token and accepts it in the query string for the hub only" |
| M06 | no redaction of the token in log events | "Access token of the query string appears in no log event" |
| M07 | no `resync` after the listener reconnected | "Listener reconnects after losing its connection …" |
| M08 | batch window a thousand times shorter | "Hints within the window go out as one message in arrival order" |
| M09 | end of the range excluded for orders | "Orders and finished tasks are counted in the range only" |
| M10 | sales amount counts every status | same |
| M11 | low stock with a rule of its own (`<` instead of the shared predicate) | "Current numbers ignore the range and match independent counts" |
| M12 | finished tasks counted by `CreatedAt` | "Orders and finished tasks are counted in the range only" |
| M13 | range limit 920 days | "The range is at most 92 days", "Invalid range is rejected on its field" |
| M14 | notification sent on a second connection (outside the transaction) | "Rolled back transaction delivers nothing", "Save inside a transaction is delivered at the commit not before" |

Not mutated because no test can tell the difference: clearing the captured hints in `SaveChangesFailed` (the next save
overwrites them anyway — the clearing is defensive), and removing `[Authorize(Policy = AnyStaff)]` from the hub (the
fallback policy still demands an authenticated user, and every user has one of the three roles).

### Required tests → where
| # | Requirement | Tests |
| --- | --- | --- |
| Unit | hint capture (each entity case, unchanged status, id cap, payload size) | `RealtimeHintCaptureTests` (8), `RealtimeHintPayloadTests` |
| Unit | summary validator (defaults, order, range limit, keys) | `DashboardSummaryQueryTests` |
| Unit (extra) | batching, options, token redaction | `RealtimeHintBatcherTests`, `AccessTokenRedactionTests` |
| 1 | commit / rollback / failed save / implicit transaction, on a separate `LISTEN` connection | `RealtimeNotifyTests` (7 methods, 8 cases), incl. the 200-line import and the unprivileged role |
| 2 | producers end to end | `RealtimeHintProducerTests` (import, mapping, intake, Order Worker reservation as one notification with three hints, Stock Sync success and final failure, manual retry) |
| 3 | hub with a real SignalR client | `RealtimeHubTests`: negotiate 401 / query token / other path; hint after a stock import; two saves → one message in order; changed security stamp → 401; token in no log event |
| 4 | listener resilience | `RealtimeHubTests` "Listener reconnects …" (`pg_terminate_backend` → `resync: true` → hints again) |
| 5 | summary | `DashboardSummaryTests` (range numbers in a window of their own, zeros, current numbers against independent counts and the inventory endpoint, defaults, 400 cases in en and vi, roles) |
| 6 | Part A; existing tests | see Part A; existing tests green, changes to existing files in deviation 9 |

## Incident: the API ran against the development database for a few seconds
My first start of the throwaway API failed (an environment variable with dots cannot be set in a Bash prefix), and I ran
the smoke script before checking that it was up. The script therefore did what it is built to do: it started the API
itself, with the development user-secrets, on port 5081, sent one login with the throwaway admin e-mail (401, no such
user), reported the failure and stopped the API. Effects on the development environment: the new build ran its start-up
(bootstrap-admin check, the hint listener's `LISTEN`) and one rejected login was counted by the rate limiter. No row was
written by the script. The Workers host was not started there. After that the API was started correctly and its log was
checked to contain the throwaway connection before the script ran again.

## Deviations from the task
1. **`AccessTokenRedactionEnricher`** is more than "check the log sources": the check showed that the hosting category
   would log the token at Information, and a configuration change must not be able to leak it.
2. **Second size guard for the payload** (per-type `all` when the payload of one save does not fit) on top of the
   product-id cap; and in the API a batch above 200 events, or a full queue (10,000 notifications), becomes `resync`.
3. **`byStatus`, `processing` and `stockSync` are lists** (`[{status, count}]`, `[{taskType, …}]`, `[{channelId, slug, …}]`),
   not objects keyed by an enum name: keys would be PascalCase while everything else is camelCase.
4. **Range validation in two places with the same messages:** the validator when both ends are given (key `to`),
   `DashboardRange.Resolve` when only `from` is given and the default `to` = now makes the range empty or too long (key
   `from`). New messages `DateRangeEmpty`, `DateRangeTooLong` (vi/en). `from` must be strictly before `to`.
5. **Listener, batcher and options live in `Api/Hubs`**; the listener reuses `RetryBackoff` from
   `Infrastructure.RabbitMQ` (a pure function) for its reconnect delay (1 s doubling to 30 s).
6. **The hub tests use long polling** with the token in the query string (the test server has no sockets); the
   WebSocket transport itself is not exercised before the browser check of 016.
7. **A4 is not a commit of its own** (it is in `d15281e` with the README and the smoke script).
8. **The 201 `Location` stays a relative URL** (`Created(...)`, as the 014 test asserts); it resolves through the new GET.
9. **Existing test files:** `StockSyncWorkerTests` became `partial` (declaration only); `ApiFactory` captures four more
   log categories and got `IssueTokenFor`; `WorkersFixture` exposes its connection string. No existing test body changed.
10. **Summary: nine statements** rather than fewer, larger ones: each is a plain aggregate and the shared low-stock
    predicate is used as it is.

## Blocked / questions for Cowork
None blocking.
- **Growth of three summary statements** (follow-up below): accept until 026, or add partial indexes now?
- **`stockSync.lastSucceededAt`** is "the most recent success ever", not bound to the range (as specified); it is the
  statement that reads every stock-sync task. If "in the range" is enough for the dashboard, it becomes cheap.

## Out of scope / follow-ups
- **Statements that grow with the table:** tasks by type and status (sequential scan, 24,001 rows → 4.6 ms), stock sync
  per channel (reads every stock-sync task, 12,000 → 3.9 ms), outbox failed (sequential scan of the outbox, 40,003 →
  3.0 ms). Candidates for 026: a partial index on `OutboxEvents` for `Status = 'Failed'`, a partial index on
  `ProcessingTasks` for non-terminal statuses, an index with `UpdatedAt` for finished tasks.
- **One extra round trip per save that has hints** (`SELECT pg_notify`). Not measured under load (026).
- **Hints go to every connected user** whatever the role (per-role filtering was out of scope; hints carry ids only).
- **A token in a query string** can still be logged outside the application (reverse proxy, load balancer): 025/026.
- **A silently dead listener connection** (no TCP reset) is noticed by the 30 s keep-alive; hints in between are lost and
  the clients get `resync` after the reconnect.
- **016:** the Vite dev proxy needs WebSocket forwarding for `/api/hubs`; clients must refetch on `resync`, on their own
  reconnect, and treat `all: true` as "everything of this type".
- **`order.status.changed`** does not exist (no order changes state after creation yet).
- The smoke script takes a running API on the configured port for granted and otherwise starts one from the developer's
  user-secrets — the cause of the incident above. A check that `SMOKE_BASE_URL` is the default before starting anything
  would have prevented it (small change to `_common.ps1`, not done here).

## Manual steps for the user
No migration, no new settings required.
1. Restart the API and the Workers host (new build).
2. Smoke on the development environment: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-015.ps1` and
   paste the table.
3. Manual check (2 minutes): start the API with the listener at Debug, e.g.
   `${env:Serilog__MinimumLevel__Override__Omnichannel.Api.Hubs} = 'Debug'` (the braces are needed: the name contains
   dots) before `dotnet run`, start the Workers and the
   frontend, submit an order on the order-entry page, and find in the API log `Hints received: …` followed by
   `Hint batch sent to the clients: … events (…), resync False`. Nothing changes on screen yet: the client side is 016.

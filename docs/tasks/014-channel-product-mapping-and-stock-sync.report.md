# Report 014 — ChannelProduct mapping + Stock Sync Worker (simulated channels)

Status: Done
Commits: `6b62354` (A1 expected unique violations not logged as errors), `bc68cc7` (A2 stock-import form),
`4d06813` (Part B: mapping API, stock sync worker, simulator, migration, smoke script, README), `0c612a4` (README wording)
Manual steps left for the user (the migrator credentials are not available to Claude Code): apply the StockSync
migration to the development database, then run the smoke script there - see the last section.

## Summary
- **Part B:** mapping API on `/api/channels/{channelId}/products`, Stock Sync Worker (one consumer per channel queue),
  simulated channel adapter behind a Polly timeout + circuit breaker, directed `stock.sync.requested` messages
  (`OutboxEvent.RoutingKey`) for initial sync and manual retry, `ProcessingTask.ChannelId` / `ProductId`, migration
  `20261002053501_StockSync`.
- **Part A1:** the observation changed the mechanism. EF Core writes **two** Error entries for a mapped 23505 and the
  first one (`CommandError`) carries **no exception**, so a Serilog filter keyed on the exception chain cannot recognise
  it. Implemented at EF Core level instead (deviation 1). **Part A2:** one line + test.
- Backend: build 0 warnings / 0 errors, **669/669** tests (459 unit, 210 integration). Frontend: typecheck, lint,
  **168/168** tests, build. Polly: `Polly.Core` **8.8.0**.
- `scripts/smoke/task-014.ps1`: **38/38**, run against the real API and Workers hosts — but on a **throwaway**
  PostgreSQL + RabbitMQ pair (containers started for this run, all migrations applied there, removed afterwards), not on
  the development database, which does not have the new migration yet.
- 18 mutations (16 in Part B, one each for A1 and A2), each caught by a test (table below).
- Existing order tests are green; four of them needed a mechanical change (deviation 3).

## Part A
### A1 — Expected unique violations are not logged as errors by EF Core (`6b62354`)
**Observed first** (integration test host, real Serilog pipeline, a re-submitted stock import → 409
`stock_import_already_applied`). EF Core logs two entries, both at Error:

| # | Category (`SourceContext`) | Event id | Level | Exception attached |
| --- | --- | --- | --- | --- |
| 1 | `Microsoft.EntityFrameworkCore.Database.Command` | 20102 `CommandError` | Error | **none** (SQL text only) |
| 2 | `Microsoft.EntityFrameworkCore.Update` | 10000 `SaveChangesFailed` | Error | `DbUpdateException` → `PostgresException` (23505, constraint name) |

Before, registered conflict (`UX_InventoryMovements_Reference`), trimmed:
```
[12:23:23 ERR] Microsoft.EntityFrameworkCore.Database.Command: Failed executing DbCommand (1ms) [Parameters=[@p0='?' (DbType = Guid), ...], CommandType='"Text"', CommandTimeout='30']
INSERT INTO "InventoryMovements" ("InventoryMovementId", "CreatedAt", ...) VALUES (@p0, @p1, ...);
... {"EventId":{"Id":20102,"Name":"Microsoft.EntityFrameworkCore.Database.Command.CommandError"},...,"RequestPath":"/api/inventory/import",...}
[12:23:23 ERR] Microsoft.EntityFrameworkCore.Update: An exception occurred in the database while saving changes for context type 'Omnichannel.Infrastructure.Persistence.OmnichannelDbContext'.
Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes. See the inner exception for details.
 ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint "UX_InventoryMovements_Reference"
   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(...)   (stack continues)
[12:23:23 INF] Omnichannel.Api.Middleware.GlobalExceptionHandler: Request failed with 409: stock_import_already_applied {...}
[12:23:23 INF] Serilog.AspNetCore.RequestLoggingMiddleware: HTTP POST /api/inventory/import responded 409 in 60.5785 ms {...}
```
After, the same request (everything the host logged for it):
```
[12:25:22 INF] Omnichannel.Api.Middleware.GlobalExceptionHandler: Request failed with 409: stock_import_already_applied {...}
[12:25:22 INF] Serilog.AspNetCore.RequestLoggingMiddleware: HTTP POST /api/inventory/import responded 409 in 44.5333 ms {...}
```
Unregistered violation (a unique index that exists only in the test database, `UX_Test_Customers_Name`) — before and
after are the same four entries; after:
```
[12:25:22 ERR] Microsoft.EntityFrameworkCore.Database.Command: Failed executing DbCommand (1ms) [Parameters=[@p0='?' (DbType = Guid), ...], CommandType='Text', CommandTimeout='30']
INSERT INTO "Customers" ("CustomerId", "Address", ...) VALUES (@p0, @p1, ...); {"EventId":{"Id":20102,"Name":"Microsoft.EntityFrameworkCore.Database.Command.CommandError"},...}
[12:25:22 ERR] Microsoft.EntityFrameworkCore.Update: An exception occurred in the database while saving changes for context type '...OmnichannelDbContext'.
Microsoft.EntityFrameworkCore.DbUpdateException: ... ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint "UX_Test_Customers_Name"
[12:25:22 ERR] Omnichannel.Api.Middleware.GlobalExceptionHandler: Unhandled exception {...}
[12:25:22 ERR] Serilog.AspNetCore.RequestLoggingMiddleware: HTTP POST /api/customers responded 500 in 35.5803 ms {...}
```
(The only visible difference: `CommandType='Text'` instead of `'"Text"'`.)

What was built:
- `Persistence/ExpectedUniqueViolations.cs` — the one registry (8 constraint names: channel slug, product SKU, user name,
  movement reference, processing-task idempotency key, order idempotency, and the two mapping indexes). Every constant of
  the class is registered (the set is built from the constants). The index configurations (`HasDatabaseName`) and all
  catch clauses use these constants; the old constants in the configuration classes are gone.
  `IsUniqueViolation(name)` never matches a name that is not registered, so a service cannot map a conflict that would
  still be logged as an error.
- `Logging/DatabaseFailureLogInterceptor.cs` — EF Core's two failure events are configured at Debug
  (`ConfigureWarnings(w => w.Log((CommandError, Debug), (SaveChangesFailed, Debug)))`); the interceptor, which receives
  the exception for both events, writes them again at Error with EF Core's category, event id and message, unless the
  cause is a 23505 on a registered constraint. Registered once in `AddInfrastructure`, so the API and the Workers host
  both have it without host code.
- Tests (`DatabaseFailureLogTests`, real exceptions from the test database): registered conflict → no entry at Warning+
  and one Information `Request failed with 409`; unregistered index → both Error entries; foreign-key violation (23503)
  → both Error entries; failed command outside `SaveChanges` (`SELECT 1 / 0`, 22012) → the `CommandError` entry; a name
  outside the registry never matches a catch clause. Workers host: `StockSyncWorkerTests` › "Expected unique violation is
  not logged ... in the workers host" (EF Core's entries are there, at Debug).
- Mutation (the interceptor never writes): 3 of the 6 tests failed.

### A2 — Stock-import form: stale list error (`bc68cc7`)
`StockImportForm` clears the API error of the `items` list when the form is submitted
(`form.setFields([{ name: 'items', errors: [] }])` in `onFinish`). Test `stockImport.test.tsx` › "removes the list error
of the previous attempt on the next submit". Mutation (line removed): the test failed.

## Part B — what exists
- **Data model** (migration `StockSync`): `ProcessingTasks.ChannelId` / `ProductId` (nullable, FK restrict, indexed) with
  CHECK `CK_ProcessingTasks_StockSync_ChannelAndProduct`; `OutboxEvents.RoutingKey` (varchar 255, null = event type).
  `ProcessingTask.ForStockSync(...)` requires both ids; `ChannelProduct.Update(...)` (at least one external id, trimmed,
  ≤ 128, blank → null) returns whether the channel has to be synced again. The two mapping indexes keep their database
  names (`IX_ChannelProducts_ChannelId_ProductId`, `IX_ChannelProducts_ChannelId_ExternalProductId`): the migration
  contains no index operation for them.
- **Messaging:** `stock.sync.requested` + `StockSyncRequestedPayload`; the publisher uses `RoutingKey ?? EventType`;
  `RabbitMqTopology.StockSyncQueues` (slug → queue) and one extra binding per stock-sync queue (routing key = queue name).
  No queue argument changed.
- **Mapping API** in `IChannelService` / `ChannelService` and `ChannelsController`: list, create, update as specified;
  mapping, task and directed outbox row are one `SaveChanges` (one transaction); both 409s come from the unique indexes.
- **Processing API:** `channelId`, `productId` on task summary / detail / order task item; `channelId` filter; manual
  retry of a `StockSync` task writes a directed `stock.sync.requested`.
- **Channel adapter:** `IChannelStockClient`, `ChannelStockUpdate`, `ChannelApiException`, `ChannelApiErrorCodes`
  (Application); `SimulatedChannelStockClient` (`ChannelSimulation` through `IOptionsMonitor`, modes, latency, SKU rules),
  `ResilientChannelStockClient` (one pipeline per slug: circuit breaker outside, timeout inside, no retry). Registered in
  the Workers host only.
- **Worker:** `StockSyncWorkerService` (three `ResilientQueueConsumer`s), `StockSyncMessageHandler`,
  `StockSyncMessageParser`, `StockSyncWorkerOptions` (+ validator); persistence in `IStockSyncService` /
  `StockSyncService`. `ProcessingErrorClassifier` moved to `Omnichannel.Workers/Processing` and knows `ChannelApiException`.

## Files changed
Backend, production code:
- `Omnichannel.Domain/Entities/ProcessingTask.cs`, `OutboxEvent.cs`, `ChannelProduct.cs` — new columns, factory, rules.
- `Omnichannel.Application/Channels/` — `ChannelProductContracts.cs`, `ChannelProductValidators.cs`,
  `IChannelStockClient.cs`, `IStockSyncService.cs` (new); `IChannelService.cs` (mapping operations).
- `Omnichannel.Application/Common/Messaging/` — `EventTypes.cs`, `MessageEnvelope.cs`, `StockSyncRequestedPayload.cs`.
- `Omnichannel.Application/Processing/ProcessingContracts.cs`, `IProcessingTaskService.cs`,
  `Orders/OrderQueryContracts.cs` — `channelId` / `productId`, `channelId` filter.
- `Omnichannel.Application/Resources/*.resx`, `Common/Validation/ValidationMessages.cs` — `ExternalIdRequired`,
  field names `externalProductId`, `externalSku` (vi/en).
- `Omnichannel.Infrastructure/Persistence/` — `ExpectedUniqueViolations.cs`, `DbUpdateExceptionExtensions.cs` (A1);
  `Channels/ChannelService.cs`, `Channels/StockSyncService.cs`, `Channels/StockSyncRequests.cs`;
  `Processing/ProcessingAttemptStore.cs` (new, shared attempt bookkeeping), `Processing/ProcessingTaskService.cs`;
  `Orders/OrderReservationService.cs` (delegates to the store), `Orders/OrderQueryService.cs`; configurations of
  `ProcessingTask`, `OutboxEvent`, `ChannelProduct` and the five that used the old index-name constants; migration
  `20261002053501_StockSync` (+ designer, snapshot).
- `Omnichannel.Infrastructure/Logging/DatabaseFailureLogInterceptor.cs` (A1), `DependencyInjection.cs`.
- `Omnichannel.Infrastructure/ExternalChannels/` — simulator, options, resilient client, registration (new folder).
- `Omnichannel.Infrastructure/RabbitMQ/` — `RabbitMqTopology.cs` (map + bindings), `QueueMessageHandling.cs` and
  `RabbitMqQueueConsumer.cs` (`HandlerResult.Abandon`), `RetryPublisher.cs` (logs the delay tier).
- `Omnichannel.Api/Controllers/ChannelsController.cs` — three endpoints.
- `Omnichannel.Workers/` — `StockSyncWorker/*` (new), `Processing/ProcessingErrorClassifier.cs` (moved + channel errors),
  `WorkerHostSetup.cs`, `OutboxPublisher/OutboxPublisherService.cs` (routing key), `appsettings.Development.json` (new:
  `ChannelSimulation` with `SkuRules: true`), `Program.cs` (comment).
- `Directory.Packages.props`, `Omnichannel.Infrastructure.csproj` — `Polly.Core` 8.8.0.

Tests: `Domain/StockSyncDomainTests.cs`, `Validation/ChannelProductValidatorTests.cs`,
`Workers/ChannelStockClientTests.cs`, `Workers/StockSyncWorkerUnitTests.cs`, `Integration/DatabaseFailureLogTests.cs`,
`Integration/ChannelProductEndpointsTests.cs`, `Integration/Workers/StockSyncWorkerTests.cs`,
`Integration/Workers/RecordingChannelClient.cs` (new); existing files touched: see deviation 3.

Other: `frontend/src/features/inventory/components/StockImportForm.tsx`, `stockImport.test.tsx` (A2);
`scripts/smoke/task-014.ps1` (new), `scripts/smoke/_common.ps1`; `README.md` (section 7, mapping API, error codes).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend, final code, run again after the mutation runs) | **669 passed**, 0 failed (1 m 33 s) |
| `dotnet test --filter "Category!=Integration"` | 459 passed |
| new integration tests, three runs in a row (`ChannelProductEndpointsTests`, `StockSyncWorkerTests`) | 36 / 36 / 36 passed |
| `dotnet ef migrations add StockSync` | generated; contains only the columns, indexes, check and foreign keys listed above |
| `dotnet ef database update` | **throwaway database only** (all migrations incl. `StockSync` applied, `Done.`); not run on the development database |
| `npm run typecheck` / `npm run lint` | exit 0 / no findings, Prettier clean |
| `npm test` | 17 files, **168 passed** |
| `npm run build` | passed |
| `scripts/smoke/task-014.ps1` (throwaway environment) | **PASSED: all 38 checks** (three runs; the first found a bug of the script, see below) |

Frontend bundle (per review 013; measured from `dist`: entry chunk + everything it imports statically, file sizes, gzip
level 9): **first-load set 984.04 kB raw / 322.58 kB gzip** in 12 chunks; the same build of the 013 state (`b0ffb55`)
measured the same way: 984.04 kB / 322.59 kB — no growth. Main chunk 254.46 kB (gzip 81.98 kB as Vite prints it),
unchanged. (The 013 report gave 978.50 kB for the first-load set with a module-level method; the file-level number above
is 5.5 kB higher for the same code. The script used here is not committed.)

### Smoke script
Environment of the run: PostgreSQL 17 and RabbitMQ 4 containers started for it (random credentials, ports 55432 / 55672),
`dotnet ef database update` against that database, the API (`Development`, port 5081) and the Workers host
(`Development`) from the build output, configured through environment variables. Run 1: API and Workers started by hand —
37/38: step 28 failed because of the script (Windows PowerShell unrolled the one-element `attempts` array returned by an
`if`), fixed. Run 2: 38/38. Run 3 (after the logging changes; the script started and stopped the Workers host itself):
```
== Task 014 - channel product mapping and stock sync (simulated channels) ==
Using the API already running at http://localhost:5081 (restart it first if it runs an older build).
Workers host not running - building and starting it for this check...
Workers host started (log: C:\Users\ACER\AppData\Local\Temp\omnichannel-workers-smoke-20261002-130518.log).
Waiting for the Stock Sync Worker (up to 60 s)...
Waiting for the retry of the pos task (5 s delay tier)...
Waiting for the stock sync of the order (up to 60 s)...
Workers host stopped.
Step                                                                Expected                 Actual   OK
----                                                                --------                 ------   --
01 create smoke manager                                                  201                    201 True
02 create smoke staff                                                    201                    201 True
03 create smoke customer                                                 201                    201 True
04 create smoke product                                                  201                    201 True
05 import 5 units                                                        200                    200 True
06 the three seeded channels exist                                      True                   True True
07 map on website                                                        201                    201 True
08 ... with a sync task                                                 True                   True True
09 website sync task                                               Succeeded              Succeeded True
10 ... one attempt by the StockSyncWorker                  1 StockSyncWorker      1 StockSyncWorker True
11 ... task names channel and product                                   True                   True True
12 map on pos (SIM-TEMP-)                                                201                    201 True
13 pos sync task                                                   Succeeded              Succeeded True
14 ... after one failed attempt (SkuRules must be on)                      2                      2 True
15 ... which failed with                                 channel_unavailable    channel_unavailable True
16 staff cannot map                                                      403                    403 True
17 product mapped twice on website                                       409                    409 True
18 ... errorCode                                      channel_product_exists channel_product_exists True
19 staff lists the website mapping                                         1                      1 True
20 staff submits an order (website, 2 units)                             202                    202 True
21 order task                                                      Succeeded              Succeeded True
22 available stock after the order                                         3                      3 True
23 order: one sync task on website                               1 Succeeded            1 Succeeded True
24 order: one sync task on pos                                   1 Succeeded            1 Succeeded True
25 order: none on digital-platform (not mapped)                            0                      0 True
26 map on digital-platform (SIM-PERM-)                                   201                    201 True
27 digital-platform sync task (SkuRules must be on)                   Failed                 Failed True
28 ... one attempt, rejected by the channel               1 channel_rejected     1 channel_rejected True
29 correct the external SKU                                              200                    200 True
30 ... requests a new sync task                                         True                   True True
31 ... which succeeds                                              Succeeded              Succeeded True
32 staff cannot retry                                                    403                    403 True
33 manager retries the failed task                                       202                    202 True
34 retried task                                                    Succeeded              Succeeded True
35 ... with a second attempt                                               2                      2 True
36 ... sent as a directed message                       stock.sync.requested   stock.sync.requested True
37 lock the smoke manager                                                200                    200 True
38 lock the smoke staff                                                  200                    200 True
PASSED: all 38 checks.
```
If the Workers host does not run in Development, steps 14 and 27 fail and the script prints
"ChannelSimulation:SkuRules is OFF" (that branch was read, not executed).

### Worker log (same run, Development console template; `{...}` = scope properties not used in the message text)
Successful push:
```
[13:05:31 INF] Omnichannel.Infrastructure.ExternalChannels.SimulatedChannelStockClient: Simulated push to website: SKU SMOKE-014-68788 (external product null, external SKU WEB-SMOKE-68788) available stock 5; task 01a0fb37-c657-7b4f-8d44-d23efeb7c570, attempt 1, request 01a0fb37-c657-7b4f-8d44-d23efeb7c570:1:1: accepted {"MessageId": "01a0fb37-b96c-74d7-a629-2960033eea76", "CorrelationId": "56f9cb15ed4948ab98ef3637022ce452", "ProductId": "01a0fb37-b84e-7932-b2b1-e73fe7505899", "WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
[13:05:31 INF] Omnichannel.Workers.StockSyncWorker.StockSyncMessageHandler: Pushed available stock 5 of SMOKE-014-68788 to website (attempt 1, push 1) {"TaskId": "01a0fb37-c657-7b4f-8d44-d23efeb7c570", "MessageId": "01a0fb37-b96c-74d7-a629-2960033eea76", "CorrelationId": "56f9cb15ed4948ab98ef3637022ce452", "ProductId": "01a0fb37-b84e-7932-b2b1-e73fe7505899", "WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
```
Retry with its delay tier (stock change of the order, correlation id of the request; 13:05:40 → 13:05:45):
```
[13:05:40 INF] ...SimulatedChannelStockClient: Simulated push to pos: SKU SMOKE-014-68788 (external product null, external SKU SIM-TEMP-68788) available stock 3; task 01a0fb37-e9ca-7100-979a-781cbc614234, attempt 1, request 01a0fb37-e9ca-7100-979a-781cbc614234:1:1: channel_unavailable {"MessageId": "01a0fb37-e634-773f-b98d-85fd870b52d9", "CorrelationId": "smoke-014-68788", ...}
[13:05:40 WRN] Omnichannel.Workers.StockSyncWorker.StockSyncMessageHandler: Attempt 1 failed (channel_unavailable): Simulated channel 'pos' is unavailable for external SKU 'SIM-TEMP-68788' on the first attempt. {"TaskId": "01a0fb37-e9ca-7100-979a-781cbc614234", "MessageId": "01a0fb37-e634-773f-b98d-85fd870b52d9", "CorrelationId": "smoke-014-68788", "Channel": "pos", "ProductId": "01a0fb37-b84e-7932-b2b1-e73fe7505899", "WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
[13:05:40 INF] Omnichannel.Infrastructure.RabbitMQ.RabbitMqRetryPublisher: Retry 1 of message 01a0fb37-e634-773f-b98d-85fd870b52d9: waits 00:00:05 in omnichannel.delay.5s, then returns to stock.sync.pos {"WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
[13:05:45 INF] ...SimulatedChannelStockClient: Simulated push to pos: ... available stock 3; task 01a0fb37-e9ca-7100-979a-781cbc614234, attempt 2, request 01a0fb37-e9ca-7100-979a-781cbc614234:2:1: accepted {...}
[13:05:45 INF] Omnichannel.Workers.StockSyncWorker.StockSyncMessageHandler: Pushed available stock 3 of SMOKE-014-68788 to pos (attempt 2, push 1) {"TaskId": "01a0fb37-e9ca-7100-979a-781cbc614234", "MessageId": "01a0fb37-e634-773f-b98d-85fd870b52d9", "CorrelationId": "smoke-014-68788", ...}
```
Final failure:
```
[13:05:47 INF] ...SimulatedChannelStockClient: Simulated push to digital-platform: SKU SMOKE-014-68788 (external product null, external SKU SIM-PERM-68788) available stock 3; task 01a0fb38-018e-7068-a6cf-76f7d985bf95, attempt 1, request 01a0fb38-018e-7068-a6cf-76f7d985bf95:1:1: channel_rejected {...}
[13:05:47 ERR] Omnichannel.Workers.StockSyncWorker.StockSyncMessageHandler: Attempt 1 failed permanently (channel_rejected): Simulated channel 'digital-platform' does not know external SKU 'SIM-PERM-68788'. {"TaskId": "01a0fb38-018e-7068-a6cf-76f7d985bf95", "MessageId": "01a0fb38-018e-7c2b-ba6b-6f44320a2d81", "CorrelationId": "877f1a1fb56643238ab4a873b670cc15", "Channel": "digital-platform", "ProductId": "01a0fb37-b84e-7932-b2b1-e73fe7505899", "WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
[13:05:47 ERR] Omnichannel.Infrastructure.Persistence.Processing.ProcessingAttemptStore: Task 01a0fb38-018e-7068-a6cf-76f7d985bf95 failed permanently at attempt 1 (channel_rejected); message goes to the DLQ {"MessageId": "01a0fb38-018e-7c2b-ba6b-6f44320a2d81", "CorrelationId": "877f1a1fb56643238ab4a873b670cc15", "Channel": "digital-platform", "ProductId": "01a0fb37-b84e-7932-b2b1-e73fe7505899", "WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
[13:05:47 ERR] Omnichannel.Infrastructure.RabbitMQ.ResilientQueueConsumer: Message 01a0fb38-018e-7c2b-ba6b-6f44320a2d81 failed permanently (channel_rejected); moved to the DLQ {"WorkerName": "StockSyncWorker", "WorkerInstanceId": "smoke-workers-2", "Application": "Omnichannel.Workers"}
```
`Channel`, `TaskId` and `AttemptNo` are in the scope of every handler entry; the console template prints a property
either in the text or in the braces, not twice. Entries of the consumer and of the retry publisher are written after
the handler returned and carry only the worker scope (as for the Order Worker). The API log of the same run has no
Error entry for the 409 `channel_product_exists` (step 17): one Information `Request failed with 409`.

### Mutations (each: mutate, build, run the test named, restore)
Part B: 16 mutants, 16 caught. (A first run of all mutants against the whole new test set was stopped after two mutants:
every failing integration test waits for its 60 s timeout. The others were run against the test expected to catch them.)

| # | Mutation | Caught by |
| --- | --- | --- |
| M01 | no re-read after a push (stops after the first push) | "Change committed during a push ...", "Pushes within one attempt are bounded" |
| M02 | pushes per attempt not bounded (`MaxPushesPerAttempt + 5`) | "Pushes within one attempt are bounded" |
| M03 | host stopping → `Ack` instead of `Abandon` | "Host stopping during a push cancels it and the message is redelivered" |
| M04 | publisher ignores `OutboxEvent.RoutingKey` | "Stock change is pushed to every channel that sells the product ..." (initial syncs never arrive) |
| M05 | manual retry of a stock-sync task copies the source message | API: "Manual retry of a stock sync task writes a directed message ..." |
| M06 | directed binding declared on the wrong exchange | "Topology of tasks 005 and 007 upgrades to the directed bindings ..." |
| M07 | an inactive mapping is pushed | "Product without an active mapping creates no task ..." |
| M08 | task for a product the channel does not sell | "Product without an active mapping creates no task ..." |
| M09 | idempotency key differs per delivery | "Same inventory change delivered twice creates one task and one push" |
| M10 | task of another channel is accepted | "Poison messages go to the dlq of their queue ..." |
| M11 | a rejection by the channel is classified as temporary | "Rejection by one channel fails its task at once ..." |
| M12 | initial sync also for an inactive channel | API: "Inactive channel gets the mapping but no sync" |
| M13 | every update of an active mapping requests a sync | `ChannelProductTests` "Update asks for a sync only when ...", API: "Update requests a sync for changed ids ..." |
| M14 | permanent errors count toward the circuit breaker | "Permanent errors do not open the breaker" |
| M15 | a value other than the current available stock is pushed | "Stock change is pushed to every channel that sells the product ..." |
| M16 | task and message saved before, not with, the mapping | API: "Product already mapped on the channel is a conflict and leaves no task behind" |
| A1 | the interceptor never writes the Error entries | 3 tests of `DatabaseFailureLogTests` |
| A2 | list error not cleared on submit | `stockImport.test.tsx` › "removes the list error of the previous attempt on the next submit" |

A first attempt at the A1 mutation did not compile and the tests ran on the old binaries (6/6 passed); it was repeated
with a mutant that compiles.

### Required tests → where
| # | Requirement | Tests |
| --- | --- | --- |
| 1 | `ChannelProduct` rules; factory requires channel and product | `StockSyncDomainTests.cs` (`ChannelProductTests`, `StockSyncTaskTests`, `DirectedOutboxEventTests`) |
| 2 | Mapping validators | `ChannelProductValidatorTests`; completeness: `FieldNamesTests`, `ValidationMessagesTests` pick the new validators and key up automatically |
| 3 | Simulated client | `SimulatedChannelStockClientTests` (modes, latency + cancellation, SKU rules on/off, settings file reload through the real registration) |
| 4 | Pipeline | `ResilientChannelStockClientTests` (timeout, breaker opens without calling, permanent errors do not open it, per channel, closes again, caller cancellation not counted, no retry) |
| 5 | Classifier | `ChannelErrorClassificationTests`; `ProcessingErrorClassifierTests` unchanged |
| 6 | Topology | `StockSyncTopologyTests` (literal arguments of every existing queue, map, bindings list); `StockSyncWorkerTests` › "Topology of tasks 005 and 007 upgrades ..." (literal 005/007 declaration, then the current one twice, real RabbitMQ, a backlog message kept, directed message in one queue only) |
| 7, 15 | Part A | `DatabaseFailureLogTests`, `stockImport.test.tsx` |
| 8 | Mapping API | `ChannelProductEndpointsTests` (17 tests) |
| 9 | End to end | "Stock change is pushed to every channel that sells the product and to no other" |
| 10 | Duplicate, stale snapshot, change during a push | "Same inventory change delivered twice ...", "Concurrent deliveries of one change resolve to one task", "Current stock is pushed not the snapshot ...", "Change committed during a push ...", "Pushes within one attempt are bounded" |
| 11 | Transient, permanent + independence, max attempts | "Transient channel failure is retried with delay ...", "Rejection by one channel fails its task at once and leaves the other channel alone", "Channel that stays unavailable fails the task after max attempts ...", "Channel that does not answer times out and is retried" |
| 12 | Manual retry | "Manual retry reaches only the queue of the task channel ..." (queue counts 1 / 0 / 0 with no consumer running), "Manual retry gives max attempts new attempts", API: "Manual retry of a stock sync task writes a directed message ..." |
| 13 | Poison | "Poison messages go to the dlq of their queue and nothing is pushed" (invalid JSON, wrong event type, unknown task, task of another type, task of another channel) |
| 14 | Orders unchanged | all existing order tests green; changes to existing test files: deviation 3 |
| — | Shutdown | "Host stopping during a push cancels it and the message is redelivered" |

## Deviations from the task
1. **A1 is an EF Core-level hook, not a Serilog filter.** Reason: the observation above — the `CommandError` entry has no
   exception, so no filter can tell an expected conflict from a real failure for that entry, and an EF Core event has
   one fixed level. Consequences to know:
   - EF Core's own entries for the two events are now always at Debug; the Error entries for every other failure are
     written by the interceptor, with EF Core's category, event id and text (asserted by the tests). With Debug enabled
     for these categories an unexpected failure appears twice (Debug by EF Core, Error by the interceptor).
   - It needs no Serilog reference in Infrastructure and no code in `Program.cs`; it also works in hosts without Serilog
     (the test Workers hosts).
   - The registry lives in `Persistence` (next to `IsUniqueViolation`), the interceptor in `Infrastructure/Logging`.
2. **`IsUniqueViolation` returns false for an unregistered name** (new behaviour, tested): the list cannot drift
   silently — an unregistered conflict surfaces as a 500 in that feature's own conflict test.
3. **Existing test files changed** (no assertion about orders changed):
   - `ProcessingErrorClassifierTests`, `OrderWorkerTests`, `OrderWorkerRetryTests`, `ManualRetryEndToEndTests`: one
     `using Omnichannel.Workers.Processing;` each (the classifier moved, as the task asks).
   - `OutboxPublisherTests.CreatePublisherHost` also removes `StockSyncWorkerService`: the test needs the messages to
     stay in the stock-sync queues, and they now have a consumer.
   - `QueryShapeTests`, `ProcessingTaskEndpointsTests`: a seeded task of type `StockSync` without channel and product is
     now rejected by the CHECK constraint; both seed an `Invoice` task instead (they only needed "another task type").
   - `ApiFactory`: `CapturingLogSink.EventsSince` (helper for the A1 tests).
4. **`OrderReservationService` delegates the attempt bookkeeping to a shared `ProcessingAttemptStore`** (start attempt,
   transient failure, final failure, task row lock; bodies moved unchanged). Reason: "do not build a second mechanism" —
   the stock-sync service uses the same store instead of a copy. The Order Worker's interface, handler and tests are
   unchanged. Visible effect: the log category of "Task ... failed permanently ..." is now `ProcessingAttemptStore`.
5. **`HandlerResult.Abandon`** (new result in the shared consumer): the delivery is left unsettled. Needed for "an
   in-flight push is cancelled through the token ... the message is not ACKed" — the consumer passes no cancellation
   token to handlers (decision of 006/007), and every existing result settles the message. The stock-sync handler links
   only the external call to `IHostApplicationLifetime.ApplicationStopping`; the broker requeues the message when the
   consumer's channel closes (counted by `x-delivery-count`, like a crash). The Order Worker does not use it.
6. **`RabbitMqRetryPublisher` logs the delay tier** (`Retry 1 of message ...: waits 00:00:05 in omnichannel.delay.5s, then
   returns to stock.sync.pos`, Information; one more constructor parameter). The acceptance criterion asks for a log
   excerpt "with its delay tier" and nothing logged the tier. Applies to the Order Worker's retries as well.
7. **Channel errors are logged without a stack trace** by the stock-sync handler (code + message only; unexpected
   exceptions keep the stack). The first smoke run showed ten stack lines per simulated failure.
8. **`ChannelSimulation:Channels:<slug>:Mode` is bound as text** and parsed on each push. An unknown value fails the push
   with a message that names the setting (classified `unexpected`, retried, visible in the attempt) instead of breaking
   the configuration reload of a running host.
9. **The adapter is a keyed service** (`ResilientChannelStockClient.AdapterKey`): `IChannelStockClient` resolves to the
   resilient client, which wraps the keyed adapter (the simulator). Tests replace the adapter only, so the Polly pipeline
   stays in the integration tests.
10. **"At least one external id" is reported on `externalProductId`** (message names both fields:
    "Enter 'External product ID' or 'External SKU'." / "Hãy nhập 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh'."). The
    task did not say which key.
11. **`Location` of the 201** is `/api/channels/{channelId}/products/{channelProductId}` (the address of the PUT); there
    is no GET for a single mapping in the task.
12. **Poison messages write `stock_sync.message_rejected`** to `SystemEventLogs` (payload `{messageId, reason, queue}`),
    like `order.message_rejected`. An unparseable `inventory.changed` produces one row per channel queue.
13. **Manual retry of other task types** now copies the source row's `RoutingKey` too (null for every existing row: no
    change in behaviour).
14. **`scripts/smoke/_common.ps1`:** `Start-SmokeWorkers` / `Test-WorkersRunning` (start, log file, stop in
    `Stop-Smoke`), and `SMOKE_ADMIN_EMAIL` / `SMOKE_ADMIN_PASSWORD` as an alternative to the `.env` file (used for the
    throwaway environment; the `.env` file was not read).
15. **Smoke run on a throwaway environment**, see above. Reason: the development database needs the migration, and the
    migrator connection is deliberately not available to Claude Code.
16. **Only `Polly.Core`** was added (no `Polly.Extensions` / `Microsoft.Extensions.Resilience`): the pipeline is built
    directly with `ResiliencePipelineBuilder`.

## Blocked / questions for Cowork
None blocking. For the review:
- **A1 mechanism** (deviation 1): accept the EF Core-level hook, or keep EF Core's own `SaveChangesFailed` entry and
  filter only that one in Serilog (two mechanisms, and Serilog in Infrastructure)? The `CommandError` entry needs the
  hook either way.
- **Open circuit and the attempt budget:** while a channel's breaker is open, attempts fail at once with
  `channel_circuit_open` and still count toward `MaxAttempts` (as the task's error classes say). With the default tiers
  five attempts span about 12.5 minutes; a longer outage ends in `Failed` + DLQ for every task of that channel, and
  each one needs a manual retry. Options: accept until reconciliation (022) exists; or do not count
  `channel_circuit_open` toward the budget; or a bulk retry in 016.
- **`HandlerResult.Abandon` for the Order Worker?** Its handler ignores shutdown on purpose (the reservation finishes).
  Left as it is.

## Out of scope / follow-ups
- README section 6 still says the `order.processing` queue has "no consumer yet" (outdated since 006); not touched.
- `OrderMessageHandler` still logs transient failures with the full stack (unchanged); the tier log line (deviation 6)
  now also appears for its retries.
- A stock-sync task created from `inventory.changed` shows that shared event as its `message` in the task detail (three
  tasks, one outbox row). After a manual retry it shows its own `stock.sync.requested`.
- A mapping update is last-write-wins (no concurrency token on `ChannelProducts`); two concurrent updates can both
  request a sync, which is harmless.
- Several instances, or `StockSyncWorker:Concurrency` > 1: pushes of one product can run in parallel; the re-read after
  each push makes the channel converge, at the cost of repeated pushes. Covered by reasoning and by the
  "change during a push" test, not by a multi-instance test.
- Channels created through the API are never synced (no queue); the frontend (016) should show that.
- The simulator keeps no channel-side stock; 022 needs readable state.
- `Omnichannel.Api.http` has no sample requests for the new endpoints.
- Still open from earlier reports: unsaved-changes guard on the forms, `OrderEndToEndTests` reusing
  `RabbitMqBrokerFixture`.

## Manual steps for the user
1. **Apply the migration** (migrator role, from `backend/`):
   ```powershell
   $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
   dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
   ```
   It adds two nullable columns to `ProcessingTasks`, one to `OutboxEvents`, two indexes, two foreign keys and a check.
2. **Restart the API and the Workers host** (new build). At its first start the Workers host adds the three bindings and
   works through the backlog in `stock.sync.*` (acknowledged without tasks: no mapping existed).
3. **Run the smoke script on the development environment** and paste the table:
   `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-014.ps1`. It uses a running API / Workers host or starts
   them; the Workers host must run in Development (`dotnet run` does), otherwise steps 14 and 27 fail with the note about
   `SkuRules`.
4. **Manual check** (about 3 minutes), API and Workers running:
   1. Map a product on `website` (`POST /api/channels/{websiteId}/products` with an `externalSku`), wait for its
      `syncTaskId` to be `Succeeded` (`GET /api/processing/tasks/{id}`).
   2. In `backend/src/Omnichannel.Workers/appsettings.Development.json` set `ChannelSimulation:Channels:website:Mode` to
      `TransientError` and save — do not restart the Workers host.
   3. Import stock for that product (`POST /api/inventory/import`). In the worker log: `Attempt 1 failed
      (channel_unavailable)`, `Retry 1 ... waits 00:00:05 in omnichannel.delay.5s`, then attempt 2 and
      `waits 00:00:30 in omnichannel.delay.30s`. `GET /api/processing/tasks?taskType=StockSync&channelId={websiteId}`
      shows the task `RetryScheduled` with its failed attempts.
   4. Set the mode back to `Succeed` and save: the next attempt (after the 30 s or 2 min tier) succeeds and the task is
      `Succeeded`. The `pos` and `digital-platform` tasks of the same import were never affected.

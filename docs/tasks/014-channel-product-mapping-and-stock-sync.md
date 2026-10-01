# Task 014 — ChannelProduct mapping + Stock Sync Worker (simulated channels)

Status: Open
Written by: Cowork, 2026-10-02
Depends on: 013 (accepted; user's manual check pending)

## Context
Doc §8.3 and §16 "Stock sync có retry và DLQ": after a stock change is committed, every channel that sells the product
gets the new available stock, independently per channel, with retry/backoff and DLQ. Since task 005 every
`inventory.changed` message is routed to `stock.sync.website`, `stock.sync.pos` and `stock.sync.marketplace`, but nothing
consumes them yet (the queues hold a backlog of old events — harmless, see "Backlog").

**Decision (user, 2026-10-02): channel APIs are simulated.** Read the implementation log, "Task 014 plan" — every design
decision of this task is recorded there (task granularity, pushed value, directed messages, manual retry, initial sync,
Polly, error classes). If the implementation needs a decision that is not in this file or in the log (e.g. a change to
the order or import transactions, a change of queue arguments), stop and report (`Blocked`).

Reuse what exists, do not build a second mechanism: `ResilientQueueConsumer` / `QueueConsumerSettings` / `RetryPolicy`
(task 007), `IQueueMessageHandler` (`HandleAsync`, `RecordFinalFailureAsync`), the attempt flow of `ProcessingTask`
(`StartAttempt`, `Succeed`, `ScheduleRetry`, `Fail`), `processing.failed`, the Outbox Publisher, `OrderWorkerService` /
`OrderMessageHandler` as the pattern, `WorkersFixture` and `RabbitMqBrokerFixture` for tests.

Read first: implementation log "Task 005 plan" … "Task 008 plan" and "Task 014 plan"; `RabbitMqTopology.cs`,
`OrderMessageHandler.cs`, `ProcessingTaskService.RetryAsync`, `MessageEnvelope.FromOutboxEvent`,
`DbUpdateExceptionExtensions.cs`, review 012 (P3 on EF Core logging).

## Part A — Follow-ups (separate commits)
1. **Expected unique violations are not logged as errors by EF Core** (review 012). Today a mapped 23505 (e.g. a
   re-submitted stock import → 409 `stock_import_already_applied`) is logged by EF Core at Error with the
   `DbUpdateException` stack, before the service maps it.
   - First **observe** the real log output of one such conflict (category / `SourceContext`, event id, level, how the
     exception is attached; EF Core may log more than one entry) and put the excerpt in the report. Do not guess names.
   - One registry of the constraint names whose 23505 is expected (every name used with `IsUniqueViolation` today:
     channel slug, product SKU, user name, movement reference, processing-task idempotency key, order idempotency; plus
     the new mapping indexes of Part B). The catch clauses use the same constants, so the list cannot drift.
   - EF Core's entries for a 23505 on a registered constraint are not written at Warning or above (dropping them or
     writing them at Debug is fine). Every other database failure — 23505 on an unregistered constraint, other SQL states
     — is still logged by EF Core at Error. The mapped outcome stays visible once at Information through the existing
     path (API: `GlobalExceptionHandler` "Request failed with 409…"; Workers: the handler's own log).
   - Applies to the API and the Workers host (shared code in `Omnichannel.Infrastructure/Logging`, doc §7). Preferred
     mechanism: a Serilog filter keyed on EF Core's event ids and the exception chain (`PostgresException`, SQL state,
     constraint name). If a cleaner EF Core-level hook exists, use it and say why. No new package.
   - Tests: a registered conflict produces no EF Core entry at Warning+; an unregistered one (e.g. a unique index created
     only in the test database, or a constraint name not in the registry) still produces the Error entry; a
     non-23505 failure still produces it. Use real exceptions from the test database, not hand-built ones.
2. **Stock-import form: stale list-level error.** `StockImportForm` keeps an API error of the `items` list after the next
   submit (the order form clears it since 013). Clear it on submit the same way; a test like the order-entry one ("old
   list error stays" mutation must fail it).

## Part B — Mapping and stock sync

### Data model (one migration, e.g. `StockSync`)
- `ProcessingTasks`: `ChannelId` (uuid, null, FK → `Channels`, restrict), `ProductId` (uuid, null, FK → `Products`,
  restrict), indexes on both; CHECK: `TaskType <> 'StockSync'` or both not null. Domain: a factory/constructor for stock-sync
  tasks that requires both; existing constructors unchanged for other task types.
- `OutboxEvents`: `RoutingKey` (varchar(255), null; null = `EventType`). The publisher uses `RoutingKey ?? EventType`;
  existing rows and all current producers are unchanged.
- `ChannelProducts` (exists since 001): add a domain method to update external ids and status. Rule: at least one of
  `ExternalProductId` / `ExternalSku` (trimmed, ≤ 128 each); the existing unique indexes stay as they
  are — reference their current database names through constants (409 mapping, Part A1 registry); no rename.
- Migration generated by Claude Code, applied with the migrator role (`OMNICHANNEL_MIGRATOR_CONNECTION`, README). If that
  variable is not available to Claude Code, applying it is a manual step for the user (report it); the app role never
  runs DDL.

### Messaging
- `EventTypes.StockSyncRequested = "stock.sync.requested"`, payload `StockSyncRequestedPayload(TaskId, ChannelId,
  ProductId)`. The envelope's `taskId` = the outbox row's aggregate id (as for `order.created`; extend
  `MessageEnvelope.TaskIdOf`).
- Topology: each stock-sync queue gets one more binding on `omnichannel.topic` with routing key = its own queue name.
  **No argument of any existing queue changes** — the Workers host must start against the current development broker
  without a topology mismatch (test: declare the 005/007 topology, then the new one, on the same broker).
- Channel ↔ queue: one map by slug in `RabbitMqTopology` (`website` → `stock.sync.website`, `pos` →
  `stock.sync.pos`, `digital-platform` → `stock.sync.marketplace`); a channel without an entry is never synced.
- A directed message = `OutboxEvent(stock.sync.requested, aggregateId: taskId, payload, correlationId, movement: null)`
  with `RoutingKey` = the channel's queue, written in the same transaction as its task.

### Mapping API (ChannelService, doc §7.1)
- `GET /api/channels/{channelId}/products` (AnyStaff): paging contract of the project (`page`, `pageSize`, `search` over
  SKU, product name, external product id, external SKU; `sortBy` `sku` | `createdAt`, default `sku`). Item:
  `channelProductId`, `channelId`, `productId`, `sku`, `productName`, `externalProductId`, `externalSku`, `status`,
  `createdAt`, `updatedAt`. Unknown channel → 404 (same as `GET /api/channels/{id}`).
- `POST /api/channels/{channelId}/products` (ManagerOrAdmin): `{ productId, externalProductId?, externalSku? }` → 201,
  `Location`, body = the item + `syncTaskId` (null when no sync was requested). Unknown product → 400 on `productId`
  (existing `ProductNotFound` message, as in import/intake); product already mapped on this channel → 409
  `channel_product_exists`; external product id used by another product on this channel → 409
  `external_product_id_taken`. Both via the unique indexes (no check-then-insert).
- `PUT /api/channels/{channelId}/products/{channelProductId}` (ManagerOrAdmin): `{ externalProductId?, externalSku?,
  status }` → 200, same body as POST. Unknown or other channel's mapping → 404 `channel_product_not_found`. Product and
  channel of a mapping cannot change. No DELETE (deactivate instead).
- **Initial sync:** create, re-activation, or a change of the external ids while Active → in the same transaction a
  `StockSync` task (`Pending`, `ChannelId`, `ProductId`, the request's correlation id, `MessageId` = the outbox id) and
  its directed message — only when the channel is Active and has a sync queue. Deactivating does nothing on the channel
  (no zero push; out of scope).
- Validation: FluentValidation, camelCase keys, localised messages and `FieldNames` (vi/en) for the new fields; the
  completeness tests must cover them. New error codes documented in the README error list if there is one.

### Processing API
- `ProcessingTaskSummaryResponse` / `ProcessingTaskDetailResponse` (and the order detail's task item, if it uses its own
  record) gain `channelId`, `productId` (nullable); `GET /api/processing/tasks` gains a `channelId` filter. Additive only.
- **Manual retry of a `StockSync` task** (`POST /api/processing/tasks/{id}/retry`): instead of copying the source message
  (which is a fanned-out `inventory.changed`), write a new directed `stock.sync.requested` for the task's channel; task
  `Pending`, `MessageId` = new id, fresh attempt budget, audit event — as for other tasks. Other task types unchanged.

### Channel adapter (Infrastructure/ExternalChannels)
- `IChannelStockClient` (Application): `PushAvailableStockAsync(ChannelStockUpdate, CancellationToken)`;
  `ChannelStockUpdate` carries channel slug, product id, SKU, external product id, external SKU, available stock, task id,
  attempt no and a request id (`{taskId}:{attemptNo}:{n}`, for channel-side deduplication by real adapters). Failures are
  a `ChannelApiException` with `ErrorCode` (`channel_timeout`, `channel_unavailable`, `channel_rate_limited`,
  `channel_rejected`, `channel_circuit_open`), a sanitised message and `IsTransient`.
- `SimulatedChannelStockClient`, options section `ChannelSimulation` read through `IOptionsMonitor` (a change of
  `appsettings.Development.json` applies without a restart):
  - per channel slug: `Mode` = `Succeed` (default) | `TransientError` | `PermanentError` | `Timeout` (waits until
    cancelled), `LatencyMs` (default 0);
  - `SkuRules` (enabled in `appsettings.Development.json` only, off by default): external SKU starting with `SIM-PERM-` →
    permanent `channel_rejected`; `SIM-TEMP-` → transient `channel_unavailable` on attempt 1 only, success afterwards.
    These prefixes exist only in the simulator and are documented in the README.
  - logs each push at Information (channel, SKU / external ids, value, task, attempt); never anything secret.
- **Polly** (doc stack): one `ResiliencePipeline` per channel slug — attempt timeout (`StockSyncWorker:ChannelCallTimeout`,
  default 10 s) and a circuit breaker that counts only transient failures and timeouts (defaults in options; break
  duration 30 s). **No Polly retry** (implementation log). Use the current stable Polly v8 package(s) through Central
  Package Management; state the version in the report. Timeout → `channel_timeout`, open circuit →
  `channel_circuit_open`, both transient.
- Registered in the Workers host only; the API does not call channels.

### Stock Sync Worker (Workers/StockSyncWorker)
- `StockSyncWorkerService` runs one consumer per mapped queue (three) through `ResilientQueueConsumer`, each bound to its
  channel slug. Options `StockSyncWorker`: `Concurrency` (default **1**; validated 1–16), `MaxAttempts` (5),
  `MaxPushesPerAttempt` (3), `ChannelCallTimeout` (10 s), circuit-breaker settings, consumer backoff. Topology mismatch →
  stop the host as the Order Worker does. `WorkerName = "StockSyncWorker"`, actor `system:stock-sync-worker`.
- Handler, per delivery:
  1. Unparseable / unknown event type → `Reject` (poison, logged with message id).
  2. `inventory.changed`: resolve the channel by slug (missing → ACK + warning, nothing created); no Active mapping for
     the product, or channel Inactive → **ACK without a task** (Debug log). Otherwise find or create the task by
     `IdempotencyKey = stocksync:{channelId:N}:{messageId}` (insert; a 23505 on that key → load the existing task —
     expected, Part A1).
  3. `stock.sync.requested`: the task from the envelope; missing, not `StockSync`, or another channel → `Reject`.
  4. Terminal task → ACK (duplicate). Otherwise `StartAttempt(WorkerName, InstanceId)` committed before the push.
  5. Read mapping, SKU and the **current** `AvailableStock` (one query, no lock); mapping or channel no longer Active →
     task `Succeeded` without a push (log why). Push through the pipeline; then read `AvailableStock` again and push again
     while it differs, at most `MaxPushesPerAttempt` pushes (if it still differs, succeed and log — the next change has
     its own event).
  6. Success → task `Succeeded`, ACK. Transient (`ChannelApiException.IsTransient`, transient DB errors per the shared
     classifier) → `ScheduleRetry` + `Retry`. Permanent → `Fail`. Final failure → `RecordFinalFailureAsync` as in the
     Order Worker (task `Failed`, `processing.failed`, DLQ).
- Move `ProcessingErrorClassifier` out of `OrderWorker/` to a shared place in the Workers project and extend it with
  `ChannelApiException`; no behaviour change for orders (existing tests stay green unchanged).
- Log scope: `TaskId`, `CorrelationId`, `MessageId`, `Channel`, `ProductId`, `AttemptNo`, `WorkerName`,
  `WorkerInstanceId`. Graceful shutdown: an in-flight push is cancelled through the token, the attempt is not left
  `Running` forever (redelivery abandons it, as today), the message is not ACKed.
- **Backlog:** the three queues hold `inventory.changed` events since task 005. They are processed like any other (no
  mapping existed → ACK, no task). Do not purge or delete queues.

## Tests (required)
Unit:
1. `ChannelProduct` update rules; stock-sync task factory requires channel and product.
2. Mapping request validators (keys, messages, at least one external id, lengths).
3. Simulated client: each mode, latency honours cancellation, SKU rules on/off.
4. Pipeline: timeout → `channel_timeout`; breaker opens after the configured transient failures → `channel_circuit_open`
   without calling the client; permanent errors do not open it.
5. Classifier: `ChannelApiException` transient/permanent; existing order cases unchanged.
6. Topology: the new bindings exist; the arguments of every existing queue equal literal expected values (the 005/007
   definitions), so an accidental change fails a test; declaring twice on one broker succeeds (real RabbitMQ).
7. Part A1 filter (see Part A).

Integration (Testcontainers; a recording `IChannelStockClient` double unless the simulator is under test):
8. Mapping API: roles (Staff reads, cannot write → 403), 404s, 400 keys, both 409s, list paging/search/sort; create →
   task + directed outbox row (routing key = queue) in the same transaction; inactive channel → no task; a channel created
   through the API (no sync queue) → mapping saved, no task; update rules for re-activation / external id change /
   deactivation.
9. End to end with Workers: import stock for a product mapped on `website` and `pos` → one task per mapped channel,
   `Succeeded`, attempt with worker name and instance, pushed value = current available stock; `stock.sync.marketplace`
   creates nothing for it.
10. Same `inventory.changed` delivered twice → one task, one push. A message whose snapshot is stale → the current value
    is pushed. A change committed during a push → second push with the new value.
11. Transient failure once → `RetryScheduled`, attempt `Failed` with the code, delayed retry → `Succeeded` (short delay
    tiers as in the task 007 tests). Permanent → `Failed`, `processing.failed`, copy in `stock.sync.<x>.dlq`; the other
    channel's task for the same change `Succeeded` (independence). Max attempts → `Failed` + DLQ.
12. Manual retry of a failed stock-sync task → exactly one directed message, consumed only by that channel's queue (the
    other two queues receive nothing) → `Succeeded`; fresh attempt budget; audit event.
13. Poison `stock.sync.requested` (unknown task, other channel) → DLQ, nothing pushed.
14. Order intake and the Order Worker unchanged: existing tests green without modification.
15. Part A1 and A2 as listed there.

## Acceptance criteria
- [ ] Backend build 0 warnings, all tests pass (unit + integration); frontend typecheck, lint, test, build pass (Part A2;
      report the first-load set, raw and gzip, per review 013).
- [ ] Migration generated; applied (by Claude Code with the migrator role, or listed as a manual step).
- [ ] `scripts/smoke/task-014.ps1` (API **and** Workers; ASCII, `_common.ps1`): smoke Manager + Staff; product with
      stock 5; mapping on `website` (`externalSku` normal) → 201 with `syncTaskId` → task `Succeeded`; mapping on `pos`
      with `SIM-TEMP-…` → task `Succeeded` after one failed attempt (wait for the 5 s tier); Staff POST mapping → 403;
      duplicate mapping → 409 `channel_product_exists`; order by Staff on `website` qty 2 → `StockSync` tasks for the
      product on `website` and `pos` (filter `channelId`), `Succeeded`, none on `digital-platform`; mapping on
      `digital-platform` with `SIM-PERM-…` → task `Failed` with `channel_rejected`; PUT that mapping with a normal
      external SKU → new task `Succeeded`; manual retry of the failed task → `Succeeded`; lock both smoke users. If
      `SkuRules` are off the script says so and fails. Run it and paste the table.
- [ ] Worker log excerpt in the report: one successful push, one retry with its delay tier, one final failure — showing
      the scope properties.
- [ ] Part A1 log excerpts before/after for one registered conflict and one unregistered violation.
- [ ] README: stock sync (what triggers it, the simulator, `ChannelSimulation` and `SkuRules`, how to make a channel fail
      for a demo), new endpoints, smoke script.
- [ ] Manual check for the user documented (short): switch `ChannelSimulation:Channels:website:Mode` to
      `TransientError` while the Workers run, import stock, watch attempts and the 5 s / 30 s tiers, switch back.

## Out of scope
Frontend for mappings and stock-sync tasks (016), SignalR (015), reconciliation and reading channel-side stock (022),
real channel adapters, webhooks (023), coalescing several changes into one push, zero push on deactivation, sync of
channels without a doc queue, bulk mapping import.

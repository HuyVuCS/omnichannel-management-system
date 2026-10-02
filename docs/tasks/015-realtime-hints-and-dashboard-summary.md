# Task 015 — Realtime hints (PostgreSQL NOTIFY → SignalR) + dashboard summary

Status: Open
Written by: Cowork, 2026-10-02
Depends on: 014 (accepted)

## Context
Doc §12: "Worker/API → PostgreSQL commit → application notification → SignalR hub → React → RTK Query
invalidate/refetch"; doc §16 "Dashboard nhận cập nhật gần real-time"; doc §11 `GET /api/dashboard/summary`.
The frontend (016) will connect to the hub and refetch on hints; this task delivers the backend half and the summary.

**Decision (user, 2026-10-02): PostgreSQL `LISTEN/NOTIFY`.** Every design decision of this task is in the implementation
log, "Task 015 plan" — read it first. SignalR stays a hint channel: no business data in a hint, the database stays the
source of truth, a client that missed hints refetches. If the implementation needs a decision that is not in this file or
in the log (another transport, a backplane, data in hints, a change of an existing transaction), stop and report
(`Blocked`).

Read first: implementation log "Task 014 result" and "Task 015 plan", review 014, `AuditingInterceptor.cs` and
`DatabaseFailureLogInterceptor.cs` (interceptor pattern, registration in `AddInfrastructure`), `TokenRevocation.cs` and
`AuthServiceCollectionExtensions.cs` (JWT events), `Program.cs` of the API (middleware order, CORS, request logging),
`InventoryPredicates.cs` (shared low-stock predicate).

## Part A — Follow-ups from review 014 (separate commits)
1. **(P2) Stale stock-sync task after deactivation.** In `StockSyncService.ResolveInventoryChangeAsync` look up the task
   by its idempotency key **before** the channel/mapping check; the check only decides whether a *new* task is created.
   The handler then reaches `PushCurrentStockAsync`, which already completes an inactive target as `Succeeded` without a
   push. The final-failure path (`RecordFinalFailureAsync` → `ResolveAsync`) gets the same behaviour. Integration tests:
   transient failure → deactivate the mapping → the delayed copy completes the task `Succeeded`, nothing pushed; the
   same with the channel deactivated; delivery-limit guard on such a message fails the existing task instead of ACKing
   it. Mutation: the old order of the checks must fail them.
2. **`GET /api/channels/{channelId}/products/{channelProductId}`** (AnyStaff): one mapping, same item shape as the list;
   404 `channel_product_not_found` also for a mapping of another channel. The 201 `Location` of 014 now resolves (test).
3. **README section 6**: remove "`order.processing` has no consumer yet"; describe the consumers that exist (Order Worker,
   Stock Sync Worker, Outbox Publisher). Text only.
4. **`Omnichannel.Api.http`**: sample requests for the mapping endpoints and the two endpoints of this task (no secrets;
   variables for the token).

## Part B — Realtime hints

### Producing hints (Infrastructure, API and Workers)
- One `SaveChanges` interceptor (e.g. `Infrastructure/Realtime/RealtimeHintInterceptor`), registered in
  `AddInfrastructure` next to the other two, so the API and the Workers host both produce hints without host code:
  - **capture** in `SavingChanges` from the change tracker (before EF Core resets entity states):
    added `Order` → `order.created` (`orderId`, `channelId`, `status`); added `InventoryMovement` →
    `inventory.changed` (`productIds`, distinct — every stock change writes a movement, the raw-SQL updates included);
    added `ProcessingTask`, or a `ProcessingTask` whose `Status` changed → `processing.task.changed` (`taskId`,
    `taskType`, `status`, `orderId`, `channelId`, `productId`);
  - **send** in `SavedChanges` with `SELECT pg_notify('omnichannel_ui', @payload)` on the context's connection: inside the
    caller's explicit transaction it is delivered at its commit and discarded by a rollback; without an explicit
    transaction `SavedChanges` runs after EF Core's own commit. **Verify both statements against the running database in
    a test** (do not rely on reading only);
  - **clear** the captured hints in `SaveChangesFailed` (a failed save sends nothing);
  - one `NOTIFY` per successful `SaveChanges` with all its hints; the payload must stay below 8000 bytes — cap id lists
    (e.g. 100 product ids; more → `"all": true` for that event) and assert the cap in a test with a 200-line import;
  - a failing `pg_notify` must never turn a committed business change into an error for the caller **when it runs after
    the commit** (log Warning and continue); inside an explicit transaction it fails with the transaction (acceptable:
    only an oversized payload can cause it, which the cap prevents). Say in the report which case applies where.
- Channel name and payload contract (`RealtimeHint` records, event-type constants) in Application, shared by producer and
  listener. No hint for catalog CRUD, users, outbox status or attempts in this task.
- No change to the existing transactions, services or their order of statements is needed; if one seems needed, stop.

### Delivering hints (API)
- `PostgresHintListener` (hosted service in the API): its own `NpgsqlConnection` from the configured connection string
  (app role — check that `LISTEN` needs no extra grant and say so), `LISTEN omnichannel_ui`, `WaitAsync` loop. On
  connection loss: Warning, reconnect with exponential backoff (cap 30 s); **after every successful reconnect** (not the
  first connect) broadcast a `resync` hint — hints sent while it was disconnected are lost. Graceful stop with the host.
- Batching: hints received within a short window (option `Realtime:BatchWindow`, default 250 ms) go to clients as **one**
  hub message `hints` with `{ events: [...], resync: bool }`. Ordering inside a batch = arrival order.
- Unparseable payload → Warning with the payload length (not the payload), skipped.
- The listener does not touch the `DbContext` and holds no pooled connection.

### Hub
- `Omnichannel.Api/Hubs/NotificationsHub` at **`/api/hubs/notifications`** (under `/api`, so the dev proxy and a later
  reverse proxy need one prefix), `[Authorize(Policy = AnyStaff)]`, no client-callable methods; clients receive `hints`.
- JWT for this path from the `access_token` query parameter (`JwtBearerEvents.OnMessageReceived`, path check), all other
  paths unchanged (header only). Existing `OnTokenValidated` (security stamp) applies at connect.
  `CloseOnAuthenticationExpiration = true`.
- **The token must never be logged**: check every log source that sees the request URL (request logging, hosting,
  SignalR/HTTP connection logs at the configured levels and at Debug for `Microsoft.AspNetCore.SignalR` /
  `Microsoft.AspNetCore.Http.Connections`) and test it: connect with a token, assert that no captured log event contains
  it.
- CORS: the hub uses the existing frontend policy; no credentials mode needed (token in the query). Rate limiting: none
  added for the hub (connections are authenticated); say if the existing global limiter applies to `negotiate`.

## Part C — `GET /api/dashboard/summary` (AnyStaff)
- Query: `from`, `to` (ISO 8601 with offset, both optional; default `to` = now, `from` = `to` − 24 h; `from` < `to`;
  range ≤ 92 days; camelCase keys, localised messages). Range applies to orders and to finished tasks; "current" numbers
  ignore it.
- Response (`generatedAt`, `from`, `to`, then):
  - `orders`: `total`; `byStatus` (every `OrderStatus`, zeros included); `byChannel` (every channel: `channelId`, `slug`,
    `name`, `total`, `reserved`, `insufficientStock`); `salesAmount` = sum of `TotalAmount` of orders `Reserved` or
    `Completed` created in the range;
  - `inventory` (current): `products`, `lowStock` (the shared predicate — same rows as the low-stock filter of
    `GET /api/inventory`), `outOfStock` (`AvailableStock = 0`);
  - `processing`: per task type (every `ProcessingTaskType`): `pending`, `inProgress`, `retryScheduled` (current),
    `failed`, `succeeded` (status reached in the range, by `UpdatedAt`);
  - `stockSync`: per channel with a sync queue: `retryScheduled` (current), `failed` (in range), `lastSucceededAt`;
  - `outbox`: `pending`, `failed` (current), `oldestPendingAt`.
- All numbers from SQL aggregates (a handful of queries, no N+1, no loading of rows into memory); check the plans of the
  order and task aggregates on a database with a few thousand rows and report them (indexes `Orders.CreatedAt`,
  `ProcessingTasks (TaskType, Status, CreatedAt)` exist; add an index only with a measured reason, in a migration).
- No caching in this task.

## Tests (required)
Unit: hint capture from a change tracker (each entity case; unchanged task status → no hint; id cap; payload size);
summary validator (defaults, order, range limit, keys).
Integration (Testcontainers):
1. Commit → one notification on a separate `LISTEN` connection, payload as specified; explicit transaction rolled back →
   none; failed `SaveChanges` → none; implicit transaction → received after the data is visible to another connection.
2. Producers end to end, each asserting the hint types: stock import; order intake (task `Pending`); Order Worker
   reservation (order, inventory, task transitions); Stock Sync Worker success and final failure; mapping create (initial
   sync task); manual retry.
3. Hub with a real SignalR client (`Microsoft.AspNetCore.SignalR.Client`, test project only, Central Package Management):
   no token → 401 on negotiate; Staff token → receives a `hints` message after a stock import; two hints within the
   window → one message; a user whose security stamp changed cannot connect; the token appears in no log event.
4. Listener resilience: terminate its backend (`pg_terminate_backend` with the test container's superuser) → it
   reconnects, connected clients receive `resync: true`, later hints arrive again.
5. Summary: seeded orders/tasks/inventory in and out of the range give the expected numbers (zeros included); low-stock
   count equals the inventory endpoint's low-stock filter count; 400 cases; Staff allowed, anonymous 401.
6. Part A as listed there. Existing tests green without modification (if one must change, deviation with reason).

## Acceptance criteria
- [ ] Backend build 0 warnings, all tests pass; frontend untouched (state it).
- [ ] No migration expected; if one is added, the reason is measured (Part C) and applying it is listed for the user.
- [ ] `scripts/smoke/task-015.ps1` (API and Workers, ASCII, `_common.ps1`): smoke Staff; `negotiate` of the hub without
      token → 401, with token → 200; summary before → import 5 of a smoke product + one order on `website` → summary
      after: `orders.total` +1, `orders.byChannel[website].reserved` +1, `processing` succeeded counts grew, `outbox.failed`
      unchanged; `from` after `to` → 400; lock the smoke user. Run it and paste the table.
- [ ] Report: excerpt of the API log for one hint batch (debug level for the listener) showing no token; the two query
      plans (Part C); which `pg_notify` case applies to which producer (Part B).
- [ ] README: realtime section (event types, payload, `resync`, hub path, token in the query string for the hub only,
      `Realtime:BatchWindow`), summary endpoint.
- [ ] Manual check for the user (short): with API and Workers running (listener at Debug), submit an order from the
      order-entry page and find the hint batch in the API log; the visible realtime check follows in 016.

## Out of scope
Frontend (016: hub client, reconnect → refetch, event → tag map, dashboard page, order-entry outcome by push), alerts and
`low_stock.created` (017), `order.status.changed` (no state change exists yet), hints for catalog/users, a SignalR
backplane, caching of the summary, per-role filtering of hints.

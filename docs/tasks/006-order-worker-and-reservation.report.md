# Report 006 — Order Worker: consume `order.created`, atomic stock reservation, attempt tracking

Status: Done
Commit: 3870ae8 (Part A, review-005 follow-up), 7c453ee (Part B)

## Summary
The Order Worker consumes `order.processing`, records each attempt before doing business work, and reserves stock atomically
(conditional `UPDATE ... WHERE "AvailableStock" >= @stock` per item, items in `ProductId` order, all or nothing), storing the Order,
`Reserve` movements and `inventory.changed` outbox rows in the same transaction and ACKing only after commit. Insufficient stock stores
an `InsufficientStock` order (task `Succeeded`). Duplicates, crashes, poison messages and transient failures are handled and tested.
Part A: broker outages no longer count as outbox publish attempts. No migration. Build 0 warnings; 263/263 tests
(183 unit, 80 integration), 4 consecutive full runs green.

## Files changed
Part A (`3870ae8`)
- `Workers/OutboxPublisher/OutboxPublisherService.cs` — per-row outcome is `Confirmed`, `MessageFailure` (`PublishException`, i.e.
  returned/unroutable or nack → counts toward `MaxAttempts`) or `BrokerFailure` (channel/connection creation, connection loss, confirm
  timeout → row untouched, stays `Pending`, `PublishAttempts` unchanged). Any broker failure makes the loop back off (exponential,
  `InitialBackoff` … `MaxBackoff`, jitter) with a warning `RabbitMQ unavailable (...)`. Class docs updated.
- Tests: `WorkersFixture.CreateHost` gained a `configureServices` hook; `CapturingLoggerProvider` (new); the outage test was replaced by
  `Broker_outage_leaves_rows_pending_without_counting_attempts_and_they_are_published_when_it_is_back` (MaxAttempts 3, waits for
  ≥ 5 backoff warnings, then asserts `Pending`, `PublishAttempts = 0`, `LastError`/`NextAttemptAt` null; broker back → all Published).
  The unroutable test still fails rows after the limit.

Part B (`7c453ee`)
- `Domain/Entities/ProcessingTask.cs` — `IsTerminal`, `StartAttempt` (abandons Running attempts, `AttemptNo = max + 1`, task
  `InProgress`), `Succeed(orderId, attemptId)`, `CloseAttemptOfCompletedTask`, `ScheduleRetry` (attempt `Failed`, task `RetryScheduled`,
  `RetryCount + 1`, unless the task was completed meanwhile).
- `Domain/Entities/ProcessingAttempt.cs` — `Succeed` / `Fail` / `Abandon` (`ErrorCode = abandoned`), error text truncated to column sizes.
- `Application/Orders/IOrderReservationService.cs` (new) — `GetTaskStatusAsync`, `StartAttemptAsync`, `ReserveAsync`,
  `RecordTransientFailureAsync`, `RecordRejectedMessageAsync`; `ReservationOutcome`; `OrderProcessingEvents`
  (`order.message_rejected`, `order.insufficient_stock`, `order.already_exists`).
- `Infrastructure/Persistence/Orders/OrderReservationService.cs` (new) — see "Reservation" below; registered in `DependencyInjection`.
- `Infrastructure/RabbitMQ/RabbitMqQueueConsumer.cs` (new) — reusable consumer plumbing for task 007: channel with
  `consumerDispatchConcurrency`, `BasicQos(prefetch = concurrency)`, manual ack; handler returns `Ack` / `Reject` (DLQ) / `Requeue`;
  a throwing handler → requeue; settle errors logged; no exception escapes the callback; `StopAsync` cancels the consumer and waits
  for in-flight handlers.
- `Workers/OrderWorker/` (new) — `OrderWorkerService` (BackgroundService; starts the consumer with backoff while RabbitMQ is down;
  topology mismatch → stop host), `OrderMessageHandler` (per-message flow), `OrderMessageParser` (envelope/payload validation, poison
  reasons), `ProcessingErrorClassifier` (error codes + sanitised messages), `OrderWorkerOptions` (`Concurrency` 4, backoff) + validator.
- `Workers/WorkerCurrentUser.cs` — now scoped and settable (default `system:workers`); the Order Worker sets `system:order-worker`.
- `Workers/WorkerHostSetup.cs` — registers options, handler and `OrderWorkerService` (after the topology initializer and publisher).
- Tests: `Integration/Workers/OrderWorkerTests.cs`, `Integration/OrderEndToEndTests.cs`, `Integration/Workers/HostedServiceTestExtensions.cs`,
  `Workers/OrderMessageParserTests.cs`, `Workers/ProcessingErrorClassifierTests.cs`, `Domain/ProcessingAttemptFlowTests.cs`;
  `WorkersFixture` (`BuildWorkersHost` static, `WithServicesAsync`, `PublishAsync`), `ApiFactory.DatabaseConnectionString`,
  `CapturingLoggerProvider` records scope values; publisher tests use hosts without the Order Worker.

### Handling one message (`OrderMessageHandler`)
1. Parse (`OrderMessageParser`): invalid JSON, wrong `eventType`, no `taskId`/`messageId`/`correlationId`, payload not an object,
   payload `taskId` ≠ envelope `taskId`, missing channel/customer/external id, no items, item without product / non-positive stock /
   negative price, duplicate product → **poison**: `SystemEventLog` Error `order.message_rejected` `{messageId, reason}` → `BasicReject(requeue: false)`
   → `order.processing.dlq`.
2. Set `WorkerCorrelationContext.CorrelationId` (envelope) and `WorkerCurrentUser` (`system:order-worker`); log scope
   `MessageId, TaskId, CorrelationId, WorkerName, WorkerInstanceId` (+ `AttemptNo`, `OrderId` when known).
3. Task unknown → poison (as above). Task `Succeeded`/`Failed` → ACK ("Duplicate delivery").
4. `StartAttemptAsync` (own committed transaction): `SELECT ... FOR UPDATE` on the task, re-check terminal (→ ACK), abandon Running
   attempts, insert attempt `max + 1`, task `InProgress`.
5. `ReserveAsync` → ACK after it returned (i.e. after commit).
6. Exception in `ReserveAsync` → `ProcessingErrorClassifier` → `RecordTransientFailureAsync` (attempt `Failed` with `ErrorCode` /
   sanitised `ErrorMessage`, task `RetryScheduled`, `RetryCount + 1`) → `BasicNack(requeue: true)`. Any other exception (e.g. database
   down before the attempt exists) → requeue by the consumer.
   Handler work uses `CancellationToken.None`: an in-flight message finishes on shutdown (bounded by the host shutdown timeout).

### Reservation (`OrderReservationService.ReserveAsync`)
- One transaction: lock the task row (`FOR UPDATE`) and re-check it is not terminal (a concurrent duplicate delivery waits here and then
  finds the task completed → `TaskAlreadyCompleted`, its attempt closed as Succeeded, nothing reserved).
- Items sorted by `ProductId`; per item
  `UPDATE "Inventories" SET "ReservedStock" = "ReservedStock" + @stock, "UpdatedAt" = @now, "UpdatedBy" = @actor
   WHERE "ProductId" = @productId AND "AvailableStock" >= @stock RETURNING "TotalStock", "ReservedStock", "AvailableStock"`.
- All updated → `Order(Reserved)` with items (payload unit price, `TotalAmount` from items), one `Reserve` movement
  (`ReferenceType = Order`, `ReferenceId = OrderId`) and one `inventory.changed` outbox row (snapshot payload, envelope correlation id)
  per item, task `Succeeded` + `OrderId`, attempt `Succeeded`; `SaveChanges` + commit.
- Any item 0 rows → **rollback** (nothing reserved), then a new transaction: `Order(InsufficientStock)` with items, no movement/outbox,
  task `Succeeded` + `OrderId`, attempt `Succeeded`, `SystemEventLog` Warning `order.insufficient_stock`
  `{orderId, taskId, items: [{productId, requested, available}]}` (available read after the rollback).
- Unique violation on `UX_Orders_ChannelId_ExternalOrderId` (either path) → rollback, new transaction: task `Succeeded` pointing to the
  existing order, attempt `Succeeded`, `SystemEventLog` Warning `order.already_exists`, ACK.

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0.

| Command | Result |
| --- | --- |
| `dotnet test` after Part A | Failed 0, Passed 224, Total 224 |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final, 4 consecutive full runs) | Failed 0, Passed 263, Total 263 (×4) |
| `dotnet test --filter` the 3 concurrency tests (no oversell, no deadlock, two publishers) | Failed 0, Passed 3 (plus the 4 full runs above) |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 183, Total 183 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 80, Total 80 |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` (no migration in this task) |

### Mutation checks (each reverted; reverts verified by grep)
| Mutation | Result |
| --- | --- |
| Part A: broker failures counted as attempts again | `Broker_outage_leaves_rows_pending_...` **failed** |
| Remove `AND "AvailableStock" >= @stock` | Test 2 **failed**: `Timed out ... 30 tasks Succeeded. Last value: 5`. Oversell was still impossible — the `CK_Inventories_ReservedStock_Range` CHECK rejected the 6th reservation (23514 → treated as transient → requeue loop) — but 25 orders never completed. The DB constraint is the last line of defence, the conditional update is the working check. |
| Remove the `ProductId` sort in the reservation | Test 4 **failed** 4/4 runs: 3× timeout (13–14 of 40 tasks succeeded in 60 s), 1× `Npgsql.PostgresException : 40P01: deadlock detected` raised by a concurrent import deadlocking with an unordered reservation. |
| Remove the handler's terminal-status check only | Test 5 **passed** — defence in depth: `StartAttemptAsync` re-checks under the row lock and ACKs ("task completed meanwhile"). |
| Remove the handler check **and** the `StartAttemptAsync` re-check | Test 5 **failed** (`Timed out ... duplicate delivery acknowledged`): the domain guard throws, the message is requeued repeatedly instead of ACKed. |

### Stock-write audit (repeated from task 003)
`grep` over `src/` (excluding tests/migrations) for `TotalStock`/`ReservedStock` assignments, `ExecuteUpdate`, `ExecuteSql*`, `SqlQuery`,
`SqlRaw`, `"Inventories"`. Writers:
1. `InventoryImportService` `UPDATE ... "TotalStock" + @n` — `Import` movement + outbox row in the same transaction (task 003).
2. `OrderReservationService` `UPDATE ... "ReservedStock" + @n` — `Reserve` movement + outbox row per item in the same transaction; on the
   insufficient-stock path the updates are rolled back before anything else is written.
3. `Inventory` constructor — the all-zero row at product creation.
The `ExecuteSqlAsync` in `OrderReservationService` is the task-row `SELECT ... FOR UPDATE` lock (no write).

### ACK / exception rules
- ACK only after `ReserveAsync` / `StartAttemptAsync` returned, i.e. after their commit; poison is rejected only after the
  `SystemEventLog` row was saved (if that fails the message is requeued).
- `RabbitMqQueueConsumer.OnReceivedAsync` wraps handler and settle in `try/catch`; nothing escapes the callback.

### Required tests → where (`OrderWorkerTests` unless noted)
| # | Requirement | Test |
| --- | --- | --- |
| 1 | Happy path: Reserved, items/total, 10/3/7, one Reserve movement, outbox row with envelope correlation id, task/attempt | `Order_with_enough_stock_is_reserved_with_movement_outbox_and_attempt` (also `CreatedBy = system:order-worker`) |
| 2 | Stock 5, 30 orders, two instances → 5 Reserved, 25 InsufficientStock, 5/5/0, 5 movements | `Concurrent_orders_for_the_last_units_never_oversell` (also 25 `order.insufficient_stock` warnings) |
| 3 | All or nothing | `One_short_item_reserves_nothing` (also the warning payload: requested 5, available 1) |
| 4 | Opposite item order + concurrent imports → all complete | `Concurrent_multi_item_orders_in_opposite_order_and_imports_do_not_deadlock` (40 orders, 2 instances × concurrency 8, 10 concurrent imports; asserts every task succeeded on attempt #1 with no error code, so a deadlock-then-retry also fails the test) |
| 5 | Same envelope twice → one order/reservation/attempt; second ACKed | `Duplicate_delivery_is_acknowledged_without_a_second_reservation` (queue empty afterwards) |
| 6 | Crash recovery | `Redelivery_after_a_crash_abandons_the_running_attempt_and_starts_a_new_one` |
| 7 | Poison: invalid JSON + unknown taskId → DLQ + SystemEventLog, nothing else | `Poison_messages_go_to_the_dlq_and_are_logged` |
| 8 | Injected transient failure → attempt #1 Failed with code, retry, attempt #2 Succeeded, one order | `Transient_failure_is_recorded_and_the_redelivery_succeeds` (test seam = decorator of `IOrderReservationService`) |
| 9 | End to end through the API host | `OrderEndToEndTests.Order_submitted_through_the_api_is_reserved_by_the_worker_with_one_correlation_id` (task, outbox rows and every worker log line's scope carry the `X-Correlation-Id`; `AttemptNo = 1` in scope) |
| Unit | parsing/poison classification; error-code mapping | `OrderMessageParserTests` (10 poison cases), `ProcessingErrorClassifierTests`, `ProcessingAttemptFlowTests` |

## Decisions recorded (as requested)
- **Insufficient stock (Cowork decision, 2026-09-30):** the order is stored with `Status = InsufficientStock` and the **task is
  `Succeeded`** (deterministic business outcome; retrying cannot change it). `Failed` tasks are reserved for technical failures that need
  an operator. The business outcome lives on `Order.Status`; step 11 alerts can use the `order.insufficient_stock` event. Small change if
  the user disagrees.
- **Interim retry policy (limitation until task 007):** a transient failure is nacked with `requeue: true`, i.e. redelivered
  **immediately** without delay, bounded only by the queue's `x-delivery-limit = 10`; the broker then dead-letters it to
  `order.processing.dlq`. The task stays `RetryScheduled` in that case (no final `Failed` + alert yet). During a longer database outage the
  10 deliveries are used up within seconds. Task 007 replaces this with delayed retry and final-failure handling.

## Deviations from the task
1. **Part A commits confirmed rows** of a batch that hit a broker failure mid-way (the task said "roll back the batch"). Rows the broker
   did not confirm are left untouched exactly as required (Pending, no attempt counted); committing the confirmed ones avoids republishing
   messages that were already delivered. Channel/connection creation failures publish nothing, so nothing is written.
2. **Concurrent duplicate deliveries:** a crash between publisher confirm and commit can put two copies of a message in the queue, consumed
   by two instances at once. The second `StartAttemptAsync` then marks the first (still running) attempt abandoned. To keep this safe the
   reservation transaction locks the task row and re-checks its status; an attempt's real outcome may overwrite `abandoned` (documented on
   `ProcessingAttempt.Succeed/Fail`). Stock correctness does not depend on the attempt history.
3. **Audit actor:** `WorkerCurrentUser` changed from a singleton to a scoped, settable service so the Order Worker's rows carry
   `system:order-worker` (orders, items, movements via the interceptor; `UpdatedBy` on inventory via the raw SQL).
4. Extra event `order.already_exists` (Warning) when the final idempotency guard is hit — the task asked for a warning log; it is also
   written to `SystemEventLog` so it is visible on the dashboard.
5. Test 1 asserts the reservation's outbox row is `Pending` **or** `Published`: the same Workers host runs the Outbox Publisher, which may
   publish it before the assertion. It is written `Pending` in the reservation transaction (checked with its correlation id, movement link
   and payload).

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- Delayed retry, final `Failed` + alert after the retry limit, DLQ handling (007).
- A channel-level (not connection-level) closure of the consumer channel is not re-established by the Order Worker; connection loss is
  (client automatic recovery). Consider re-creating the consumer in 007.
- `Payment Succeeded`, invoice/email tasks, release/deduct of reserved stock — later tasks.
- Expected constraint/unique violations are still logged by EF at Error level (step 9).

## Manual steps for the user
No new migration. With PostgreSQL, RabbitMQ, API and Workers running (README sections 1, 5, 6):
1. Import stock for a product (`POST /api/inventory/import`, e.g. 5 units), then submit an order for 2 units
   (`POST /api/orders/website`). Within a second the order exists with `Status = Reserved`; inventory shows total 5 / reserved 2 /
   available 3 (`GET /api/inventory`); `GET /api/inventory/movements` shows a `Reserve` movement.
2. Submit an order for more than the available stock → an order with `Status = InsufficientStock`, stock unchanged, and a
   `SystemEventLog` row `order.insufficient_stock` (query the database until the orders endpoints of task 008 exist:
   `SELECT "ExternalOrderId", "Status" FROM "Orders" ORDER BY "CreatedAt" DESC LIMIT 5;`).

# Task 006 — Order Worker: consume `order.created`, atomic stock reservation, attempt tracking

Status: Open
Written by: Cowork, 2026-09-30
Depends on: 005 (done)

## Context
Doc §8.2 and acceptance criteria "duplicate orders not processed twice", "concurrent orders for the same SKU do not oversell",
"Total/Reserved/Available correct", "InventoryMovement + OutboxEvent in the same transaction", "trace an order to task, worker,
instance, attempt, error". This is the most safety-critical task so far. Read `docs/implementation-log.md` first (lock order,
stock-mutation rule, correlation context, decision A).

## Part A — Follow-up from review 005 (separate commit)
**Broker outages must not fail outbox rows.** In `OutboxPublisherService`:
- Count toward `PublishAttempts`/`MaxAttempts` **only** message-specific failures: returned/unroutable (`PublishReturnException`)
  and broker nack (`PublishException`).
- Connection/channel creation failures, connection loss during the batch and confirm timeouts: roll back the batch (rows unchanged,
  still `Pending`), log a warning, and back off the **loop** (same exponential backoff, cap 5 min). Rows are retried when the broker is back.
- Test: with `MaxAttempts = 3`, keep the broker down for longer than 3 publisher polls → rows still `Pending` with `PublishAttempts = 0`;
  broker back → all rows `Published`. Keep the existing unroutable test (still fails after the limit).

## Part B — Order Worker

Placement: `Omnichannel.Workers/OrderWorker/` (consumer + handler), reservation SQL in
`Omnichannel.Infrastructure/Persistence/Orders/` (e.g. `OrderReservationService`), contracts in `Application/Orders`.
RabbitMQ consumer plumbing that task 007 will reuse goes in `Omnichannel.Infrastructure/RabbitMQ/` (keep it small; 007 extends it).

### Consumer
- Queue `order.processing`; manual ack; `BasicQos(prefetch = Concurrency)`; `OrderWorker:Concurrency` (default 4) via the channel's
  consumer dispatch concurrency. One DI scope per message.
- Worker identity: `WorkerName = OrderWorker`, `WorkerInstanceId` from `Worker:InstanceId`; audit actor `system:order-worker`.
- Before any work: set `WorkerCorrelationContext.CorrelationId` from the envelope; open a log scope with `MessageId`, `TaskId`,
  `CorrelationId`, `WorkerName`, `WorkerInstanceId` (+ `AttemptNo` once known, `OrderId` once known).
- Graceful shutdown: cancel the consumer, let in-flight handlers finish (bounded by the host shutdown timeout); unacked messages
  are redelivered by RabbitMQ.

### Handling one message
1. **Parse** the envelope with `MessageJson` → `OrderCreatedPayload`. Invalid JSON, wrong `eventType`, missing `taskId`, or no
   `ProcessingTask` with that id → **poison**: `SystemEventLog` Error `order.message_rejected` (messageId, reason), then
   `BasicReject(requeue: false)` → `order.processing.dlq`. Never throw out of the consumer callback.
2. **Idempotency / redelivery:** load the task. If it is terminal (`Succeeded` or `Failed`) → ACK, log "duplicate delivery", done.
3. **Start attempt** (own short transaction, committed before the business work so it survives failures):
   lock the task row (`SELECT ... FOR UPDATE`), mark any `Running` attempts of this task as `Failed` with
   `ErrorCode = abandoned` (a previous worker died), insert `ProcessingAttempt(AttemptNo = max + 1, WorkerName, WorkerInstanceId,
   StartedAt)`, set task `InProgress`, commit. The unique `(TaskId, AttemptNo)` index is the last guard.
4. **Reserve** — one explicit transaction, the core of this task:
   - Items sorted by `ProductId` (same lock order as the import).
   - Per item one statement, no read-modify-write:
     ```sql
     UPDATE "Inventories"
     SET "ReservedStock" = "ReservedStock" + @stock, "UpdatedAt" = @now, "UpdatedBy" = @actor
     WHERE "ProductId" = @productId AND "AvailableStock" >= @stock
     RETURNING "TotalStock", "ReservedStock", "AvailableStock";
     ```
     This conditional update **is** the doc's "Check AvailableStock" step — there is no separate check-then-update.
   - If every item updated: insert `Order(Status = Reserved)` with items (unit price = payload snapshot; `TotalAmount` from items),
     one `InventoryMovement(Reserve, ReferenceType = Order, ReferenceId = OrderId)` per item, and one `OutboxEvent(inventory.changed)`
     per movement (payload = `InventoryChangedPayload` snapshot, correlation id from the envelope). Task → `Succeeded`, `OrderId` set;
     attempt → `Succeeded`, `FinishedAt`. Commit, **then** ACK.
   - If any item updated 0 rows: **roll back** (nothing reserved — all or nothing), then in a new transaction insert
     `Order(Status = InsufficientStock)` with its items (no reservation, no movements, no outbox), task → `Succeeded` with `OrderId`,
     attempt → `Succeeded`, and a `SystemEventLog` Warning `order.insufficient_stock` (orderId, taskId, the short items: productId,
     requested, available at the time). Commit, then ACK. See "Decision" below.
   - Unique violation on `UX_Orders_ChannelId_ExternalOrderId` (final guard; should not happen after intake idempotency):
     roll back, set task `Succeeded` pointing to the existing order, attempt `Succeeded`, log Warning, ACK.
5. **Transient failure** (database unavailable, timeout, deadlock `40P01`, serialization `40001`, anything unexpected):
   roll back; in a new transaction attempt → `Failed` with `ErrorCode` (`db_unavailable`, `deadlock`, `unexpected`…) and a
   sanitised `ErrorMessage` (no connection strings, no stack traces), task → `RetryScheduled`, `RetryCount + 1`; then
   `BasicNack(requeue: true)`. **Interim policy until task 007:** immediate redelivery bounded by the queue's `x-delivery-limit = 10`
   (then the broker dead-letters it). Document this limitation in the report.

### Decision (Cowork, 2026-09-30): insufficient stock
The order **is** stored with `Status = InsufficientStock` (visible and traceable in the order list; the idempotency key already
prevents resubmission), and the **task is `Succeeded`**: the processing ran to completion with a deterministic business outcome;
retrying cannot change it. `Failed` tasks are reserved for technical failures that need an operator (retry/DLQ). The business
outcome lives on `Order.Status`; step 11 alerts use the `order.insufficient_stock` event. Record this in the report; if the user
disagrees it is a small change.

## Tests (required)
Integration (PostgreSQL + RabbitMQ; reuse the Workers fixture):
1. **Happy path:** import 10, submit an order for 3 → Order `Reserved` with items and total; inventory 10/3/7; one `Reserve`
   movement; one `Pending` `inventory.changed` outbox row with the envelope's correlation id; task `Succeeded` with `OrderId`;
   attempt #1 `Succeeded` with `WorkerName`/`WorkerInstanceId`.
2. **No oversell:** stock 5; 30 orders of 1 unit for the same product processed by **two** worker instances concurrently →
   exactly 5 `Reserved`, 25 `InsufficientStock`; inventory 5/5/0; 5 movements; never a negative or over-reserved value.
3. **All or nothing:** product A has enough, B has not → no reservation on A or B, order `InsufficientStock`, no movements.
4. **No deadlock:** concurrent multi-item orders with products in opposite order, while imports run → all complete.
5. **Duplicate delivery:** the same envelope published twice → one order, one reservation, one succeeded attempt; second delivery ACKed.
6. **Crash recovery:** a task left `InProgress` with a `Running` attempt #1 (inserted by the test) is delivered → attempt #1 becomes
   `Failed/abandoned`, attempt #2 `Succeeded`, one order.
7. **Poison:** invalid JSON and unknown `taskId` → message in `order.processing.dlq`, `SystemEventLog` row, nothing else written.
8. **Transient failure:** inject one failure (test seam on the reservation service) → attempt #1 `Failed` with error code, task
   `RetryScheduled` then `Succeeded` on redelivery, attempt #2 `Succeeded`, one order.
9. **End to end:** `POST /api/orders/website` through the API host → Outbox Publisher → Order Worker → order `Reserved`,
   correlation id identical on task, attempts' log scope, order outbox rows.
Unit: envelope/payload parsing and poison classification; error-code mapping of exceptions.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (unit/integration counts); run the concurrency tests 3 times.
- [ ] Mutation checks, each reverted: remove the `AvailableStock >= @stock` condition → test 2 fails; remove the `ProductId`
      sort → test 4 fails (or explain why it cannot); remove the terminal-status check → test 5 fails.
- [ ] Every stock mutation still writes a movement + outbox row in the same transaction (repeat the stock-write audit from 003).
- [ ] ACK only after commit; no exception escapes the consumer callback.
- [ ] Manual check documented: submit an order with stock available → order `Reserved`; without stock → `InsufficientStock`.

## Out of scope
Delayed retry and final-failure handling (007), orders/processing read endpoints (008), release/deduct of reserved stock (later),
stock-sync consumers (013), alerts (016).

# Task 008 — Orders and processing read API, manual retry, audit events

Status: Open
Written by: Cowork, 2026-09-30
Depends on: 007 (done)

## Context
Doc §11 API contract (`GET /api/orders`, `GET /api/orders/{id}`, `GET /api/processing/tasks`, `POST /api/processing/tasks/{id}/retry`),
§5.3 (filter tasks by TaskType, Status, WorkerName, WorkerInstanceId, AttemptNo, OrderId, ErrorCode) and acceptance criterion
"look up an order and see task, worker, instance, attempt, error". These endpoints feed the frontend pages in tasks 013 and 016.
Follow the existing conventions: paging contract, camelCase error keys, thin controllers, services in
`Infrastructure/Persistence/<Feature>`, contracts/validators in `Application/<Feature>`.

## Part A — Follow-up from review 007 (separate commit)
In `RabbitMqQueueConsumer`, when the retry copy cannot be published and the message is NACK-requeued, first wait a bounded backoff
(`RetryBackoff`, e.g. 1 s doubling to 30 s per consecutive failure of this consumer, reset on success) so a persistently unroutable copy
cannot spin; rate-limit the error log. Test: with the delay exchange missing, the message is redelivered at most a few times per
10 seconds and is not lost; after re-declaring the topology it is processed.

## Part B — Orders

### `GET /api/orders` — AnyStaff
Paged (existing contract). Filters: `channel` (slug), `status` (`OrderStatus`), `customerId`, `from`/`to` (UTC, on `CreatedAt`,
inclusive), `search` (external order id or customer name, ILIKE escaped). `sortBy`: `createdAt` (default, **descending** by default
for this endpoint), `totalAmount`, `externalOrderId`. Item:
`orderId, externalOrderId, status, totalAmount, itemCount, channel { channelId, slug, name }, customer { customerId, name }, createdAt`.

### `GET /api/orders/{id}` — AnyStaff
Order header as above plus:
- `items[]`: `productId, sku, productName, stock, unitPrice, lineTotal`.
- `tasks[]`: every `ProcessingTask` with this `OrderId` — `taskId, taskType, status, retryCount, correlationId, messageId, createdAt,
  updatedAt, attempts[]` (`attemptNo, status, workerName, workerInstanceId, errorCode, errorMessage, startedAt, finishedAt`, ordered by
  `attemptNo`).
404 `order_not_found` if missing.

## Part C — Processing tasks

### `GET /api/processing/tasks` — AnyStaff
Paged. Filters: `taskType`, `status`, `orderId`, `correlationId`, `from`/`to` (on `CreatedAt`), and attempt-level filters
`workerName`, `workerInstanceId`, `errorCode`, `attemptNo` — a task matches when **any** of its attempts matches (SQL `EXISTS`).
Sort: `createdAt` desc (default), `updatedAt`. Item: task fields + `attemptCount` + `lastAttempt` (same shape as above, null when none).

### `GET /api/processing/tasks/{id}` — AnyStaff
Task, all attempts, `orderId`, and the status of the outbox row that carried its message (`messageId` → `OutboxEvent.Status`,
`PublishedAt`, `PublishAttempts`, `LastError`) so the UI can show "not published yet" vs "published, not processed". 404 `task_not_found`.

### `POST /api/processing/tasks/{id}/retry` — ManagerOrAdmin
Manual retry of a **`Failed`** task (doc §3.4: "Failed task must link to error detail and retry").
1. One transaction: lock the task (`FOR UPDATE`); not `Failed` → 409 `task_not_failed`.
2. Find the original outbox row by the task's `MessageId`; missing → 409 `retry_source_missing`.
3. Insert a **new** `OutboxEvent` copying `EventType`, `AggregateId`, `Payload`, `CorrelationId` (new `OutboxEventId`); set the task's
   `MessageId` to the new id, `Status = Pending`, and record the **attempt budget reset** (see schema). Attempts are kept.
4. `SystemEventLog` Information `processing.retry_requested` `{taskId, taskType, requestedBy, previousMessageId, newMessageId,
   attemptBudgetStart}` with the task's correlation id.
5. Commit → 202 `{ taskId, status: "Pending", messageId }`. The Outbox Publisher and Order Worker then process it as usual.

The republished message uses its **original routing key** (e.g. `order.created`), so it reaches the right consumer; the doc's
`processing.retry` routing key stays unused — record this as a deviation. The DLQ copy is left in place (inspection only).

Schema (migration `ProcessingTaskAttemptBudget`): `ProcessingTask.AttemptBudgetStart int NOT NULL DEFAULT 0` — the number of attempts
that existed at the last manual retry. The consumer's `MaxAttempts` check uses `AttemptNo - AttemptBudgetStart`, so a manually retried
task gets a fresh budget while `AttemptNo` keeps counting up. Update `OrderMessageHandler`/`RetryPolicy` accordingly.

## Part D — Audit events
Write `SystemEventLog` Information rows for important operations (doc §6 "Audit"): `processing.retry_requested` (above) and
`inventory.imported` (`{referenceId, itemCount, totalQuantity}` with the request's correlation id, in the import transaction).
No read endpoint yet (the Logs/Audit page comes with task 017/018).

## Tests (required)
Integration:
1. Orders list: filters (`channel`, `status`, `from`/`to`, `search` incl. `50%` literal), default newest first, paging, `sortBy=unknown` → 400.
2. Order detail: items with product data and line totals; tasks with attempts incl. worker name/instance/error code for an order that
   needed a retry; unknown id → 404 `order_not_found`.
3. Tasks list: `status=Failed`, `errorCode=timeout`, `workerName=OrderWorker`, `attemptNo=2` each return exactly the expected tasks.
4. Task detail shows the outbox publish status of its message.
5. **Manual retry end to end:** inject a permanent failure → task `Failed` (+ DLQ); remove the injection; `POST .../retry` → 202; the task
   is processed again and the order is `Reserved`; attempts continue numbering (#2 after #1); a new outbox row exists; the
   `processing.retry_requested` event has the user id.
6. **Fresh budget:** a task that failed after `MaxAttempts` transient failures, retried manually while the failure persists, gets
   `MaxAttempts` new attempts (not zero) before failing again.
7. Retry of a non-failed task → 409 `task_not_failed`; two concurrent retries of the same failed task → exactly one 202, one 409, one new outbox row.
8. Staff `POST .../retry` → 403; anonymous `GET /api/orders` → 401.
9. `inventory.imported` event written with the import's correlation id.
Unit: validators (filters, sort whitelist), budget arithmetic.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (unit/integration counts).
- [ ] Migration + clean `has-pending-model-changes`; migration SQL in the report.
- [ ] Queries are single round-trips per page (no N+1): state how order detail and task list load attempts.
- [ ] Manual check documented: open an order that needed a retry via `GET /api/orders/{id}` and see both attempts; retry a failed task.

## Out of scope
Frontend, alerts, system-event read endpoint, DLQ purge/re-drive tooling, user management (009).

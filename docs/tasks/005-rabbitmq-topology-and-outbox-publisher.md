# Task 005 — RabbitMQ topology, message envelope, Outbox Publisher

Status: Open
Written by: Cowork, 2026-09-30
Depends on: 004 (done)

## Context
Pending `order.created` (task 004) and `inventory.changed` (task 003) outbox rows accumulate in PostgreSQL. This task
publishes them to RabbitMQ reliably (doc §8.3, §10, §13 "Outbox publish fail → stays Pending; publisher retries").
No consumers yet: the Order Worker is task 006, shared retry/DLQ handling task 007, stock sync task 013.
The **queue topology is defined completely here**, including dead-lettering, because quorum-queue arguments cannot be
changed later without deleting the queue.

Placement: RabbitMQ code in `Omnichannel.Infrastructure/RabbitMQ/`; the publisher `BackgroundService` in
`Omnichannel.Workers/OutboxPublisher/`. The API still has **no** RabbitMQ dependency (keep the assembly test green by
scoping it to the API assembly; Infrastructure now legitimately references the client).

## Part A — Follow-up from review 004
The user chose option 1 (implementation log, task 004 result). Separate commit, with migration `ProcessingTaskRequestHash`:
- `ProcessingTask.RequestHash` (`varchar(64)`, nullable): SHA-256 hex of the normalised content —
  `customerId` + items sorted by `productId` as `productId:stock` (prices excluded: they are a snapshot, not client input).
- Duplicate key with the same hash → 202 `duplicate: true` (unchanged). Different hash → 409 `order_conflict`, nothing written.
- Tests: same content resubmitted → 202 duplicate; different quantity → 409; different customer → 409.

## Part B — Topology

New package: `RabbitMQ.Client` (latest 7.x, async API). Test package: `Testcontainers.RabbitMq` (latest 4.x).

| Element | Definition |
| --- | --- |
| Exchange `omnichannel.topic` | topic, durable |
| Exchange `omnichannel.dlx` | direct, durable (dead-letter exchange) |
| `order.processing` | ← `order.created` |
| `stock.sync.website`, `stock.sync.pos`, `stock.sync.marketplace` | each ← `inventory.changed` (every channel gets its own copy; `marketplace` = the `digital-platform` channel) |
| `invoice.processing` | ← `invoice.requested` |
| `notification.email` | ← `email.requested` |
| `<queue>.dlq` for each queue above | ← `omnichannel.dlx` with routing key `<queue>.dlq` |

Every main queue: durable **quorum** queue with
`x-dead-letter-exchange = omnichannel.dlx`, `x-dead-letter-routing-key = <queue>.dlq`,
`x-dead-letter-strategy = at-least-once`, `x-overflow = reject-publish`, `x-delivery-limit = 10`
(safety net against crash loops; application-level retry comes in task 007). DLQs: durable quorum queues, no DLX.
`processing.retry` is reserved for manual retry (task 008) — no binding yet.

- One `RabbitMqTopology` class holds all names (constants) and a `DeclareAsync(IChannel)` that declares everything
  idempotently. The Workers host declares it at start-up before any publisher/consumer starts. An argument mismatch with an
  existing queue (`PRECONDITION_FAILED`) must stop the host with a clear log message — never delete queues automatically.
- `RabbitMqOptions` (section `RabbitMq`): `HostName`, `Port`, `VirtualHost`, `UserName`, `Password` (secret: env var /
  user-secrets only), `ClientProvidedName`; validated on start. One long-lived connection per host with automatic recovery.
- `README`: RabbitMQ user-secrets for the Workers project and how to run the Workers host.

## Part C — Envelope and Outbox Publisher

Envelope (doc §10), JSON body, serialised with `MessageJson`:
```json
{ "messageId": "<OutboxEventId>", "correlationId": "...", "taskId": "<guid|null>", "eventType": "order.created",
  "occurredAt": "<OutboxEvent.CreatedAt>", "payload": { ...the stored JSON as an object, not a string... } }
```
`taskId`: for `order.created` = `AggregateId`; for `inventory.changed` = null (stock-sync tasks are created per channel by
the consumer in task 013). AMQP properties: `MessageId`, `CorrelationId`, `Type = eventType`, `ContentType = application/json`,
persistent delivery, `Timestamp`; routing key = `EventType`.

Schema (migration `OutboxPublishTracking`): add to `OutboxEvent` `PublishAttempts int NOT NULL DEFAULT 0`,
`NextAttemptAt timestamptz NULL`, `LastError varchar(1000) NULL`. Replace the partial pending index so it serves
`WHERE "Status" = 'Pending' ORDER BY "NextAttemptAt" NULLS FIRST, "CreatedAt"` (decide the exact index and state it in the report).

Publisher loop (`BackgroundService`, one per Workers instance, several instances must be safe):
1. In a transaction: `SELECT ... FROM "OutboxEvents" WHERE "Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= now())
   ORDER BY "CreatedAt" LIMIT @batch FOR UPDATE SKIP LOCKED` (raw SQL is fine). Batch size and poll interval configurable
   (defaults 100 and 1 s; poll immediately again when a full batch was found).
2. Publish each row on a channel with **publisher confirmations** and `mandatory: true`. Success = broker confirm and not returned.
3. Success → `Status = Published`, `PublishedAt = now`. Failure (nack, returned/unroutable, timeout, connection error) →
   `PublishAttempts + 1`, `LastError` (sanitised, no credentials), `NextAttemptAt = now + backoff` (exponential, 1 s → max 5 min,
   with jitter). After **10** attempts → `Status = Failed` and a `SystemEventLog` Error row (`EventType = outbox.publish_failed`,
   `CorrelationId`, payload with `OutboxEventId`, `EventType`, `LastError`). Never loop forever.
4. Commit. A crash after confirm but before commit re-publishes the row later: delivery is **at-least-once** and consumers
   deduplicate by `messageId` (task 006). Document this on the publisher class.
5. Broker down: rows stay `Pending`, the loop keeps running with backoff, no exception escapes `ExecuteAsync`.
6. Graceful shutdown: honour `stoppingToken`; finish or roll back the current batch; no half-updated rows.
7. Logs carry `WorkerName = OutboxPublisher`, `WorkerInstanceId` (config `Worker:InstanceId`, default `{MachineName}-{ProcessId}`),
   `MessageId`, `CorrelationId`, `EventType`. Worker identity for auditing: `system:outbox-publisher`.

## Tests (required)
Integration (Testcontainers PostgreSQL + RabbitMQ; create a test fixture for the Workers side — do not start a second
`WebApplicationFactory`):
1. Topology: declared twice without error; every queue exists with the arguments above (check via the management API or
   passive declare + a message dead-lettered to its DLQ when rejected with `requeue: false`).
2. A pending `order.created` row → one message in `order.processing` with the exact envelope and AMQP properties;
   row becomes `Published` with `PublishedAt`.
3. A pending `inventory.changed` row → one message in **each** of the three `stock.sync.*` queues.
4. Two publisher instances running concurrently over 200 pending rows → every row published exactly once (count messages),
   all rows `Published`.
5. Broker unavailable (stop the container or point at a closed port) → rows stay `Pending`, `PublishAttempts` increases,
   `NextAttemptAt` in the future, host keeps running; broker back → rows published.
6. Unroutable message (e.g. an unknown event type inserted in the test) → not `Published`; after the attempt limit →
   `Failed` + `SystemEventLog` row. (Use a small configured limit in the test.)
7. Payload in the envelope is a JSON object equal to the stored payload.
Unit: backoff calculation (bounds, jitter range, cap), envelope building.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (report unit/integration counts).
- [ ] Migration + clean `has-pending-model-changes`; migration SQL in the report.
- [ ] Rows are marked `Published` only after a positive publisher confirm.
- [ ] No infinite retry; failures visible in `SystemEventLog`.
- [ ] Manual check documented: run API + Workers locally, submit an order, see the message in `order.processing`
      in the RabbitMQ management UI (http://localhost:15672).

## Out of scope
Consumers (006), consumer retry/DLQ handling (007), manual retry endpoint (008), stock-sync tasks (013), outbox row cleanup/archiving
(later), RabbitMQ user/permission hardening (025), Workers Dockerfile (024).

# Report 005 — RabbitMQ topology, message envelope, Outbox Publisher

Status: Done
Commit: 707802b (Part A, review-004 follow-up), 30fe05f (Parts B + C)

## Summary
The Workers host now declares the full RabbitMQ topology (quorum queues with DLX/DLQs) and runs an Outbox Publisher that
publishes pending outbox rows with publisher confirms, marks them `Published` only after a positive confirm, backs off on failure
and gives up after a limit (`Failed` + `SystemEventLog`). Several instances are safe (`FOR UPDATE SKIP LOCKED`); delivery is
at-least-once. The API still has no RabbitMQ dependency. Part A: a resubmission with different content → 409 `order_conflict`.
Build 0 warnings; 224/224 tests (153 unit, 71 integration), stable over 3 full runs.

## Files changed
Part A (`707802b`)
- `Domain/Entities/ProcessingTask.cs` — `RequestHash` (optional ctor parameter, exactly 64 chars), `RequestHashLength`.
- `Application/Orders/OrderRequestHash.cs` (new) — SHA-256 lower-case hex of `{customerId:D}|{productId:D}:{stock},...`, items sorted by productId.
- `Application/Orders/OrderIntakeContracts.cs` — `OrderErrorCodes.OrderConflict = "order_conflict"`; `IOrderIntakeService` doc.
- `Infrastructure/Persistence/Orders/OrderIntakeService.cs` — hash stored on the task; on a duplicate key, same hash → 202 duplicate,
  different hash → `ConflictException(order_conflict)` (warning logged). A stored `NULL` hash (tasks created before this migration) is
  treated as identical.
- `ProcessingTaskConfiguration` + migration `ProcessingTaskRequestHash`.
- Tests: `OrderIntakeTests` (reordered items → still duplicate; other quantity → 409; other customer → 409; one task/outbox row and the
  stored hash unchanged), `OrderRequestHashTests` (canonical form, order-independence, sensitivity).

Part B + C (`30fe05f`)
- Packages: `RabbitMQ.Client` 7.2.2 (Infrastructure), `Testcontainers.RabbitMq` 4.15.0 (tests). Tests project now references Workers.
- `Infrastructure/RabbitMQ/` (new): `RabbitMqTopology` (names, arguments, `DeclareAsync`, `RabbitMqTopologyMismatchException`),
  `RabbitMqOptions` + validator (password required, never logged), `RabbitMqConnectionProvider` (singleton, lazy connect, automatic +
  topology recovery, topology declared on first connection), `AddRabbitMq()` (called only by the Workers host).
- `Application/Common/Messaging/MessageEnvelope.cs` (new) — envelope record + `FromOutboxEvent` (taskId rule);
  `MessageJson.SerializeToUtf8Bytes`.
- `Domain/Entities/OutboxEvent.cs` — `PublishAttempts`, `NextAttemptAt`, `LastError` (≤ 1000, truncated); `MarkPublished`,
  `RecordPublishFailure` (returns true when the row becomes `Failed`); both only from `Pending`.
- `OutboxEventConfiguration` + migration `OutboxPublishTracking`.
- `Workers/OutboxPublisher/`: `OutboxPublisherService` (BackgroundService), `OutboxPublisherOptions` + validator, `OutboxBackoff`.
- `Workers/RabbitMqTopologyInitializer.cs` (hosted service registered before the publisher), `WorkerOptions` (`Worker:InstanceId`),
  `WorkerHostSetup.AddWorkerServices` (shared by `Program` and tests), `Program.cs`, `appsettings.json` (`RabbitMq` section, empty secrets).
- `README.md` section 6 — Workers user-secrets, run command, management UI, optional settings.
- Tests: `Integration/Workers/{WorkersFixture,OutboxPublisherTests}.cs`, `Integration/Eventually.cs`, `Workers/{OutboxBackoffTests,
  MessageEnvelopeTests}.cs`, `Domain/OutboxEventTests.cs`; `OrderIntakeTests.Api_has_no_rabbitmq_dependency` rescoped.

### Topology (as declared and verified)
| Element | Definition |
| --- | --- |
| `omnichannel.topic` | topic, durable |
| `omnichannel.dlx` | direct, durable |
| `order.processing` ← `order.created`; `stock.sync.website` / `.pos` / `.marketplace` ← `inventory.changed`; `invoice.processing` ← `invoice.requested`; `notification.email` ← `email.requested` | quorum, durable; `x-dead-letter-exchange=omnichannel.dlx`, `x-dead-letter-routing-key=<queue>.dlq`, `x-dead-letter-strategy=at-least-once`, `x-overflow=reject-publish`, `x-delivery-limit=10` |
| `<queue>.dlq` (6) | quorum, durable, no DLX; bound to `omnichannel.dlx` with key `<queue>.dlq` |

### Publisher behaviour
1. Transaction; `SELECT * FROM "OutboxEvents" WHERE "Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now)
   ORDER BY "CreatedAt" LIMIT @batch FOR UPDATE SKIP LOCKED` (EF `FromSql`, parameterised).
2. One channel per batch with publisher confirmations + tracking; all messages published (`mandatory: true`, routing key = event type),
   then each confirm awaited (batch confirm timeout `ConfirmTimeout`). `PublishReturnException` (unroutable), `PublishException` (nack),
   timeout and connection errors are failures.
3. Success → `MarkPublished`. Failure → `RecordPublishFailure` with `OutboxBackoff` (base 1 s × 2^(n−1), cap 5 min, equal jitter
   [base/2, base]); at `MaxAttempts` (10) → `Failed` + `SystemEventLog(Error, "outbox.publish_failed", correlationId, taskId, workerName,
   {outboxEventId, eventType, publishAttempts, lastError})`.
4. Save + commit with `CancellationToken.None` once messages are out ("finish the batch"); shutdown during publishing throws before
   saving → rollback, rows stay `Pending` (republished later). Documented at-least-once on the class.
5. Broker unreachable / connection recovering → every row of the batch records one failed attempt; the loop never throws. Any other
   batch error (e.g. database down) is logged and retried with backoff. Topology mismatch at runtime → Critical log, `ExitCode = 1`,
   `StopApplication()`.
6. Logs: scope `WorkerName = OutboxPublisher`, `WorkerInstanceId`; per message `MessageId`, `CorrelationId`, `EventType`.
   `LastError` never contains the RabbitMQ password (replaced by `***` defensively; verified by test and by a run of the real host).

### Index decision (`IX_OutboxEvents_Pending_CreatedAt`)
`CREATE INDEX ... ON "OutboxEvents" ("CreatedAt") INCLUDE ("NextAttemptAt") WHERE "Status" = 'Pending'`.
The batch query orders FIFO by `CreatedAt` (as in the task's step 1), so `CreatedAt` stays the key; `NextAttemptAt` is included so
rows still in backoff are filtered from the index without visiting the table. Ordering by `NextAttemptAt NULLS FIRST` was not used:
with a steady inflow, new (NULL) rows would always come first and starve retries that are already due.

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0 (containers `postgres:17`, `rabbitmq:4-management`).

| Command | Result |
| --- | --- |
| `dotnet test` after Part A | Failed 0, Passed 198, Total 198 |
| `dotnet ef migrations add ProcessingTaskRequestHash` / `OutboxPublishTracking` | `Done.` both |
| `dotnet ef migrations has-pending-model-changes ...` (final) | `No changes have been made to the model since the last migration.` |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final, 3 consecutive runs) | Failed 0, Passed 224, Total 224 (×3) |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 153, Total 153 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 71, Total 71 |

**Mutation checks** (each reverted):
- Confirmation tracking disabled → `Unroutable_row_fails_after_the_attempt_limit_and_is_logged` failed (returned message counted as published).
- `FOR UPDATE SKIP LOCKED` removed → `Two_publisher_instances_publish_every_row_exactly_once` failed 2/2 (duplicates).

**Broker outage test:** the verbose run shows the RabbitMQ container really stopped and restarted
(`Stop Docker container f0519f56a7da` → `Start Docker container f0519f56a7da` → `ready`), fixed host port, rows published afterwards.

**Real Workers host** (`Omnichannel.Workers.exe`, Development = DI validation on build, broker port closed, dummy DB without password):
logged `RabbitMQ is not reachable at start-up ...` (Warning), `Application started`, `Outbox publisher started (batch 100, poll 00:00:01,
max attempts 10)` with `WorkerName`/`WorkerInstanceId`, then `Outbox publish batch failed; next try in ...` for the unreachable database;
kept running until the 15 s timeout. The dummy password did not appear in the output.

### Migration SQL
`dotnet ef migrations script ProcessingTaskIdempotencyKey ProcessingTaskRequestHash`:
```sql
START TRANSACTION;
ALTER TABLE "ProcessingTasks" ADD "RequestHash" character varying(64);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260929095727_ProcessingTaskRequestHash', '10.0.12');

COMMIT;
```
`dotnet ef migrations script ProcessingTaskRequestHash OutboxPublishTracking`:
```sql
START TRANSACTION;
DROP INDEX "IX_OutboxEvents_Pending_CreatedAt";

ALTER TABLE "OutboxEvents" ADD "LastError" character varying(1000);

ALTER TABLE "OutboxEvents" ADD "NextAttemptAt" timestamp with time zone;

ALTER TABLE "OutboxEvents" ADD "PublishAttempts" integer NOT NULL DEFAULT 0;

CREATE INDEX "IX_OutboxEvents_Pending_CreatedAt" ON "OutboxEvents" ("CreatedAt") INCLUDE ("NextAttemptAt") WHERE "Status" = 'Pending';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260929100314_OutboxPublishTracking', '10.0.12');

COMMIT;
```
Existing rows get `PublishAttempts = 0`, `NextAttemptAt = NULL` (published on the first poll).

### Required tests → where
| # | Requirement | Test (`OutboxPublisherTests` unless noted) |
| --- | --- | --- |
| 1 | Topology declared twice; every queue/argument; DLQ on reject | `Topology_is_idempotent_and_matches_the_definition` (management API: type, durable, all 5 arguments, DLQ args, bindings, exchanges), `Rejected_message_is_dead_lettered_to_the_queue_dlq` |
| — | Mismatch stops the host | `Topology_mismatch_stops_the_host_at_start_up` (separate vhost with a classic `order.processing`) |
| 2 | `order.created` → exact envelope + AMQP properties; row Published | `Pending_order_created_row_is_published_with_envelope_and_properties` |
| 3 | `inventory.changed` → each `stock.sync.*` queue | `Inventory_changed_is_delivered_to_every_stock_sync_queue` (also `taskId` null, nothing in `order.processing`) |
| 4 | 2 instances, 200 rows → each exactly once | `Two_publisher_instances_publish_every_row_exactly_once` (batch 20, message ids compared) |
| 5 | Broker down → Pending, attempts++, NextAttemptAt future, host alive; back → published | `Rows_stay_pending_while_the_broker_is_down_and_are_published_when_it_is_back` |
| 6 | Unroutable → Failed after limit + SystemEventLog | `Unroutable_row_fails_after_the_attempt_limit_and_is_logged` (limit 3) |
| 7 | Envelope payload = stored payload as object | test 2 (`JsonNode.DeepEquals`), `MessageEnvelopeTests` |
| Unit | backoff; envelope | `OutboxBackoffTests`, `MessageEnvelopeTests`, `OutboxEventTests` (state rules) |

## Deviations from the task
1. **Broker unreachable at start-up does not stop the host.** The task requires the topology to be declared before any publisher starts
   and a mismatch to stop the host; it also requires the host to keep running while the broker is down. So: `RabbitMqTopologyInitializer`
   runs first and stops the host on a mismatch, but only warns when the broker is unreachable; the connection provider declares the
   topology on the first successful connection, before any channel is handed to the publisher.
2. **Pending index** — see "Index decision" (`INCLUDE ("NextAttemptAt")`, FIFO key `CreatedAt`).
3. **Worker audit identity:** `OutboxEvent` and `SystemEventLog` have no actor column, so `system:outbox-publisher` is not stored anywhere;
   the publisher is identified by `WorkerName = OutboxPublisher` (logs and `SystemEventLog.WorkerName`) and `WorkerInstanceId` (logs).
   The Workers host keeps `WorkerCurrentUser = system:workers` for auditable entities (none written by the publisher).
4. **`PublishAttempts` counts failed attempts only** (a row published on the first try has 0), as described in step 3 of the task.
5. **API RabbitMQ test** now checks the API assembly's references **and** that the API host's DI container has no
   `RabbitMqConnectionProvider` (Infrastructure references the client now; `AddRabbitMq()` is called only by Workers).
6. `WorkerHostSetup.AddWorkerServices` extracted so tests build Workers hosts with exactly the production registrations.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- **Manual check (acceptance criterion) not executed by me** — needs the user's secrets; steps below.
- Ordering: messages of the same aggregate can be published out of order across retries/instances (e.g. two `inventory.changed` for
  one product). Consumers must not assume order: stock sync re-reads current stock (already stated in the payload docs).
- A row whose `Payload` is not valid JSON would fail envelope building for the whole batch (retried with backoff, never published).
  Payloads are written only by `MessageJson` today; consider per-row isolation if external writers appear.
- Outbox row cleanup/archiving (later task), consumer dedup by `messageId` (006), consumer retry/DLQ policies (007).

## Manual steps for the user
1. Apply migrations `ProcessingTaskRequestHash` and `OutboxPublishTracking`:
   ```powershell
   $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
   dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
   ```
2. Workers secrets (README section 6): `ConnectionStrings:Default` (runtime role `omnichannel_app`), `RabbitMq:UserName`, `RabbitMq:Password`.
3. Manual end-to-end check: start RabbitMQ/PostgreSQL (`docker compose ... up -d`), run the API (`dotnet run --project src/Omnichannel.Api
   --launch-profile http`) and the Workers (`dotnet run --project src/Omnichannel.Workers`), submit an order with
   `POST /api/orders/website` (`.http` file), then open http://localhost:15672 → Queues → `order.processing`: 1 ready message
   (Get messages shows the envelope with `messageId`, `taskId`, `payload`).

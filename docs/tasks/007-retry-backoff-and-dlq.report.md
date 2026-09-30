# Report 007 — Delayed retry with backoff, final failure and DLQ handling for every consumer

Status: Done
Commit: e124527

## Summary
Consumers now return `Ack` / `Retry` / `Fail` / `Reject`; the shared plumbing publishes delayed retry copies through per-tier delay
queues (5 s / 30 s / 2 min / 10 min), turns the last attempt or a permanent error into a visible final failure (task `Failed` +
`processing.failed` + message in `<queue>.dlq`), and re-creates a consumer whose channel was closed. The Order Worker uses it; permanent
PostgreSQL errors (`22xxx`, `23514`, `23502`, `23503`) are no longer retried.
**Bug found and fixed on the way:** RabbitMQ 4 does not count `basic.nack(requeue: true)` toward `x-delivery-limit`, so task 006's interim
"requeue bounded by the delivery limit" was in fact unbounded for handler exceptions (measured ≈ 16,000 redeliveries per minute).
Handler exceptions are now counted by the consumer and retried with delay. No migration. Build 0 warnings; 306/306 tests
(216 unit, 90 integration), 3 consecutive full runs green.

## Files changed
- `Infrastructure/RabbitMQ/RabbitMqTopology.cs` — `RetryExchange` (`omnichannel.retry`, direct; every main queue bound with routing key =
  its own name), delay tiers (`DefaultRetryDelays`, `DelayNameOf`, `RetryDelayFor`, `DelayQueueArguments`), `DeclareAsync(channel, retryDelays)`.
  Existing exchanges/queues unchanged.
- `Infrastructure/RabbitMQ/RabbitMqOptions.cs` — `RetryDelays` (`TimeSpan[]?`, null = defaults) + validation (ascending, 100 ms – 1 day);
  `RabbitMqConnectionProvider` declares the configured tiers.
- `Infrastructure/RabbitMQ/QueueMessageHandling.cs` (new) — `ReceivedMessage` (+ `DeliveryCount`, original `Properties`), `HandlerResult`
  (`Ack`, `Retry(attemptNo, code, message)`, `Fail(attemptNo?, code, message)`, `Reject(reason)`), `FinalFailure`, `IQueueMessageHandler`
  (`HandleAsync`, `RecordFinalFailureAsync`), `RetryPolicy(MaxAttempts, DeliveryLimit)`, `QueueConsumerSettings`.
- `Infrastructure/RabbitMQ/RetryPublisher.cs` (new) — `IRetryPublisher` / `RabbitMqRetryPublisher`: copy with the same body and properties
  (`MessageId`, `CorrelationId`, `Type`, `ContentType`, `Timestamp`, persistent), broker `x-*` headers dropped, retry headers added;
  published to `omnichannel.delay.<tier>` with routing key = source queue, `mandatory`, **publisher confirm**.
- `Infrastructure/RabbitMQ/RabbitMqQueueConsumer.cs` — rewritten settlement (below); DI scope per delivery; `Interrupted` signal.
- `Infrastructure/RabbitMQ/ResilientQueueConsumer.cs` (new) — start with backoff, re-create on channel close / broker cancel (connection
  still open), graceful stop; `Current`, `Restarts` for diagnostics.
- `Infrastructure/RabbitMQ/RetryBackoff.cs` — moved from `Workers/OutboxPublisher/OutboxBackoff.cs` (unchanged algorithm, shared).
- `Application/Processing/ProcessingEvents.cs` (new) — `processing.failed`.
- `Application/Orders/IOrderReservationService.cs`, `Infrastructure/Persistence/Orders/OrderReservationService.cs` — `RecordFinalFailureAsync`
  (one transaction: attempt `Failed` — a new attempt when none exists for this delivery — task `Failed`, `SystemEventLog` Error
  `processing.failed` `{taskId, taskType, attemptNo, errorCode, errorMessage, queue}` with the task's correlation id; returns null when the
  task was already completed).
- `Domain/Entities/ProcessingTask.cs` — `Fail(attemptId, code, message)` (keeps an attempt failure already recorded by a transient failure).
- `Workers/OrderWorker/OrderMessageHandler.cs` — scoped `IQueueMessageHandler`; transient → records the attempt failure (task
  `RetryScheduled`) and returns `Retry`; permanent → `Fail`; final-failure callback also works from the message alone (delivery-limit guard).
- `Workers/OrderWorker/ProcessingErrorClassifier.cs` — returns `ProcessingError(code, message, isPermanent)`; new codes `data_error`,
  `check_violation`, `not_null_violation`, `foreign_key_violation`; message taken from the innermost `PostgresException`.
- `Workers/OrderWorker/OrderWorkerService.cs`, `OrderWorkerOptions.cs` (`MaxAttempts` 5), `WorkerHostSetup.cs` (handler scoped),
  `OutboxPublisherService.cs` (uses `RetryBackoff`).
- `README.md` section 6 — retry tiers, attempt limit, final failure and DLQ.
- Tests: `Integration/Workers/OrderWorkerRetryTests.cs` (partial of `OrderWorkerTests`), `Integration/Workers/FailureInjection.cs` (test seams
  for reservation service and retry publisher), `Workers/RetryTopologyTests.cs`, `Workers/ProcessingErrorClassifierTests.cs` (permanent
  table), `Workers/RetryBackoffTests.cs` (renamed); `WorkersFixture` (short test tiers 1–4 s for every host, delay queues purged,
  `ConnectAsync(vhost)`, management port bound to a fixed host port).

### Settlement (`RabbitMqQueueConsumer`)
| Situation | Action |
| --- | --- |
| `x-delivery-count >= DeliveryLimit` (last delivery the queue allows) | final-failure callback (`delivery_limit_reached`) **before** processing, then DLQ |
| `Ack` | ACK |
| `Reject` | reject → DLQ (poison, unchanged) |
| `Retry` and `attemptNo < MaxAttempts` | delayed copy (tier by attempt) with `x-retry-attempt`, `x-last-error-code`, confirmed → ACK; copy not confirmed → NACK requeue (not lost) |
| `Retry` at the limit, or `Fail` | final-failure callback → reject → DLQ (callback returns false when the task was completed meanwhile → ACK) |
| Handler throws (no attempt could be recorded) | `x-handler-failures + 1`; below `MaxAttempts` → delayed copy; at the limit → final failure `handler_error` → DLQ |
| Final-failure callback throws (e.g. database down) | delayed copy (`x-handler-failures + 1`), recorded on a later delivery — no hot loop |

Delayed copies are new messages, so their `x-delivery-count` starts at 0. The attempt number always comes from the database.

### Delay topology (as declared)
| Element | Definition |
| --- | --- |
| `omnichannel.retry` | direct, durable; binding `<queue>` → `<queue>` for each of the 6 main queues |
| `omnichannel.delay.5s` / `.30s` / `.2m` / `.10m` (exchange) | fanout, durable |
| same names (queue) | durable quorum, `x-message-ttl` = tier ms, `x-dead-letter-exchange = omnichannel.retry`, no dead-letter routing key, `x-dead-letter-strategy = at-least-once`, `x-overflow = reject-publish`; bound to its exchange |

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0, `rabbitmq:4-management`.

| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, 0 warnings |
| `dotnet test` (3 consecutive full runs) | Failed 0, Passed 306, Total 306 (×3) |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 216, Total 216 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 90, Total 90 |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` (no migration) |
| Workers exe, Development (DI validated on build), no broker | starts; `Cannot start consuming order.processing; next try in 00:00:00.70` (backoff), publisher running; dummy password not in the output |

### Mutation checks (each reverted, verified by grep)
| Mutation | Result |
| --- | --- |
| ACK instead of publishing the retry copy | Test 1 **failed**: `Timed out ... 1 tasks Succeeded. Last value: 0 Attempt errors: [timeout x1]` (message lost after attempt 1) |
| Skip the final-failure callback (reject directly) | Test 2 **failed**: `Timed out ... 1 tasks Failed. Last value: 0 Attempt errors: [timeout x3]` (message in the DLQ, task stuck `RetryScheduled`) |
| Classify `23514` as transient | Test 3 **failed**: `Assert.Single() Failure: The collection contained 5 items` (5 attempts instead of 1) |

### The RabbitMQ 4 finding
First run of the crash-loop test (handler throws on every delivery, plain NACK requeue as in task 006): after 60 s the task was still not
failed, the message still in `order.processing`, nothing in the DLQ, and the injected failure had been hit **16,090** times. The test
`Broker_counts_unsettled_returns_but_not_nack_requeues` now documents the broker behaviour: after `basic.nack(requeue: true)` the
`x-delivery-count` is unchanged; after a channel closes with the message unsettled it is incremented. Consequences:
- handler exceptions are bounded by the consumer (`x-handler-failures`, `MaxAttempts`) and retried with delay;
- the `x-delivery-count` guard remains for real crashes (process/channel dies), tested by returning a message unsettled 10 times;
- nothing in the code relies on NACK-requeue being bounded any more (only used when a retry copy cannot be published, i.e. broker trouble).

### Required tests → where (`OrderWorkerTests`, file `OrderWorkerRetryTests.cs`)
| # | Requirement | Test |
| --- | --- | --- |
| 1 | Transient ×2 then success; gaps ≥ tier delays; queues empty | `Transient_failures_are_retried_with_delay_until_the_attempt_succeeds` (gap ≥ 0.9 s and ≥ 1.9 s after attempts 1 and 2; main, delay and dead-letter queues empty) |
| 2 | Always transient, MaxAttempts 3 → task Failed, every attempt Failed, one `processing.failed`, DLQ with original ids + retry headers, no stock | `Always_failing_message_fails_the_task_after_max_attempts_and_goes_to_the_dlq` (`x-retry-attempt = 2`, `x-last-error-code = timeout`, payload attemptNo 3 / queue / taskType) |
| 3 | `23514` → one attempt, Failed, DLQ, no retry | `Permanent_error_fails_at_once_without_retry` (retry publisher never called) |
| 4 | Retry publish fails → requeued, not lost | `Failed_retry_publish_requeues_the_original_and_nothing_is_lost` |
| 5 | Crash loop → finalised Failed, DLQ | `Crash_loop_is_finalised_after_max_attempts_instead_of_looping` (5 handler failures, `handler_error`) and `Last_delivery_allowed_by_the_queue_is_finalised_without_processing` (x-delivery-count 10 → `delivery_limit_reached`, handler never ran) |
| 6 | Closed channel → consumer re-created | `Closed_consumer_channel_is_recreated_and_consumption_continues` (channel-level error: ack of an unknown delivery tag → broker closes the channel with 406; new channel, next order processed) |
| 7 | Delayed copy after success → ACK, no second reservation | `Delayed_copy_arriving_after_success_is_acknowledged_without_a_second_reservation` (published into the 1 s delay tier) |
| 8 | Task-005 topology upgrades; idempotent; delay elements match | `Task_005_topology_upgrades_to_the_retry_topology_without_mismatch` (fresh vhost, 005 topology declared first, full declare twice, management API checks) |
| — | Broker behaviour assumption | `Broker_counts_unsettled_returns_but_not_nack_requeues` |
| Unit | tiers; classification; retry-copy properties | `RetryTopologyTests` (tier per attempt, names, delay-queue arguments, copy properties/headers, policy, options validation), `ProcessingErrorClassifierTests` (permanent vs transient table) |

## Deviations from the task
1. **Handler exceptions are not NACK-requeued** (task: "crash loops ... treat as final failure at the last delivery"). Because RabbitMQ 4
   does not count NACK-requeues, the consumer counts handler failures itself (`x-handler-failures`) and uses the delay tiers; at
   `MaxAttempts` it records the final failure (`handler_error`). The `x-delivery-count` guard is kept for crashes the broker does count.
   The crash-loop requirement (task finalised `Failed`, message in the DLQ) holds for both kinds; both are tested.
2. **Tier names contain the TTL** (`omnichannel.delay.30s`), so a changed tier creates a new exchange/queue pair instead of a mismatch.
   The old pair stays (never auto-deleted) and still returns its messages to the right queue.
3. `IRetryPublisher.PublishDelayedAsync` takes the header set (retry copy vs. handler-failure copy) instead of `(attemptNo, errorCode)`.
4. The final-failure callback returns `false` when the task was already completed (duplicate at the limit) → ACK instead of DLQ.
5. Test fixture: the management port is bound to a fixed host port (the broker-outage test restarts the container, which re-mapped the
   random port and broke later management-API tests).

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- Manual retry from the dashboard (008) must reset a `Failed` task and republish; DLQ re-drive tooling is not built.
- `processing.failed` rows are the alert source for step 11 (017).
- Expected constraint violations still logged at Error by EF (step 9).
- Stock-sync / invoice / email consumers (014, 024) should implement `IQueueMessageHandler` and run through `ResilientQueueConsumer`.

## Manual steps for the user
Manual check (PostgreSQL outage, Workers keep running): with API + Workers running, stop PostgreSQL
(`docker compose -f docker/docker-compose.yml --env-file docker/.env stop postgres`) **right after** submitting an order
(`POST /api/orders/website` answers 202 before the stop), wait ~40 s, then start it again (`... start postgres`). In the Workers log you see
`Cannot start consuming` / `retrying with delay` warnings while the database is down; afterwards the order is processed
(`SELECT "ExternalOrderId", "Status" FROM "Orders" ORDER BY "CreatedAt" DESC LIMIT 1;` → `Reserved`). The outbox row published before the
stop is delivered once the database is back. No new migration.

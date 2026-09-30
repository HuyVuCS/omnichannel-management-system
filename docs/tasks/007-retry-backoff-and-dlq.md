# Task 007 — Delayed retry with backoff, final failure and DLQ handling for every consumer

Status: Open
Written by: Cowork, 2026-09-30
Depends on: 006 (done)

## Context
Doc §8.3 / §13 and CLAUDE.md §10: temporary failures → retry with backoff; after the limit → task `Failed`, message in the DLQ,
attempt history preserved, failure visible. Task 006 left an interim policy (immediate requeue bounded by `x-delivery-limit`)
and two P2 gaps (review 006). This task builds the shared mechanism in `Infrastructure/RabbitMQ/` on top of
`RabbitMqQueueConsumer` and switches the Order Worker to it. Stock-sync, invoice and email consumers (014, 024) will reuse it.

## Design (decided by Cowork, 2026-09-30)

### Delay topology (no RabbitMQ plugin; added to `RabbitMqTopology`, existing queues unchanged)
| Element | Definition |
| --- | --- |
| Exchanges `omnichannel.delay.<tier>` | fanout, durable, one per tier |
| Queues `omnichannel.delay.<tier>` | durable quorum, `x-message-ttl = tier`, `x-dead-letter-exchange = omnichannel.retry`, **no** dead-letter routing key (the original routing key is kept), `x-dead-letter-strategy = at-least-once`, `x-overflow = reject-publish` |
| Exchange `omnichannel.retry` | direct, durable; each main queue bound with routing key = **its own queue name** |

Tiers (defaults): `5s`, `30s`, `2m`, `10m`. A retry is published to `omnichannel.delay.<tier>` with **routing key = the source queue
name**; when the TTL expires the message is dead-lettered to `omnichannel.retry` and lands back in exactly that queue.
One TTL per queue avoids head-of-line blocking of mixed delays. Tier TTLs are part of the topology (a changed TTL is a mismatch →
host stops, as for other queues); tests may use shorter tiers on a fresh broker/vhost.

### Handler contract
Handlers return one of:
- `Ack` — done (success, duplicate, business outcome).
- `Retry(errorCode, errorMessage)` — transient; the plumbing decides between delayed retry and final failure.
- `Fail(errorCode, errorMessage)` — permanent; no retry.
- `Reject(reason)` — poison (unparseable/unknown task); straight to the DLQ as today.

Plumbing (`RabbitMqQueueConsumer` + a small `RetryPublisher`):
- `Retry` with `attemptNo < MaxAttempts` → publish a copy (same body and properties, `MessageId`/`CorrelationId` unchanged, headers
  `x-retry-attempt`, `x-last-error-code`) to the tier for `attemptNo` (1 → 5s, 2 → 30s, 3 → 2m, ≥ 4 → 10m) **with publisher
  confirm**, then ACK the original. Publish failure → `Nack(requeue: true)` (fallback) and log.
- `Retry` at the limit, or `Fail` → the handler's final-failure callback runs first (see below), then `Reject(requeue: false)` → `<queue>.dlq`.
- `MaxAttempts` per consumer (default 5), `AttemptNo` taken from the **database** (the task's attempts), not from headers.
- Last-chance guard: if `x-delivery-count` shows this is the last delivery allowed by `x-delivery-limit` (crash loops, handler never
  returned), treat it as a final failure before processing.
- Consumer channel closed (not the connection) → log, re-create the channel and consumer with backoff; test it.

### Error classification (`ProcessingErrorClassifier` → decides `Retry` vs `Fail`)
- Transient → `Retry`: deadlock, serialization failure, database unavailable, timeouts, broker errors, and `unexpected`
  (unknown exceptions are retried but only up to `MaxAttempts`).
- Permanent → `Fail`: PostgreSQL class `22` (data exception), `23514` (check violation), `23502` (not null), `23503` (FK) — the
  input or state is wrong and retrying cannot help. (`23505` unique violations stay handled explicitly where expected.)

### Order Worker final failure
`OrderReservationService` gets `RecordFinalFailureAsync(taskId, attemptId, errorCode, errorMessage)`: attempt `Failed`, task
`Failed`, and `SystemEventLog` Error `processing.failed` `{taskId, taskType, attemptNo, errorCode, errorMessage, queue}` with the
envelope's correlation id — in one transaction, **before** the message is rejected to the DLQ. Replace the interim
`Nack(requeue: true)` policy. Keep poison handling (`order.message_rejected`) unchanged.

## Tests (required)
Integration (short tiers, e.g. 1 s / 2 s / 3 s / 4 s, on a fresh broker or vhost):
1. **Transient twice, then success:** attempts #1, #2 `Failed` with error code, #3 `Succeeded`; one order; the gaps between attempts
   are at least the tier delays; queue and delay queues empty afterwards.
2. **Always transient:** after `MaxAttempts` (use 3) → task `Failed`, every attempt `Failed`, one `processing.failed` SystemEventLog row,
   the message in `order.processing.dlq` with the original `MessageId`/`CorrelationId` and the retry headers; no stock reserved.
3. **Permanent error** (inject a `23514`-classified failure) → exactly one attempt, task `Failed`, DLQ, no retry published.
4. **Retry publish fails** (delay exchange missing or broker nack in a test seam) → original nacked/requeued, not lost.
5. **Crash loop:** handler throws out of the callback every time → at the last allowed delivery the task is finalised `Failed` and
   the message is in the DLQ (not left `RetryScheduled`).
6. **Channel closed** (e.g. force-close the consumer channel via the management API or a channel-level error) → consumer is
   re-created and processes the next message.
7. **Duplicate after retry:** a delayed copy arriving after the task already succeeded → ACK, no second reservation.
8. **Topology upgrade:** a broker with only the task-005 topology upgrades without mismatch; declaring twice is idempotent;
   the delay queues/exchanges/bindings match the table above.
Unit: tier selection per attempt; classification table (transient vs permanent); retry-copy properties/headers.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (unit/integration counts); retry tests stable over 3 runs.
- [ ] Mutation checks, each reverted: always ACK instead of publishing the retry copy → test 1 fails; skip the final-failure callback →
      test 2 fails; classify `23514` as transient → test 3 fails.
- [ ] No infinite retry anywhere; every final failure is visible as task `Failed` + `processing.failed` + message in the DLQ.
- [ ] Manual check documented: stop PostgreSQL briefly while submitting an order → attempts fail and retry with delay; start it →
      the order completes. (Workers keep running.)

## Out of scope
Manual retry from the dashboard (`POST /api/processing/tasks/{id}/retry`, task 008), DLQ re-drive tooling, stock-sync/invoice/email
consumers (014, 024), alert entity (017).

# Review 007 — Delayed retry, final failure, DLQ

Reviewer: Cowork, 2026-09-30
Verdict: **Accepted**

## Checked
- Report, commit `e124527`; settlement table, delay topology, `RabbitMqQueueConsumer` requeue paths; mutation results.
- Delay topology as designed (fanout per tier → quorum queue with fixed TTL → `omnichannel.retry` → source queue by original
  routing key); retry copy keeps `MessageId`/`CorrelationId`/body, published with confirm before ACK; attempt numbers from the database.
- Classification: permanent PostgreSQL errors fail at once; transient and unknown errors retried up to `MaxAttempts`.
- Final failure: task `Failed` + attempt `Failed` + `processing.failed` event in one transaction, then DLQ; duplicate at the limit → ACK.
- Closed consumer channel re-created (`ResilientQueueConsumer`).
- 306/306 tests (216 unit, 90 integration), 3 runs green; mutation checks fail as required.
- **Manual check confirmed by the user** (table lock → attempt #1 `timeout`, delayed retry, attempt #2 `Succeeded`, order `Reserved`).

## Important finding by the implementation
RabbitMQ 4 does **not** count `basic.nack(requeue: true)` toward `x-delivery-limit`. Task 006's interim policy (and the task-006 spec
written by Cowork) assumed it did; handler exceptions could loop ~16,000 times per minute. Now fixed and documented by
`Broker_counts_unsettled_returns_but_not_nack_requeues`. Recorded in the implementation log as a platform fact.

## Deviations — accepted
1. Handler exceptions counted by the consumer (`x-handler-failures`) and retried through the tiers; delivery-count guard kept for real crashes.
2. Tier names contain the TTL (`omnichannel.delay.30s`) — changing a tier creates a new pair instead of a mismatch. Better than the task.
3–5. Header set parameter, `false` from the final-failure callback → ACK, fixed management port in tests.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | The one remaining `Nack(requeue: true)` path (retry copy cannot be published) has the same property: not counted by the broker. If the broker is up but the copy is persistently unroutable (e.g. a delay exchange deleted by an operator), the message spins without delay. | Task 008 Part A: wait a bounded backoff before that requeue and log at most once per interval |
| P3 | DLQ messages are never re-driven; manual retry (008) republishes from the outbox, so DLQ copies accumulate. | Document; purge/inspection tooling later |

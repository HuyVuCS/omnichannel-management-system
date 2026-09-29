# Review 005 — RabbitMQ topology, envelope, Outbox Publisher

Reviewer: Cowork, 2026-09-30
Verdict: **Accepted with one P1 follow-up** (fixed in task 006 Part A)

## Checked
- Report, commits `707802b` (Part A) and `30fe05f`; read in full: `OutboxPublisherService` (batch, publish, failure handling),
  `RabbitMqTopologyInitializer`, `WorkerHostSetup`; topology table and both migration scripts in the report.
- Topology matches the plan exactly (quorum, DLX `omnichannel.dlx`, at-least-once dead-lettering, reject-publish, delivery limit 10);
  verified through the management API in tests, mismatch stops the host.
- Publishing: `FOR UPDATE SKIP LOCKED` batch; publisher confirms with tracking + `mandatory`; rows `Published` only after confirm;
  batch finished with `CancellationToken.None` once messages are out; shutdown mid-publish rolls back (at-least-once, documented).
  Both key properties are proven by mutation checks (confirm tracking off → unroutable counted as published; no SKIP LOCKED → duplicates).
- Password never reaches `LastError`/logs. Pending index choice (`CreatedAt` key, `INCLUDE (NextAttemptAt)`) is well argued.
- Part A: request hash → 409 `order_conflict`; reordered items still a duplicate.
- 224/224 tests (153 unit, 71 integration), stable over 3 runs.

## Deviations — accepted
All six. Deviation 1 (unreachable broker warns, mismatch stops) is the right reading of two task requirements.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| **P1** | Broker/connection failures count toward `MaxAttempts`. With backoff 1 s … 256 s, a RabbitMQ outage of about 8–9 minutes turns **every** pending row into `Failed` permanently (no automatic recovery), although the messages themselves are fine. Contradicts doc §13 ("Outbox publish fail → stays Pending; publisher retries"). The outage test passes only because its outage is short. | Task 006 Part A: only message-specific failures (returned/unroutable, nack) count; connection/channel errors and confirm timeouts back off the loop without touching the rows |
| P3 | A row with invalid JSON payload blocks its whole batch (retried forever). Only `MessageJson` writes payloads today. | Accept; revisit if other writers appear |
| P3 | Same-aggregate messages may arrive out of order. | Consumers must not rely on order (006, 013) |

## Manual acceptance check
**Confirmed by the user (2026-09-30):** an order submitted with `X-Correlation-Id: manual-check-78807` arrived in `order.processing`
via `omnichannel.topic` / `order.created`, with `message_id` = outbox id, `correlation_id`, `type = order.created`, `delivery_mode = 2`,
`content_type = application/json` and the envelope body. The extra header `x-dotnet-pub-seq-no` is added by RabbitMQ.Client for
confirmation tracking — harmless; consumers must not depend on it.

## Untracked file noticed
`backend/src/Omnichannel.UserSeeder/` (empty "Hello, World!" console project, not in the solution) — deleted with the user's approval;
user management is planned as task 009.

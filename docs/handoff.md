# Handoff — Omnichannel Order & Inventory Management System

Snapshot: 2026-09-30, branch `master` (nothing pushed). Latest commit: `e5d67c8` (docs: review 007, task 008).

## Status
- Tasks **001–007**: implemented, reported and reviewed (`docs/tasks/00N-*.report.md` / `*.review.md`).
- **Open: task 008** — `docs/tasks/008-orders-and-processing-api.md` (not started; the next `/next-task` picks it up).
- Last verified run (task 007): build 0 warnings, **306/306 tests** (216 unit, 90 integration), 3 consecutive full runs green.
  Integration tests need Docker running; `dotnet test --filter "Category!=Integration"` runs unit tests only.
- Uncommitted at the time of writing (not from Claude Code): `docs/tasks/README.md` (modified), `docs/tasks/_setup/` (untracked).

## What exists
**API (`Omnichannel.Api`)**
- Auth: ASP.NET Core Identity + JWT, roles Admin / Manager / Staff, policy-based authorization.
- CRUD: channels, customers, products; `GET /api/inventory` (paging contract, low-stock rule `AvailableStock <= LowStockThreshold`).
- `POST /api/inventory/import` (idempotent per document), `GET /api/inventory/movements`.
- `POST /api/orders/{slug}` → **202**: stores `ProcessingTask` + `order.created` outbox row in one transaction (decision A).
  Idempotency key `order:{channelId:N}:{externalOrderId}` + request hash (different content → 409 `order_conflict`).
  **No RabbitMQ dependency in the API** (asserted by a test).

**Workers (`Omnichannel.Workers`)**
- Topology declared at start-up (quorum queues, DLX/DLQs, retry exchange and delay tiers); mismatch stops the host.
- **Outbox Publisher**: `FOR UPDATE SKIP LOCKED` batches, publisher confirms + mandatory; only message-specific failures count as
  attempts (broker outages never fail rows).
- **Order Worker**: attempt tracking; atomic reservation (conditional `UPDATE ... WHERE "AvailableStock" >= @stock`, items locked in
  `ProductId` order, all or nothing); `InsufficientStock` orders; poison → DLQ.
- **Retry/DLQ (task 007)**: handlers return `Ack` / `Retry` / `Fail` / `Reject`; delayed retry 5 s / 30 s / 2 min / 10 min; final failure
  → task `Failed` + `SystemEventLog` `processing.failed` + message in `<queue>.dlq`; closed consumer channels are re-created.

**Migrations after `InitialCreate`**: `InventoryImportIdempotency`, `ProcessingTaskIdempotencyKey`, `ProcessingTaskRequestHash`,
`OutboxPublishTracking` (task 007 added none).

## Rules to keep in mind
- Every stock change = movement + outbox row in the **same transaction**; code locking several inventory rows sorts by `ProductId`.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** The consumer counts handler failures itself
  (`x-handler-failures`); never rely on NACK-requeue being bounded.
- New consumers: implement `IQueueMessageHandler`, run through `ResilientQueueConsumer`, register the handler as scoped.
- Validation error keys are JSON paths (`items[0].productId`); ProblemDetails carry `errorCode` and `correlationId`.
- Tests: the API collection uses **one** `WebApplicationFactory` per run (Serilog bootstrap logger); the Workers collection has its own
  PostgreSQL + RabbitMQ containers (fixed host ports) and short retry tiers (1–4 s).
- Workflow: `docs/tasks/README.md`; decisions: `docs/implementation-log.md`; plan: `docs/roadmap.md`.

## Open for the user
- Task 007 manual check (stop PostgreSQL briefly after submitting an order → retries with delay → order `Reserved`), if not done yet.
- Commit or finish the pending Cowork changes (`docs/tasks/README.md`, `docs/tasks/_setup/`).

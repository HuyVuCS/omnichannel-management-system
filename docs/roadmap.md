# Roadmap

Plan from the current state to the complete system (backend + frontend), following the implementation order in
the architecture document (section 15). Maintained by Cowork; updated after every task review.

Last updated: 2026-09-30 (after review 004)

## Conventions
- One row = one task file `docs/tasks/NNN-*.md`, written by Cowork shortly before it is needed (not all up front),
  so each task can use what earlier reviews found.
- Follow-ups found in a review go into **Part A of the next task** (as with tasks 003 and 004) instead of new task
  numbers, so this numbering stays stable. A new number is only added for work that is genuinely new.
- Backend and frontend are interleaved so an end-to-end flow can be demonstrated early (after task 012).
- Status: `Done` (reviewed and accepted) · `Review` (report written, awaiting Cowork review) · `Open` (task file written)
  · `Planned` (no task file yet).

## Tasks

| # | Area | Scope | Doc step | Status |
| --- | --- | --- | --- | --- |
| 001 | BE | Build, test, `InitialCreate` migration for steps 1–3 | 1–3 | Done |
| 002 | BE | Channel / Customer / Product CRUD, inventory list, integration-test infrastructure | 4 | Done |
| 003 | BE | Inventory import, movement history, `inventory.changed` outbox events | 4 | Done |
| 004 | BE | Order intake `POST /api/orders/{slug}` (outbox first, decision A) | 5 | Done |
| 005 | BE | RabbitMQ topology (exchange, quorum queues, DLX/DLQs), message envelope, Outbox Publisher with publisher confirms | 6, 8 | Open |
| 006 | BE | Order Worker: consume `order.created`, atomic reserve transaction (lock inventory rows in `ProductId` order, same as import), Order/Items/Movements/Outbox, ProcessingAttempt tracking, ACK after commit, oversell tests | 6, 7 | Planned |
| 007 | BE | Shared retry/backoff + DLQ handling for all consumers, max attempts, poison messages | 8 | Planned |
| 008 | BE | `GET /api/orders`, `GET /api/orders/{id}` (with tasks/attempts), `GET /api/processing/tasks`, `POST .../retry`, SystemEventLog writes | 9 | Planned |
| 009 | FE | Vite + React + TS + Redux Toolkit/RTK Query scaffold; login, token handling, role guard, layout, ProblemDetails handling | 3 | Planned |
| 010 | FE | Products, Customers, Channels pages (search, filter, sort, paging) | 3.2 | Planned |
| 011 | FE | Inventory list, stock import, movement history pages | 3.2 | Planned |
| 012 | FE | Order entry page (Digital Platform / Website / POS components) + orders list/detail | 3.1, 3.2 | Planned |
| 013 | BE | ChannelProduct mapping + Stock Sync Worker (per-channel queues, channel adapters, Polly) | 8 | Planned |
| 014 | BE | SignalR hub, worker → API notifications, `GET /api/dashboard/summary` | 10 | Planned |
| 015 | FE | Dashboard overview + realtime (reconnect → refetch) + Sync & Processing page (tasks, attempts, errors, retry) | 3.2, 12 | Planned |
| 016 | BE | Alerts: low-stock (shared predicate) and processing failures, `GET /api/alerts`, `low_stock.created` event | 11 | Planned |
| 017 | FE | Alerts page + Logs/Audit page | 3.2 | Planned |
| 018 | BE+FE | S3 product images: presigned upload `POST /api/products/{id}/image/presign`, private bucket, UI upload | 12 | Planned |
| 019 | BE | Excel export (ClosedXML) for orders and inventory; background export task + S3 for large files | 12 | Planned |
| 020 | FE | Reports & Excel export page | 3.2 | Planned |
| 021 | BE | Reconciliation Worker (detect drift, create StockSync tasks, never modify the source of truth) | 13 | Planned |
| 022 | BE | `POST /api/webhooks/{slug}`: signature verification, rate limiting, reuse of order intake | 11 (API) | Planned |
| 023 | BE | Invoice Worker + Notification (email) Worker with independent task status | 8.4 | Planned |
| 024 | Ops | Worker Dockerfiles, full docker-compose, CI pipeline (build, unit + integration tests) | 14 | Planned |
| 025 | All | Security and performance hardening review (OWASP, indexes, load test of reserve path) | 14 | Planned |
| 026 | All | End-to-end smoke tests against the acceptance criteria (doc section 16) | 14, 16 | Planned |

Expect roughly 2–4 extra tasks for review follow-ups that are too large for a Part A.

## Decisions still needed

| Needed before | Question | Options to discuss |
| --- | --- | --- |
| 013 | Are marketplace/channel APIs real integrations or simulated? | Simulated adapters behind an interface (default) · real sandbox APIs |
| 014 | How do Workers (separate process) trigger SignalR pushes in the API? | RabbitMQ notification queue consumed by the API · PostgreSQL `LISTEN/NOTIFY` |
| 016 | Where are alerts stored? The ERD has no ALERT table. | New `Alert` table (open/acknowledged) · `SystemEventLog` only |
| 018 | S3 in development | Real AWS bucket with dev credentials · local S3-compatible emulator |
| 023 | Where does "Payment Succeeded" come from? No Payment entity exists. | Out of scope · simulated payment step · payment field on Order |

## Acceptance criteria coverage (doc section 16)

| Criterion | Task(s) |
| --- | --- |
| Orders from Website / POS / Digital Platform components | 004, 012 |
| Orders normalised and put on RabbitMQ | 004, 005 |
| Duplicate orders not processed twice | 004, 006 |
| Concurrent orders for the same SKU do not oversell | 006 |
| Total/Reserved/Available stock correct | 003, 006 |
| InventoryMovement + OutboxEvent in the same transaction | 003, 006 |
| Stock sync with retry and DLQ | 007, 013 |
| Near-real-time dashboard | 014, 015 |
| Trace an order to task, worker, instance, attempt, error | 006, 008, 015 |
| Low-stock alert by threshold | 016, 017 |
| Inventory and processing history | 003, 008, 011, 015 |
| Excel export | 019, 020 |
| Product images in S3 | 018 |
| API authentication/authorization | 001–004 (ongoing) |
| Tests for important flows | every task; 026 end to end |

# Roadmap

Plan from the current state to the complete system (backend + frontend), following the implementation order in
the architecture document (section 15). Maintained by Cowork; updated after every task review.

Last updated: 2026-10-03 (after review 016b)

## Conventions
- One row = one task file `docs/tasks/NNN-*.md`, written by Cowork shortly before it is needed (not all up front),
  so each task can use what earlier reviews found.
- Follow-ups found in a review go into **Part A of the next task** (as with tasks 003 and 004) instead of new task
  numbers, so this numbering stays stable. A new number is only added for work that is genuinely new.
- A planned task that turns out too large is split with a letter suffix (`016` → `016`, `016b`), so later numbers stay
  stable (user, 2026-10-02). `/next-task` orders `016b` after `016` and before `017`.
- Backend and frontend are interleaved so an end-to-end flow can be demonstrated early (after task 013).
- Status: `Done` (reviewed and accepted) · `Review` (report written, awaiting Cowork review) · `Open` (task file written)
  · `Planned` (no task file yet).

## Tasks

| # | Area | Scope | Doc step | Status |
| --- | --- | --- | --- | --- |
| 001 | BE | Build, test, `InitialCreate` migration for steps 1–3 | 1–3 | Done |
| 002 | BE | Channel / Customer / Product CRUD, inventory list, integration-test infrastructure | 4 | Done |
| 003 | BE | Inventory import, movement history, `inventory.changed` outbox events | 4 | Done |
| 004 | BE | Order intake `POST /api/orders/{slug}` (outbox first, decision A) | 5 | Done |
| 005 | BE | RabbitMQ topology (exchange, quorum queues, DLX/DLQs), message envelope, Outbox Publisher with publisher confirms | 6, 8 | Done |
| 006 | BE | Order Worker: consume `order.created`, atomic reserve transaction (lock inventory rows in `ProductId` order, same as import), Order/Items/Movements/Outbox, ProcessingAttempt tracking, ACK after commit, oversell tests | 6, 7 | Done |
| 007 | BE | Shared retry/backoff + DLQ handling for all consumers, max attempts, poison messages | 8 | Done |
| 008 | BE | `GET /api/orders`, `GET /api/orders/{id}` (with tasks/attempts), `GET /api/processing/tasks`, `POST .../retry`, SystemEventLog writes | 9 | Done |
| 009 | BE | User management (Admin only): list/create users, assign role, lock/unlock, reset password; no ad-hoc seeder tools | 6 | Done |
| 010 | FE | Vite + React + TS + Redux Toolkit/RTK Query + Ant Design scaffold; login (sessionStorage), role guards, layout, bilingual vi/en, ProblemDetails handling, **Users page**; BE: lockout fix + request localisation | 3 | Done |
| 011 | FE | Products, Customers, Channels pages; Part A: AntD 6, localised custom validation messages, `isLowStock` on products, code splitting | 3.2 | Done |
| 012 | FE | Inventory list, stock import, movement history pages (+ `createdByEmail` on movements); Part A: camelCase error keys, localised field names, route error page, dialogs closed on logout, search box follows the address | 3.2 | Done |
| 013 | FE | Order entry page (Digital Platform / Website / POS panels, reusing `ProductSelect`; outcome tracked by polling the processing task) + orders list/detail; order links in the movement history; Part A: picker tooltips, deterministic expiry test, main-chunk growth | 3.1, 3.2 | Done |
| 014 | BE | ChannelProduct mapping API + Stock Sync Worker (per-channel queues, **simulated** channel adapters, Polly timeout/circuit breaker, directed `stock.sync.requested` for initial sync and manual retry); Part A: expected 23505 conflicts not logged as errors by EF Core, stale list error in the stock-import form | 8 | Done |
| 015 | BE | Realtime hints: `pg_notify` in the committing transaction (API and Workers) → per-instance `LISTEN` in the API → SignalR hub `/api/hubs/notifications` (JWT, AnyStaff, resync after a lost listener); `GET /api/dashboard/summary`; Part A (review 014): stale stock-sync task after deactivation (P2), `GET` of one mapping, README section 6, `.http` samples | 10 | Done |
| 016 | FE | Realtime client (`@microsoft/signalr`, lazy; connect/stop with the session, reconnect → resync, event → tag map, coalesced refetch, connection indicator) + dashboard overview (`GET /api/dashboard/summary`, range in the address) + Sync & Processing page (task list with filters incl. channel, task detail route, retry for Manager/Admin, also on the order detail) + order-entry outcome by push (polling only while disconnected); BE: `productSku` on task summary/detail; Vite proxy with WebSocket; Part A (review 013): pickers hide only stale answers (`currentData`), panel title repetition, orders table width at 1280 px, load only the active language; Part A (review 014): committed bundle-measurement script; Part A (review 015): smoke scripts start hosts only for the local default | 3.2, 12 | Done |
| 016b | FE | Channel detail route `/channels/:channelId` with its product mappings (list, search, sort, paging; create, edit, activate/deactivate for Manager/Admin; initial-sync task with live status); channels without stock sync shown as such (review 014); BE: `stockSyncEnabled` on channel responses; Part A (review 016): `127.0.0.1` for local connections, hint flush as a throttle, order outcome invalidates lists only when not live; fix of a pre-existing form-dialog defect (stale values after a full close) | 3.2 | Done |
| 017 | BE | Alerts: low-stock (shared predicate) and processing failures, `GET /api/alerts`, `low_stock.created` event; Part A (review 016b): frontend suite stable under load (smoke runs npm after stopping its hosts, worker count, split slow tests), strict `Assert-Step` | 11 | Planned |
| 018 | FE | Alerts page + Logs/Audit page; Part A (review 016b): `currentData` on detail pages bound to a route parameter, stale API errors on optional customer fields, products table at 1280 px | 3.2 | Planned |
| 019 | BE+FE | S3 product images: presigned upload `POST /api/products/{id}/image/presign`, private bucket, UI upload | 12 | Planned |
| 020 | BE | Excel export (ClosedXML) for orders and inventory; background export task + S3 for large files | 12 | Planned |
| 021 | FE | Reports & Excel export page | 3.2 | Planned |
| 022 | BE | Reconciliation Worker (detect drift, create StockSync tasks, never modify the source of truth); needs readable channel-side stock in the simulator (decide its state) | 13 | Planned |
| 023 | BE | `POST /api/webhooks/{slug}`: signature verification, rate limiting, reuse of order intake | 11 (API) | Planned |
| 024 | BE | Invoice Worker + Notification (email) Worker with independent task status | 8.4 | Planned |
| 025 | Ops | Worker Dockerfiles, full docker-compose, CI pipeline (build, unit + integration tests); a multi-instance check of the stock sync; reverse proxy passes WebSocket upgrades for `/api/hubs/*` (review 016) | 14 | Planned |
| 026 | All | Security and performance hardening review (OWASP, CSP/security headers, revisit httpOnly-cookie auth, login throttling per account+IP, indexes, load test of reserve path, `createdByEmail` lookup index, dashboard-summary indexes (review 015: outbox failed, non-terminal tasks, last stock-sync success per channel), token-in-URL logging at the reverse proxy, accessibility pass) | 14 | Planned |
| 027 | All | End-to-end smoke tests against the acceptance criteria (doc section 16) | 14, 16 | Planned |

Expect roughly 2–4 extra tasks for review follow-ups that are too large for a Part A.

## Decisions still needed

| Needed before | Question | Options to discuss |
| --- | --- | --- |
| ~~014~~ | ~~Are marketplace/channel APIs real integrations or simulated?~~ | **Decided 2026-10-02 (user): simulated adapters** behind `IChannelStockClient`, in-process in the Workers host; see implementation log "Task 014 plan" |
| ~~015~~ | ~~How do Workers (separate process) trigger SignalR pushes in the API?~~ | **Decided 2026-10-02 (user): PostgreSQL `LISTEN/NOTIFY`**; see implementation log "Task 015 plan" |
| 017 | Where are alerts stored? The ERD has no ALERT table. | New `Alert` table (open/acknowledged) · `SystemEventLog` only |
| 019 | S3 in development | Real AWS bucket with dev credentials · local S3-compatible emulator |
| 024 | Where does "Payment Succeeded" come from? No Payment entity exists. | Out of scope · simulated payment step · payment field on Order |

## Acceptance criteria coverage (doc section 16)

| Criterion | Task(s) |
| --- | --- |
| Orders from Website / POS / Digital Platform components | 004, 013 |
| Orders normalised and put on RabbitMQ | 004, 005 |
| Duplicate orders not processed twice | 004, 006 |
| Concurrent orders for the same SKU do not oversell | 006 |
| Total/Reserved/Available stock correct | 003, 006 |
| InventoryMovement + OutboxEvent in the same transaction | 003, 006 |
| Stock sync with retry and DLQ | 007, 014, 015 (A1) |
| Near-real-time dashboard | 015, 016 |
| Trace an order to task, worker, instance, attempt, error | 006, 008, 016 |
| Low-stock alert by threshold | 017, 018 |
| Inventory and processing history | 003, 008, 012, 016 |
| Excel export | 020, 021 |
| Product images in S3 | 019 |
| API authentication/authorization | 001–004 (ongoing) |
| Tests for important flows | every task; 027 end to end |

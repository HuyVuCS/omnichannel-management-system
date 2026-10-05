# Implementation log

Decisions taken while implementing the architecture document, in order of the implementation plan (section 15).
Anything listed under "Deviations" differs from, or adds to, the architecture document and should be confirmed.

## Working model

- Planning and review: Claude Cowork. Implementation, build, test: Claude Code (desktop Code tab or VS Code).
- Handoff through `docs/tasks/` (see `docs/tasks/README.md`): Cowork writes `NNN-*.md`, Claude Code runs `/next-task`
  and writes `NNN-*.report.md`. Only results in a report count as verified.
- `.claude/settings.json` pre-approves read-only/build commands and denies reading `.env` files.
- **Development steps are applied by Claude Code (user, 2026-10-05).** After a task is verified, Claude Code applies
  what the development environment needs — the key helper, `dotnet ef database update` as the migrator, `docker compose
  up -d` of changed services — and runs the smoke script there; the report lists them under "Development steps
  (applied)". Guardrails in `docs/tasks/README.md` (development only, additive migrations only, never print secrets,
  stop on anything destructive). The migrator connection comes from the user-level environment variable
  `OMNICHANNEL_MIGRATOR_CONNECTION` (set once by the user). Trade-off accepted by the user: the least-privilege
  boundary of steps 1–3 (Claude Code without DDL rights) is given up for the development database only.

## Steps 1–3 — Solution, Docker, data model, Auth/RBAC (2026-09-25)

### Decisions

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Target framework | .NET 10 (LTS) | .NET 8 support ends Nov 2026 | — |
| Solution format | `backend/Omnichannel.slnx` + Central Package Management | Single place for package versions | — |
| Primary keys | `Guid` (UUIDv7 via `Guid.CreateVersion7()`), generated in the app | IDs known before insert (outbox/tasks), index locality of v7 | — |
| `SystemEventLog.LogId` | `bigint GENERATED ALWAYS AS IDENTITY` | High-volume append-only table | — |
| Naming | Tables plural PascalCase, columns = C# property names (quoted identifiers) | Keeps names 1:1 with the data dictionary; no extra package | Raw SQL becomes painful |
| Enums | Stored as `varchar(32)` strings | Readable, safe against enum reordering | — |
| `Inventory.AvailableStock` | PostgreSQL `GENERATED ALWAYS AS ("TotalStock" - "ReservedStock") STORED` | Invariant enforced by DB, also for set-based UPDATEs | — |
| Oversell guard | CHECK `TotalStock >= 0` and `0 <= ReservedStock <= TotalStock` | Last line of defence independent of app code | — |
| Inventory concurrency token | `xmin` (`uint RowVersion`) | Optimistic concurrency for tracked updates | — |
| Idempotency constraints | `UX_Orders_ChannelId_ExternalOrderId`; unique `(TaskId, AttemptNo)`; unique `(OrderId, ProductId)` on items; partial unique on movements `(ReferenceType, ReferenceId, ProductId, Type)` for Reserve/Release | Duplicate webhook / redelivery cannot double-apply | — |
| Audit fields | `SaveChangesInterceptor` sets CreatedAt/By, UpdatedAt/By from `ICurrentUser` | One place; controllers/services never set them | — |
| DB roles | `omnichannel_migrator` (DDL) and `omnichannel_app` (DML only) | Least privilege (doc section 6) | — |
| Migrations | Run by `dotnet ef database update` with the migrator role, never on API start-up | App role has no DDL rights; avoids concurrent migrators | CI/CD: use a migrations bundle |
| JWT | HS256, 60 min, claims `sub`, `email`, `jti`, `role`; inbound claim mapping off | Simple, no external IdP required | Refresh tokens / revocation needed |
| Default authorization | Fallback policy = authenticated user | Secure by default; public endpoints must opt out | — |
| Login hardening | Identity lockout (5 tries / 15 min), generic 401, dummy hash for unknown users, rate limit 10/min/IP | Brute force & account enumeration | Behind a proxy: configure ForwardedHeaders |
| Errors | `IExceptionHandler` → ProblemDetails with `errorCode`, `correlationId` | Consistent contract for the frontend | — |
| Validation | FluentValidation via global action filter | Validators in Application, controllers stay thin | — |
| Logging | Serilog; text in Development, compact JSON otherwise; `CorrelationId` in LogContext | Structured logs | Add sink (e.g. CloudWatch) at deployment |

### Deviations / additions to the architecture document

1. `Channel.Slug` (unique) added — required by `POST /api/orders/{slug}` but missing from the ERD. Seeded: `digital-platform`, `website`, `pos`.
2. `OutboxEvent.CorrelationId` and `OutboxEvent.CreatedAt/UpdatedAt` added — needed to build the message envelope and to publish in order.
3. `OutboxEvent.InventoryMovementId` is nullable — events such as `invoice.requested` have no movement.
4. `ProcessingTask.OrderId` is nullable — reconciliation stock-sync tasks are not tied to an order.
5. `ChannelProduct` has audit fields (the ERD lists none).
6. `GET /api/auth/me` added — frontend session restore; returns roles from the store, not only from the token.
7. `GET /health` added (anonymous) — container health checks.

### Open points for the next steps

- ~~Flow 8.1 vs. reliability (step 5)~~ — decided 2026-09-29, option A (see "Steps 4–5").
- **Reservation SQL (step 7):** use one conditional `UPDATE ... SET "ReservedStock" = "ReservedStock" + @qty WHERE "ProductId" = @id AND "AvailableStock" >= @qty` per item inside the transaction; raw SQL bypasses the audit interceptor, so set `UpdatedAt/UpdatedBy` explicitly. Do not enable `EnableRetryOnFailure` without wrapping the transaction in the execution strategy.
- ~~**StockSync per channel (step 8):** `ProcessingTask` has no `ChannelId` yet; add it when stock-sync tasks are introduced.~~ — planned in 014 (deviation 13).
- **Integration tests:** concurrency/oversell and idempotency tests need a real PostgreSQL (Testcontainers or the compose database). Not added yet.
- **User management:** only the bootstrap admin exists; endpoints to create Manager/Staff users are not in the API contract yet.

## Task 001 result (2026-09-25, reviewed 2026-09-29)

Build 0 errors/0 warnings, 39/39 tests, `InitialCreate` generated and applied by the user. Packages bumped to
Microsoft 10.0.12 / Npgsql 10.0.3. See `docs/tasks/001-*.report.md` and `001-*.review.md`.

## Steps 4–5 — Catalog, inventory import, order intake (planned 2026-09-29, tasks 002–004)

### Decisions

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Order intake (flow 8.1) | **Option A:** API writes `ProcessingTask(OrderProcessing)` + `OutboxEvent(order.created)` in one transaction and returns 202; publisher → RabbitMQ; Order Worker creates the Order in the reserve transaction | No lost orders when the publish fails; API independent of RabbitMQ; flow 8.2 unchanged | — |
| Intake idempotency | `ProcessingTask.IdempotencyKey` = `order:{channelId:N}:{externalOrderId}`, unique filtered index | Duplicate submissions produce one task before any order exists | — |
| Rejected external order id | Cannot be resubmitted under the same id | Same input → same result | Business asks for "retry after restock" |
| Service placement | Contracts, validators, service interfaces in `Application/<Feature>`; EF implementations in `Infrastructure/Persistence/<Feature>`; entity methods hold domain rules | Follows the existing `IAuthService` convention; raw SQL stays out of Application | Services grow business logic that needs DB-free unit tests |
| Stock mutation | Only via single-statement `UPDATE ... SET x = x + @n ... RETURNING`, plus movement + outbox row in the same transaction; items locked in `ProductId` order | Atomic, no lost updates, no deadlocks | — |
| Import idempotency | `UX_InventoryMovements_Reference` covers `Import`, `Reserve`, `Release` | Same goods-receipt document cannot be applied twice | — |
| Uniqueness errors | Map PostgreSQL 23505 + constraint name to `ConflictException` (409) with stable `errorCode` | Race-safe; no check-then-insert | — |
| Paging contract | `page` ≥ 1, `pageSize` 1–100 (default 20), `search`, whitelisted `sortBy`, `sortDir`; response `{items, page, pageSize, totalCount}` | One contract for the frontend | — |
| Integration tests | Testcontainers PostgreSQL 17 + one shared `WebApplicationFactory`; `Category=Integration` trait | Concurrency/oversell/idempotency need real PostgreSQL | CI without Docker |
| Message contracts | `Application/Common/Messaging` (`EventTypes`, payload records, `MessageJson`) shared by API and Workers | Single source for envelope payloads | — |

### Deviations / additions to the architecture document
8. `ProcessingTask.IdempotencyKey` (nullable, unique) — intake idempotency before an order exists.
9. `POST /api/orders/{slug}` returns **202** with `taskId` (asynchronous processing), not the order.
10. Extra endpoints for the order-entry UI and catalog maintenance: `GET/POST/PUT /api/channels`, `GET/POST/PUT /api/customers`,
    `GET /api/products/{id}`, `PUT /api/products/{id}`.

## Task 002 result (reviewed 2026-09-29)

Catalog CRUD + `GET /api/inventory` + Testcontainers integration tests; 114/114 tests. Review: `docs/tasks/002-*.review.md`.

| Topic | Decision | Reason |
| --- | --- | --- |
| Low-stock rule | **`AvailableStock <= LowStockThreshold`**, inclusive, no special case (threshold 0 ⇒ warn when out of stock). Supersedes the task-002 rule (`threshold > 0 AND ...`), changed 2026-09-29 in task 003 | Doc §16: "low-stock alert works by threshold"; AvailableStock is what can still be sold; an out-of-stock product must never be hidden. One shared predicate for list, filter and step 11 alerts |
| Infrastructure namespace | `Persistence/Inventories` (not `Inventory`) | Avoids shadowing the `Inventory` entity |
| SKU format | Upper-cased, `^[A-Z0-9][A-Z0-9._-]*$`, ≤ 64 (task 003) | SKUs go to channel APIs and exports |
| Paging bound | `page` ≤ 10,000 (task 003) | Prevents `int` overflow in `Skip` |

## Task 003 result (reviewed 2026-09-29)

Inventory import + movements + `inventory.changed` outbox; migration `InventoryImportIdempotency`; 165/165 tests.
Review: `docs/tasks/003-*.review.md`.

| Topic | Decision | Reason |
| --- | --- | --- |
| Correlation id access | `ICorrelationContext` (Application abstraction); API = request `X-Correlation-Id`, Workers = per scope, set from the envelope | Services need it without controllers threading it through |
| Inventory lock order | Every code path locking several `Inventories` rows sorts by `ProductId` (.NET `Guid` order = PostgreSQL `uuid` order) | Prevents deadlocks between import and reservation |
| Validation error keys | camelCase JSON paths (`items[0].productId`) — task 004 Part A | Frontend maps errors to form fields |
| Stock overflow | PostgreSQL 22003 on stock update → 409 `stock_limit_exceeded` (task 004 Part A) | No 500 for a business limit |

## Task 004 result (reviewed 2026-09-30)

Order intake (decision A), migration `ProcessingTaskIdempotencyKey`, camelCase validation keys, stock overflow → 409;
193/193 tests. Review: `docs/tasks/004-*.review.md`.

| Topic | Decision | Reason |
| --- | --- | --- |
| Resubmission with different content | **Option 1 (user, 2026-09-30):** `ProcessingTask.RequestHash` = SHA-256 of customerId + items sorted by productId (`productId:stock`); same key + same hash → 202 duplicate; different hash → 409 `order_conflict` (task 005 Part A) | Channel edits or client bugs must be visible, not silently dropped |

## Task 005 plan — RabbitMQ and Outbox Publisher (2026-09-30)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Client | `RabbitMQ.Client` 7.x (async API) | Doc stack; current major | — |
| Queue type | Durable quorum queues; DLX `omnichannel.dlx` (direct) → `<queue>.dlq`; at-least-once dead-lettering; `x-delivery-limit = 10` | Data safety on RabbitMQ 4; poison messages cannot loop forever | — |
| Topology owner | Declared idempotently by the Workers host at start-up; mismatch stops the host | Single definition in code; never auto-delete queues | Ops-managed definitions in production |
| Publishing | Outbox Publisher in Workers; `FOR UPDATE SKIP LOCKED` batches; publisher confirms + mandatory; mark Published only after confirm | Several instances safe; no silent loss | — |
| Delivery guarantee | At-least-once; consumers deduplicate by `messageId` (= OutboxEventId) | Crash between confirm and commit republishes | — |
| Publish failures | `PublishAttempts`/`NextAttemptAt`/`LastError` on OutboxEvent, exponential backoff with jitter, `Failed` + SystemEventLog after 10 | No infinite retry; failures visible | — |

## Task 005 result (reviewed 2026-09-30)

RabbitMQ topology + Outbox Publisher + request hash (409 `order_conflict`); migrations `ProcessingTaskRequestHash`,
`OutboxPublishTracking`; 224/224 tests. Review: `docs/tasks/005-*.review.md`.

| Topic | Decision | Reason |
| --- | --- | --- |
| Outbox attempt counting | Only message-specific failures (returned/unroutable, nack) count toward `MaxAttempts`; connection/channel errors and confirm timeouts back off the loop and leave rows untouched (task 006 Part A) | A broker outage must never turn healthy messages into `Failed` (doc §13) |
| Broker unreachable at start-up | Workers host starts and warns; topology declared on first successful connection; mismatch stops the host | Both task requirements hold |

## Task 006 plan — Order Worker (2026-09-30)

| Topic | Decision | Reason |
| --- | --- | --- |
| Reservation | One transaction; per item `UPDATE ... SET ReservedStock = ReservedStock + n WHERE ProductId = @p AND AvailableStock >= n RETURNING ...`, items in `ProductId` order; all or nothing | Atomic check-and-reserve, no oversell, no deadlock |
| Insufficient stock | Order stored with `Status = InsufficientStock`, **task `Succeeded`**, `SystemEventLog` Warning `order.insufficient_stock` (alert source for step 11) | Deterministic business outcome, not a technical failure; `Failed` tasks mean operator action |
| Attempts | Attempt row committed before the business transaction; stale `Running` attempts marked `Failed/abandoned` on redelivery | Attempts survive crashes; history is exact |
| Duplicate delivery | Terminal task status → ACK without work; `UX_Orders_ChannelId_ExternalOrderId` as final guard | At-least-once delivery from the publisher |
| Transient failures (interim) | Attempt `Failed` + task `RetryScheduled` + nack requeue, bounded by `x-delivery-limit`; replaced by delayed retry in 007 | Keep 006 focused on reservation correctness |

## Roadmap change (2026-09-30)
User management (Admin: create users, roles, lock/unlock, reset password) added as task 009; later planned tasks renumbered.
Only the bootstrap admin existed. An untracked, empty `Omnichannel.UserSeeder` project was deleted with the user's approval.
Task 005 manual acceptance check confirmed by the user (message with envelope in `order.processing`).

## Task 006 result (reviewed 2026-09-30)

Order Worker with atomic reservation, attempt tracking, poison handling; broker outages no longer fail outbox rows;
263/263 tests; manual check confirmed by the user. Review: `docs/tasks/006-*.review.md`.
The DB CHECK on `ReservedStock` is the last line of defence; the conditional `UPDATE` is the working availability check (proven by mutation).

## Task 007 plan — retry, backoff, DLQ (2026-09-30)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Delayed retry | Per-tier delay queues (`omnichannel.delay.<tier>`, quorum, fixed `x-message-ttl`) behind fanout exchanges; expired messages dead-letter to `omnichannel.retry` (direct) and return to the source queue by their original routing key (= queue name). Tiers 5 s / 30 s / 2 m / 10 m | No plugin; one TTL per queue avoids head-of-line blocking; reusable by every consumer | Need per-message arbitrary delays |
| Retry decision | Handler returns `Ack` / `Retry` / `Fail` / `Reject`; attempt number from the database; `MaxAttempts` default 5; retry copy published with confirm before ACK | Database is the source of truth for attempts; no message loss |
| Classification | Transient (deadlock, serialization, DB unavailable, timeouts, broker, unknown) → retry; PostgreSQL `22xxx`, `23514`, `23502`, `23503` → permanent failure | Bugs/bad data must not burn retries silently |
| Final failure | Task `Failed` + attempt `Failed` + `SystemEventLog` `processing.failed`, then reject to `<queue>.dlq`; `x-delivery-count` last-chance guard for crash loops | Every failure visible, nothing left `RetryScheduled` |
| Consumer resilience | Closed consumer channel is re-created with backoff | Review 006 P2 |

## Task 007 result (reviewed 2026-09-30)

Delayed retry (tiers `omnichannel.delay.5s/30s/2m/10m`), final failure → task `Failed` + `processing.failed` + DLQ, consumer channel
re-creation; 306/306 tests; manual check confirmed by the user. Review: `docs/tasks/007-*.review.md`.

**Platform fact:** RabbitMQ 4 does not count `basic.nack(requeue: true)` toward `x-delivery-limit` (only unsettled returns on channel
close). Never rely on NACK-requeue being bounded; count failures in the consumer and use the delay tiers.

## Task 008 plan — orders/processing API, manual retry (2026-09-30)

| Topic | Decision | Reason |
| --- | --- | --- |
| Manual retry | Only `Failed` tasks; new outbox row copying the original message (original routing key), task `Pending`, audit event; DLQ copy left for inspection | Reuses the reliable outbox path; doc's `processing.retry` routing key stays unused (deviation) |
| Attempt budget | `ProcessingTask.AttemptBudgetStart`; limit checks `AttemptNo - AttemptBudgetStart`; attempt numbers keep increasing | Fresh retries after a manual retry, complete attempt history |
| Task search | Attempt-level filters (worker, instance, error code, attempt no) via `EXISTS` | Doc §5.3 |
| Authorization | Reads AnyStaff; manual retry ManagerOrAdmin | Operators see errors; only managers re-trigger work |
| Audit | `SystemEventLog` for manual retry and stock import | Doc §6 audit for important operations |

## Task 008 result (reviewed 2026-09-30)

Orders and processing read API, manual retry with fresh attempt budget (`AttemptBudgetStart`, migration `ProcessingTaskAttemptBudget`),
audit events `processing.retry_requested` / `inventory.imported`; 366/366 tests. Review: `docs/tasks/008-*.review.md`.

## Task 009 plan — user management (2026-09-30)

| Topic | Decision | Reason |
| --- | --- | --- |
| Token revocation | `sstamp` claim + `OnTokenValidated` check of security stamp and lockout, cached per user 30 s in `IMemoryCache` | Locked users / changed roles lose access within 30 s instead of up to 60 min; in-process cache only |
| Roles | Exactly one role per user | Admin/Manager/Staff are hierarchical; simpler UI and policies |
| Password reset | Admin sets a temporary password (no e-mail infrastructure) | Self-service flows need e-mail (task 024 area) |
| Guards | `last_admin`, `cannot_modify_self` | Never lock everyone out |

## Task 009 result (reviewed 2026-10-01)

User management API, security-stamp token revocation (≤ 30 s across instances, immediate on the changing instance), guards,
audit; 428/428 tests. Review: `docs/tasks/009-*.review.md`. Smoke `scripts/smoke/task-009.ps1` passed (user).

## Frontend decisions (user, 2026-10-01) — task 010

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Lockout vs. sessions | Token check and `/me` rely on the security stamp only; temporary (failed-login) lockout blocks new logins but does not kill sessions | Prevents kicking users out by typing wrong passwords for their e-mail | — |
| UI library | Ant Design v5 | Data-heavy admin UI (tables, filters, forms), Vietnamese locale | — |
| Token storage | `sessionStorage`, 60-min token, no refresh token | Survives reload, cleared on tab close | Hardening (026): CSP, httpOnly cookie |
| Language | Bilingual vi (default) / en with i18next; API messages localised via `Accept-Language`; `errorCode` translated on the client | User requirement | — |
| Language switch | Changes text only: no reload/remount, state kept, no layout shift (fixed widths, truncation + tooltip), same date/money formats in both languages | User requirement | — |
| Routing / state | `react-router` v7 (library mode); RTK Query for server state, Redux slices only for auth/UI | Doc §3.3 | — |
| Dev serving | Vite proxy `/api` → API (5080) | No CORS in dev | Deployment (025) |

## Task 010 result (reviewed 2026-10-01)

Frontend scaffold (React 19, AntD 5 → v6 planned in 011, RTK Query, react-router 7, i18next), Users page, stamp-only revocation,
`Accept-Language` localisation; backend 433/433, frontend 42/42, smoke 17/17; user's browser check passed. Review: `docs/tasks/010-*.review.md`.

| Topic | Decision | Reason |
| --- | --- | --- |
| AntD major | Move to AntD 6 in task 011 (v5 needs a React 19 patch) | Cheapest while only one page exists |
| Validation texts | All custom validation messages localised (vi/en) through one backend mechanism; `errorCode`/keys unchanged | Bilingual UI must not show English messages |
| Business rules in the UI | None duplicated client-side (e.g. `isLowStock` comes from the API) | Single source of truth |
| Test accounts | Created only by smoke scripts (`smoke-*`), no Admin unless required | Dev database hygiene, least privilege |
| Bundle | Route-level code splitting from task 011 | Main chunk under 500 kB |

## Task 011 result (reviewed 2026-10-01)

Products, Customers, Channels pages; AntD 6; validation messages vi/en through `ValidationMessages` resources; `isLowStock` on
product responses (shared predicate, in SQL); route-level code splitting (main chunk 1,369 → 153 kB); backend 479/479, frontend
77/77, smoke 16/16. Review: `docs/tasks/011-*.review.md`. User's manual check passed.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Validation error keys | **All** keys of a 400 are camelCase JSON paths, query string and model binding included (`to`, `sortBy`, `pageSize`, `items[0].productId`); normalised once in `ValidationProblemResponse`; `$…` body paths of malformed JSON unchanged (task 012 A1) | `ModelStateDictionary` is case-insensitive, so supplied query parameters kept the C# name (`To`); one convention for every client. Alternatives: document the difference (special case in every client), fix only in `ValidationFilter` (misses model-binding errors) | — |
| Field names in messages | Localised display names from `FieldNames.resx` / `.vi.resx`, keyed by JSON member name, wording = UI label (task 012 A2) | Messages must not show technical English names in the Vietnamese UI | A field name needs different wording per form |
| Shared form/list pieces | `useListQueryState`, `useFormSubmission`, `useResetFormOnOpen`; no generic CRUD framework | Only what the list pages actually share | — |
| Form dialogs | Reset on every open (`useResetFormOnOpen`); `destroyOnHidden` alone keeps the previous record during the close animation | Found in the browser: edit B showed A's values | — |
| Prices in the UI | Whole VND | VND has no minor unit | Another currency |
| Admin for browser checks | Reuse the locked test Admin `ui-check-61563`, lock it again afterwards; never create another Admin | Dev database hygiene, least privilege | — |

## Task 012 plan — inventory pages (2026-10-01)

| Topic | Decision | Reason |
| --- | --- | --- |
| Stock import UI | Own route `/inventory/import` (Manager/Admin, not a menu item), up to 200 lines with `Form.List`, confirmation before submit, result view from the response | A goods receipt can have many lines; the import cannot be undone (no reversal API) |
| Re-submitted import | No client idempotency key: the document number is the key; 409 `stock_import_already_applied` is shown on the document field and points to the movement history | Server already guarantees once-only application (task 003) |
| Product picker | Shared `ProductSelect` with server search, reused by order entry (013) | One picker, no client-side product cache |
| Movement actor | `createdByEmail` on `InventoryMovementResponse` (null for `system:*` actors); `createdBy` unchanged | Audit view must show who imported, not a user id; resolved in the same query |
| Date filters | Address keeps local dates; requests send local start/end of day with offset; server range inclusive | Shareable links, correct day boundaries in the user's time zone |
| Route errors | Localised `errorElement` with reload for chunk-load failures | Lazy chunks disappear after a redeploy |

## Task 012 result (reviewed 2026-10-01)

Inventory, stock import and movement history pages; shared `ProductSelect`; `createdByEmail` on movements; camelCase keys for
every 400; localised field names (`FieldNames` resources as FluentValidation display names); route error page; dialogs closed
on session end; search box follows the address. Backend 528/528, frontend 120/120, smoke 24/24. Review:
`docs/tasks/012-*.review.md` (user's manual check pending).

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| "Stop if more than X is needed" in a task | Protects the *mechanism* named in the task; adopting the finished mechanism at an existing producer of the same output (e.g. `IdentityUserService.RoleOf` building `RoleUnknown`) is in scope and is reported as a deviation | Leaving such a producer would ship the defect the task removes | — |
| Frontend test parallelism | `maxWorkers: 4` in `vite.config.ts` as a ceiling; CI may override with `--maxWorkers` | The suite is memory-bound (one jsdom per file); more workers time out while API/Docker run | 025 (CI runner sizing) |
| Confirmation dialogs | Short yes/no confirmations: `App.useApp().modal.confirm` (texts frozen while open — known limitation). Confirmations that show a data summary: a page `Modal` (follows a language switch, unmounts with the route on session end) | AntD 6 `modal.confirm` keeps its opening texts | — |
| Cache rules | A successful mutation invalidates every list that displays fields it changed (e.g. product change → inventory list); lists of data the Workers change in the background use `refetchOnMountOrArgChange` | No stale stock or orders after navigation without realtime | 016: realtime push → refetch; keep refetch on SignalR reconnect |
| Movement actor e-mail | `createdByEmail` visible to every role, Staff included | Internal audit view of internal users | Users outside the company get access |
| API messages after a language switch | Messages received from the API (field errors, inline alerts, form-level errors) stay in the language they arrived in | Re-requesting to translate is not worth it; consistent since 010 | Users ask for it |
| ~~Main chunk budget~~ | ~~Every frontend report states the main chunk size and explains growth above 10 kB~~ — superseded by "Bundle metric" (review 013) | 012 grew 70 kB unexplained | — |

## Task 013 plan — order entry, orders list/detail (2026-10-01)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Order entry layout | Three stacked panels on `/order-entry`, one per seeded slug (`digital-platform`, `website`, `pos`); each has its own form, request and outcome | Doc §3.1: three components on one page, routed by slug, no table per component; stacked panels can be used one after the other without hidden state | A channel needs its own panel |
| Channel availability | Panel shows the channel's name and status from `GET /api/channels`; inactive or missing → warning only, form usable; the API's 404 `channel_not_found` is shown in the panel | No client-side copy of the server rule | — |
| External order id | Generated per panel (`DP-`/`WEB-`/`POS-` + local `YYYYMMDD` + 6 random `[A-Z0-9]`), editable, kept after a failed request, replaced only by "New order" or an explicit "new number" | It is the intake idempotency key: a resend after a lost response must be the same order; editable to demonstrate duplicate (202 `duplicate`) and conflict (409 `order_conflict`) | Real channel input (023 webhooks) |
| Outcome tracking | Poll `GET /api/processing/tasks/{taskId}` every 1.5 s until `Succeeded`/`Failed`, at most 60 s ("Check again" restarts), paused while the tab is hidden (`skipPollingIfUnfocused` + `setupListeners`); then `GET /api/orders/{orderId}` once; invalidate `Order`/`Inventory`/`InventoryMovement`/`Product` once at the terminal state | The order is created asynchronously by the Order Worker; the database is the source of truth; SignalR arrives in 015/016. Bounded polling of one row per open outcome view | 016: push triggers the refetch; decide then whether polling stays as fallback |
| Amounts in the form | No price or total in the order-entry form; amounts only from the order (price snapshot at intake) | No client arithmetic on business amounts; price can change before intake | Users need a preview |
| Outbox `lastError` | Never shown in the UI; attempt `errorMessage` (sanitised by the API) may be shown | `lastError` is not sanitised for the UI | 016 if operators need it (Manager-only view) |
| Manual retry on the order detail | Not in 013 | Only `OrderProcessing` tasks are order-bound today and they are `Succeeded` whenever an order exists | 016 |
| Local-day date ranges | One helper `shared/utils/dateRange.ts` for movements and orders | Second page with the same rule | — |

## Task 013 result (reviewed 2026-10-02)

Order entry (three channel panels, outcome by polling the processing task), orders list and detail, order links in the
movement history, `CustomerSelect`, processing API slice; Part A: picker titles, deterministic expiry test, main-chunk
analysis. Frontend 167/167, smoke 31/31, 38/38 mutations; backend unchanged. Review: `docs/tasks/013-*.review.md`
(user's manual check pending).

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Tab visibility for polling | `setupListeners` with a custom handler (`app/tabVisibility.ts`), mounted from `App.tsx`: only `visibilitychange`, and the API's `focused` flag is corrected whenever it differs from the real visibility | RTK's default handler registers once per page load (module flag) and `resetApiState` resets `focused` to its page-load value — polling would stay off after logout + login in a background-opened tab | A query needs `refetchOnFocus` / `refetchOnReconnect` |
| Cache after an order task finished | Invalidate the order *list* tag, `Inventory`, `InventoryMovement`, `Product` once; not single orders | The order being loaded would be fetched twice; orders do not change after creation yet; the detail refetches on open | Orders get state changes after creation (cancel, payment) |
| Server-search pickers | Offer no option while the typed text is not what the options answer (debounce pending or request running) | Enter chose the first option of an older search: wrong product on a line (since 012) | — |
| Default sort shown in a table | `useListQueryState({ defaultSortDir })`: the default field's column shows the API's default direction; choosing exactly that order writes nothing to the address | AntD reports a shown default order like a chosen one | — |
| Bundle metric | Every frontend report states the **first-load set** (entry + static imports), raw and gzip, and explains growth above 10 kB raw; the main chunk is listed but not judged alone. No `manualChunks` for now | The main chunk moves by tens of kB when the bundler regroups shared vendor code (012 +67 kB, 013 +33 kB, no new code) | The first-load set must shrink (026); cheapest step: load only the active language (016 Part A) |

## Task 014 plan — channel product mapping and stock sync (2026-10-02)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Channel APIs | **Simulated (user, 2026-10-02):** `IChannelStockClient` (Application) with `SimulatedChannelStockClient` (Infrastructure/ExternalChannels), in-process in the Workers host; per-channel mode (`Succeed` / `TransientError` / `PermanentError` / `Timeout`) and latency in configuration (live reload), plus Development-only external-SKU prefixes for deterministic failures | No real channel accounts exist; retry/DLQ must be demonstrable without code changes; a real adapter replaces the simulated one without touching the worker. Alternatives: real sandbox API (blocked on credentials, flaky tests), HTTP mock service (extra infrastructure) | A real channel is integrated; 022 needs readable channel-side stock (simulator state) |
| Channel ↔ queue | Fixed map by slug: `website` → `stock.sync.website`, `pos` → `stock.sync.pos`, `digital-platform` → `stock.sync.marketplace` (doc §10). Other channels can have mappings but are not synced | Doc topology has exactly these queues; one queue per channel keeps channels independent (doc §13) | Channels are added at runtime and need sync |
| Stock-sync task | One `ProcessingTask(StockSync)` per (channel, `inventory.changed` message) with `IdempotencyKey = stocksync:{channelId:N}:{messageId}`, `ChannelId` and `ProductId` set, `OrderId` null; the correlation id of the change links it to the order or import | Exact history per change and channel; duplicate deliveries hit the unique key; an order detail is not flooded with sync tasks | Sync volume needs coalescing per (channel, product) |
| No mapping | Product without an Active mapping on the channel, or channel Inactive → ACK, no task | Every movement reaches every channel queue; tasks only for real work | — |
| Pushed value | The **current** `AvailableStock`, re-read before the push (never the message snapshot); after a push it is read again and pushed again while it differs (at most `MaxPushesPerAttempt`, default 3); consumer concurrency 1 per channel queue per instance | Messages arrive out of order (delayed retries) and several instances may push the same product; re-read + verify converges to the central value without locks or holding a transaction during an external call | Real channel rate limits make extra pushes expensive |
| Directed messages | Event `stock.sync.requested` (payload `taskId`, `channelId`, `productId`; envelope `taskId` = aggregate id), published with `OutboxEvent.RoutingKey` = the channel's queue name; each stock-sync queue is additionally bound to its own name on `omnichannel.topic` | Initial sync, manual retry and later reconciliation must reach one channel only; `inventory.changed` fans out to all three | — |
| Manual retry of a stock-sync task | Creates a directed `stock.sync.requested` for the task's channel instead of copying the fanned-out `inventory.changed` | A copy would reach all channels and create unrelated tasks; the retried task would stay `Pending` | — |
| Initial sync | Creating a mapping, re-activating it, or changing its external ids while Active creates a `StockSync` task + directed message in the same transaction (only for an Active channel with a sync queue) | A mapped product without later movements would never reach the channel | 022 reconciliation |
| Resilience | Polly v8 pipeline per channel: attempt timeout (10 s) + circuit breaker on transient failures; **no Polly retry** — retries are the broker delay tiers, each recorded as an attempt | Retries inside an attempt would hide tries from the attempt history | — |
| Error classes | Channel timeout, 5xx/unavailable, 429, open circuit, transient DB errors → retry; channel rejection (unknown external product, invalid value) → permanent; shared `ProcessingErrorClassifier` | Same retry/DLQ model as orders (task 007) | — |
| Expected unique violations | 23505 on a constraint that a service maps to 409 / an idempotent outcome is not logged at Warning/Error by EF Core; every other database failure still is. One registry of these constraint names, used by the catch clauses and the log filter | Operators must not see business conflicts as errors (review 012); unexpected violations stay loud | — |

### Deviations / additions to the architecture document
11. `OutboxEvent.RoutingKey` (nullable; null = event type) — directed messages to one channel queue.
12. Event type `stock.sync.requested` and one extra binding per stock-sync queue (routing key = queue name).
13. `ProcessingTask.ChannelId`, `ProcessingTask.ProductId` (nullable FKs; required for `StockSync`) — closes the open point
    "StockSync per channel" of steps 1–3.
14. Endpoints `GET/POST /api/channels/{channelId}/products`, `PUT /api/channels/{channelId}/products/{channelProductId}`
    (doc §7.1 `ChannelService`: "Channel và ChannelProduct mapping"; not in the §11 table).

## Task 014 result (reviewed 2026-10-02)

Mapping API, Stock Sync Worker with simulated channels behind Polly (timeout + circuit breaker), directed
`stock.sync.requested`, migration `StockSync`, expected 23505 no longer logged as errors; backend 669/669, frontend 168/168,
smoke 38/38 (throwaway environment; the development run, migration and manual checks of 012–014 confirmed by the user on
2026-10-02). Review: `docs/tasks/014-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Expected-conflict logging | EF Core's `CommandError` / `SaveChangesFailed` at Debug; `DatabaseFailureLogInterceptor` writes them again at Error unless the cause is a 23505 on a constraint in `ExpectedUniqueViolations`; `IsUniqueViolation` only matches registered names | `CommandError` carries no exception, so no log filter can classify it; one registry for catch clauses and log level | — |
| Attempt bookkeeping | `ProcessingAttemptStore` shared by the Order and Stock Sync workers | One mechanism for start / transient / final failure | — |
| Shutdown during an external call | `HandlerResult.Abandon`: delivery left unsettled, requeued on channel close (counted in `x-delivery-count`); stock sync only | Consumers get no cancellation token (006/007); every other result settles | — |
| Open circuit vs attempt budget | `channel_circuit_open` counts toward `MaxAttempts`; long outages end in `Failed` + DLQ; recovery by reconciliation (022) and retry from the processing page (016) | Not counting it would make retries unbounded; only the newest task per (channel, product) matters because every push sends the current stock | Real channels with long maintenance windows |

## Task 015 plan — realtime hints and dashboard summary (2026-10-02)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Worker → API notification | **PostgreSQL `LISTEN/NOTIFY` (user, 2026-10-02).** Hints are derived from the change tracker by one `SaveChanges` interceptor in Infrastructure (added `Order` → `order.created`, added `InventoryMovement` → `inventory.changed` — every stock change writes one, also the raw-SQL ones —, added `ProcessingTask` or a changed task status → `processing.task.changed`), so no producer can forget one; they are sent with `pg_notify('omnichannel_ui', …)` *after* `SaveChanges` succeeded on the same connection — inside an explicit transaction (delivered at its commit, discarded by a rollback) or, without one, after EF Core's own commit | Doc §12 "PostgreSQL commit → application notification": a hint never precedes or outlives its data; no new infrastructure; the API stays independent of RabbitMQ (decision A). Alternative: RabbitMQ notification queue per API instance (API would depend on the broker, one more consumer) | Notification volume makes `NOTIFY` a bottleneck (thousands/s) |
| Hint content | Event type + ids only (`order.created`, `inventory.changed`, `processing.task.changed` with task type/status/order/channel ids); one `NOTIFY` per unit of work, payload well under PostgreSQL's 8000-byte limit (id lists capped, overflow → "all") | SignalR is not the source of truth: clients refetch through the API with their own authorization; no business data on the wire | — |
| Doc §12 event names | `processing.failed` and `sync.succeeded/failed` are `processing.task.changed` with `taskType` / `status`; `order.status.changed` is defined when an order can change state after creation; `low_stock.created` comes with 017 | One event per task transition, filtered by the client | — |
| API side | One `LISTEN` connection per API instance (background service, reconnect with backoff); after a reconnect every client gets `resync` (hints may have been missed) ; hints batched per short window (≤ 250 ms) into one hub message | Each instance pushes to its own connections: scale-out without a SignalR backplane; batching avoids refetch storms | Many API instances with sticky sessions → consider a backplane |
| Hub | `/api/hubs/notifications`, server → client only, AnyStaff, JWT from the `access_token` query parameter for this path only, `CloseOnAuthenticationExpiration`, security-stamp check at connect (existing `OnTokenValidated`); the token never appears in logs | WebSockets cannot send headers from the browser; revocation keeps working at (re)connect; hints carry no data, so a revoked user keeps at most hint traffic until the token expires | Hints ever carry data |
| Dashboard summary | `GET /api/dashboard/summary?from&to` (AnyStaff): orders in range by status and channel + sales amount (Reserved + Completed), current inventory counts (shared low-stock predicate, out of stock), current non-terminal tasks per type and failures in range, stock-sync health per channel, outbox health | One request for the overview page; every number from SQL aggregates, none computed by the client | 017 adds open alerts |

## Task 015 result (reviewed 2026-10-02)

Realtime hints (`RealtimeHintInterceptor` → `pg_notify('omnichannel_ui')` → `PostgresHintListener` → hub
`/api/hubs/notifications`, message `hints` `{ events, resync }`), `GET /api/dashboard/summary`; Part A: stale stock-sync
task after deactivation (014 P2), `GET` of one mapping, README section 6, `.http` samples. Backend 745/745 (505 unit, 240
integration), smoke 27/27 (throwaway environment; the development run and the manual check confirmed by the user on
2026-10-02), 15 mutations; frontend untouched; no migration. Review: `docs/tasks/015-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Token in log events | `AccessTokenRedactionEnricher` replaces every `access_token=` value in string properties of every Serilog event of the API | The hub reads the token from the query string; `Microsoft.AspNetCore.Hosting.Diagnostics` logs URLs at Information — one level change must not leak tokens | Hardening (026): reverse proxy / load balancer logs (outside the application) |
| Oversized hints | Per save: > 100 product ids → `inventory.changed` with `all: true`; payload still > 7999 bytes → one `{type, all: true}` per event type. Per API instance: batch > 200 events or queue (10,000) full → `resync: true` without events | Degrade to "refetch" instead of failing a business transaction or flooding clients | — |
| Summary response shape | `byStatus`, `processing`, `stockSync` are lists of objects (every enum value / channel, zeros included), not objects keyed by enum name | Keys would be PascalCase enum names; lists keep camelCase and a stable order | — |
| Summary consistency | Nine independent aggregate statements, no common snapshot; sections can differ by changes committed in between | Overview page; a `REPEATABLE READ` read-only transaction is the fix if it ever matters | Users compare numbers across sections |
| `stockSync.lastSucceededAt` | The channel's most recent successful sync **ever**, not bound to the range | An operator needs "when did this channel last get stock" exactly when the range shows no success | 026 adds the index for it |
| Summary indexes | None added; three statements grow with the table (tasks by type/status, stock sync per channel, outbox failed), measured 3–5 ms at 24k tasks / 40k outbox rows | No measured need yet | 026, or earlier when the endpoint exceeds 200 ms: partial indexes on `OutboxEvents (Status = 'Failed')`, `ProcessingTasks` non-terminal statuses, `ProcessingTasks (ChannelId, UpdatedAt)` for `StockSync`/`Succeeded` |
| Hint audience | Every connected user receives every hint (ids only) | Every role may read orders, inventory and tasks | A page whose ids are restricted by role |
| Smoke scripts and foreign environments | (016 Part A) a script starts an API / Workers host only for the local default (`SMOKE_BASE_URL` unset) | The 015 run started an API with the development secrets while targeting a throwaway port | — |

## Task 016 plan — realtime client, dashboard, Sync & Processing page (2026-10-02)

Roadmap change (user, 2026-10-02): the planned 016 is split. **016** = realtime client, dashboard, Sync & Processing page,
order-entry outcome by push, Part A; **016b** = channel product mapping UI (Channels page) incl. "channel is never
synced". Numbers 017+ are unchanged.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| SignalR client | `@microsoft/signalr` (official client), loaded with a dynamic `import()` after sign-in so it stays out of the first-load set; client log level Warning (the client must never print a URL with the token) | No hand-written WebSocket protocol; the login page does not need it | — |
| Connection lifecycle | One connection per session: started when a session exists, stopped on logout / expiry / 401; `accessTokenFactory` reads the current token from the store; automatic reconnect with delays 0, 2, 5, 10, 30 s, then every 30 s while signed in (no give-up) | The token has no refresh: the session ends at expiry anyway (`SessionManager`), so reconnect never needs a new token | Refresh tokens (026) |
| Resync | Every transition to *connected* (the first connect included) and every `resync: true` message invalidates the realtime tags once | Hints sent before the connection existed or while it was down are lost; one extra refetch at page load is the price | — |
| Event → tag map | One module: `order.created` → `Order` LIST, summary; `inventory.changed` → `Inventory`, `InventoryMovement`, `Product` LIST, summary; `processing.task.changed` → `ProcessingTask` (id + LIST), `Order` (id, when `orderId`), summary; `all: true` = every tag of that event type | Hints carry ids only; RTK Query refetches what is on screen with the user's own authorization | — |
| Refetch coalescing | Tags from hints are collected and flushed at most once per second (trailing); `resync` flushes the realtime tags, never `resetApiState` | A stock-sync burst produces several hub messages per second; refetches per tab must stay bounded (review 015) | Users need sub-second freshness |
| Polling and refetch-on-open | Order-entry outcome: push (tag invalidation) while connected, the 013 polling (1.5 s, 60 s window) only while the connection is not connected. Lists keep `refetchOnMountOrArgChange` | Hints are best effort; one request per page open is cheap and covers the disconnected case | — |
| Connection indicator | Header shows *live* / *reconnecting* / *offline* as icon + text (not colour alone), localised | Users must know when the screen is not live | — |
| Dashboard | Tiles and tables from `GET /api/dashboard/summary`, no chart library; range presets (last 24 h default, today, 7 days, 30 days) and a custom range kept in the address; the 92-day rule is the server's (400 shown on the field) | No business rule in the client; a chart library is a decision for 021 | Reports (021) |
| Sync & Processing page | `/processing`: task list (filters type, status, channel, created range, correlation id, error code; address-backed) and a detail route `/processing/tasks/:taskId` (attempts, message status — never `lastError`), retry for Manager/Admin on `Failed` with confirmation; the same retry on the order detail | Doc §3.2 and §16 "trace an order to task, worker, instance, attempt, error"; retry from the processing page is the recovery path for long channel outages (review 014) | — |
| Product of a task | `productSku` added to the task summary and detail (left join in the same query); channel names from the channel list | A task list must not show product GUIDs or look products up per row | — |

## Task 016 result (reviewed 2026-10-02)

Realtime client (`@microsoft/signalr` 10.0.11, lazy chunk 55 kB raw; own reconnect loop; hints → coalesced tag
invalidation; header indicator), dashboard (`GET /api/dashboard/summary`, range in the address), Sync & Processing list,
task detail and retry (also on the order detail), order-entry outcome by push; `productSku` on task summary/detail;
Part A: smoke scripts start hosts only for the local default, pickers hide only stale answers, panel title, orders table
at 1280 px, only the active language loaded, `npm run measure`. Backend 746/746, frontend 243/243, smoke 23/23; first-load
set 967 kB raw / 317 kB gzip. The open browser items and the test/lint run after the last fixes were confirmed by the
user on 2026-10-02. Review: `docs/tasks/016-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Hub reconnect | `realtimeConnection.ts` owns connect/retry; the library's `withAutomaticReconnect` is not used; a 401 on negotiate is detected from the HTTP status recorded by a `DefaultHttpClient` subclass | The library does not retry a failed first start and does not tell a refused token from an unreachable server | The library gains both |
| Indicator states | *connecting* (first start), *live*, *reconnecting* (quick retries 0–30 s), *offline* (quick retries used up; still retried every 30 s) | The user must know when the screen is not live and not coming back by itself soon | — |
| RTK Query and invalidation | An invalidated entry without subscribers is removed, not refetched; with `invalidationBehavior: 'delayed'` an invalidation waits while any request is pending | Verified in the library source and by a test; a page opened later fetches fresh data anyway (`refetchOnMountOrArgChange`) | RTK Query major version |
| Language loading | One chunk per language; the active one awaited before the first render, the other loaded before `changeLanguage` (always `shared/i18n` `changeLanguage`, never `i18n.changeLanguage`) | First-load set −24 kB; no flash of keys, switch stays "text only" | — |
| Bundle measurement | `npm run measure` (after `npm run build`) is the source of the first-load-set numbers in every frontend report | One reproducible method | — |
| Dashboard links | Counts "in the range" link with type, status and newest-updated-first, not with the range | List filters on creation time, summary counts by update time | The task list gets an update-time filter |
| Local connection host (review 016) | `127.0.0.1` instead of `localhost` in every local connection setting (PostgreSQL, RabbitMQ, migrator); ports stay bound to `127.0.0.1` only | On Windows `localhost` tries `::1` first; the refused IPv6 connect costs ~2 s per new pooled connection (measured 2075 ms) | Docker publishes on IPv6 too |
| Hint flush (review 016) | Throttle instead of a trailing window: flush at once when the last flush is ≥ 1 s ago, otherwise at the end of that second with the union | Same bound (≤ 1 flush/s per tab) without adding 1 s to every idle-state update; end to end "typically ≤ 2 s" | Users need sub-second freshness |
| Order outcome and lists (review 016) | The once-per-terminal-state invalidation of lists only while the connection is not live | Live: the same hint batch already invalidated them (order, movements, task status are one `SaveChanges` → one `NOTIFY`) | Orders change state after creation |
| WebSockets behind a proxy | Every proxy in front of `/api` must pass the WebSocket upgrade for `/api/hubs/*` (dev: Vite `ws: true`) | The hub negotiates WebSockets; long polling would still work but costs a request per message | 025 |

## Task 016b result (reviewed 2026-10-03)

Channel detail `/channels/:channelId` with product mappings (list, create, edit incl. status, sync-result alert with the
task's live status), stock-sync column on the channels list, `stockSyncEnabled` on channel responses; Part A:
`127.0.0.1` for local connections, hint flush as a throttle, order outcome invalidates lists only when not live; fix of a
pre-existing form-dialog defect. Backend 752/752, frontend 281 tests (3 of 5 full runs green, see "Frontend test
stability"), smoke 25/25 (second run). First-load set 968 kB raw / 317 kB gzip. Review: `docs/tasks/016b-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Form dialogs | Every dialog `Form` has `clearOnDestroy`; `useResetFormOnOpen` resets only a mounted form (one with registered fields) | After a full close the destroyed form kept the previous record's values and saving wrote them onto another record (production only; StrictMode hid it in development) | AntD major upgrade (re-check the "mounted" test) |
| Browser checks | The real-browser check of a frontend task runs on the production build (`npm run build` + `vite preview`, same `/api` proxy); the dev server is for iterating | StrictMode's double mounting in development hid a data-corrupting defect | — |
| `stockSyncEnabled` | Derived from `RabbitMqTopology.StockSyncQueueOf(slug)` after the query, on every channel response; a capability, independent of the status | One map for sync, dashboard and UI; the client has no copy of it | Stock-sync queues per channel at runtime |
| Mapping writes | Status is a field of the edit dialog, no separate action, no confirmation; no client rule for "one external id" (the server's 400 on `externalProductId`) | Reversible, nothing deleted; business rules stay on the server | — |
| Frontend test stability (review 016b) | Smoke scripts run `npm run build` / `npm test` after stopping the hosts they started; `testTimeout` stays 20 s; worker count and slow-test split decided by measurement in 017 Part A | Whole-test timeouts under memory pressure made 2 of 6 known runs red | CI runner sizing (025) |

## Task 017 plan — alerts backend (2026-10-03)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Alert storage | **New `Alerts` table with a lifecycle `Open` → `Acknowledged` → `Resolved` (user, 2026-10-03)**; `SystemEventLog` stays the event history | `SystemEventLog` alone cannot say what is open now, cannot be acknowledged without mutating an append-only audit table, and writes one row per occurrence. Alternatives: `SystemEventLog` only; alerts derived from current state (no acknowledge, permanent failures listed forever, complex "superseded task" query) | — |
| Acknowledge | Manager/Admin only (user, 2026-10-03); `Open` → `Acknowledged`, never resolves; audited in `SystemEventLog` (`alert.acknowledged`) | Same role as manual retry: Staff must not remove a failure from "needs attention" before a manager sees it | Staff need to triage |
| Types and keys | `LowStock` per product, `InsufficientStock` per product, `StockSyncFailed` per (channel, product), `ProcessingFailed` per task; one unresolved row per `DedupKey` (partial unique index `WHERE Status <> 'Resolved'`); a recurring condition refreshes the row (`OccurrenceCount`, latest task/order/error), a condition that returns after resolution opens a new row | Dedup without losing history; per (channel, product) because only the newest stock-sync task matters (log 014) | — |
| Resolution | Always by the system: low stock when the shared predicate is false again; insufficient stock by the next import of the product; stock-sync failure by any later `Succeeded` task of the same (channel, product); processing failure by the same task succeeding (manual retry). No manual resolve | The source-of-truth tables decide; an operator cannot hide a live condition | Users ask for manual resolve |
| Write mechanism | One raw SQL statement per alert write, in the transaction of the causing change (`INSERT … ON CONFLICT … WHERE Status <> 'Resolved'`, conditional `UPDATE … RETURNING`); writer throws without an explicit transaction | Race-free without extra locks or check-then-insert; an alert never exists without its cause nor survives a rollback | — |
| Low stock | Reconciled from current rows after every stock or threshold change (two set-based statements over the changed products, under the inventory row locks already held), not detected by transition; product update locks the inventory row before changing the threshold; new products are low at creation (intended); the reserve `UPDATE` itself is unchanged | Self-healing and exact under the existing lock order; no second copy of the rule | A stock release/cancel path is added: it must call the reconcile |
| Hints | Alert writer sends its own `pg_notify` in the same transaction: `alert.changed` for every row change, `low_stock.created` when a `LowStock` row is inserted; the change-tracker interceptor does not capture alerts | Raw SQL writes are invisible to the change tracker; transactional `NOTIFY` keeps "hint never precedes its data" | — |
| Backfill | Migration opens `LowStock` for products low now, `StockSyncFailed` for pairs whose newest task is `Failed`, `ProcessingFailed` for other `Failed` tasks; no `InsufficientStock` backfill | Alert view matches the current state from the first deploy | — |
| Retention | Resolved alerts are kept; no purge | Volume is small (deduplicated) | 026 |

### Deviations / additions to the architecture document
15. `Alerts` table (the ERD has no alert entity).
16. `GET /api/alerts/{alertId}`, `POST /api/alerts/{alertId}/acknowledge` (doc §11 lists only `GET /api/alerts`).
17. Hint `alert.changed` in addition to the doc's `low_stock.created`.

## Task 017 result (reviewed 2026-10-03)

`Alerts` table (migration `Alerts` with backfill), `AlertStore` as the only writer, eight call sites, `GET /api/alerts`,
`GET /api/alerts/{alertId}`, `POST /api/alerts/{alertId}/acknowledge`, `alerts` in the dashboard summary; Part A:
smoke scripts run npm after stopping their hosts, 4 test workers kept (3 measured 13 % slower), tests > 5 s split,
strict `Assert-Step` + self-test. Backend 820/820, 15/15 mutants killed; frontend 292 tests, 5 of 5 full runs green
(second series); smoke 53/53 (throwaway environment as `omnichannel_app`). Correction (review 018): the user's first
migration attempt never reached PostgreSQL (container not running); task 018 found the table missing on development and
the user applied migration `Alerts` later on 2026-10-03. Review: `docs/tasks/017-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Statements in the reserve transaction | 5 → 8 per order (two set-based low-stock statements, one resolve of the task's `processing_failed`), a 9th `pg_notify` only when an alert row changed; the reserve `UPDATE` is unchanged | Measured by a command-interceptor test | Load test of the reserve path (026) |
| Alert row locks | Multi-row resolves lock in `DedupKey` order inside one statement; upserts of one call run in key order; low-stock rows rely on the inventory row locks (`ProductId` order) | An import resolving several insufficient-stock alerts and a rejected multi-line order could deadlock otherwise | — |
| Backfill | "Newest stock-sync task" = most recently **finished** (`Failed`/`Succeeded` by `UpdatedAt`); ids UUIDv4 (`gen_random_uuid()`); idempotent | The order in which the live system would have opened and resolved; PostgreSQL 17 has no v7 generator | — |
| Partial unique index | `UX_Alerts_DedupKey_Unresolved` not in `ExpectedUniqueViolations` | Every insert names it as `ON CONFLICT` arbiter; no update moves a row back into it | — |
| Insufficient stock from a stale read | Accepted: an import committed between the rejection's unlocked availability read and its alert write leaves an alert that the next import resolves | The alert states a true fact (an order was rejected); closing it needs `FOR SHARE` locks in that path | Operators report stale insufficient-stock alerts |
| Deactivated mapping | **(user, 2026-10-03)** `Active` → `Inactive` of a mapping resolves the pair's `StockSyncFailed` alert in the same transaction (018 Part A6); channel deactivation does not touch alerts. An attempt already running at deactivation can still fail finally and reopen it; a manual retry then completes without a push and resolves it | The pair is no longer synced, so the failure needs no attention; a channel re-activation does not re-sync its mappings, so its alerts keep their meaning | Channel re-activation starts a sync |
| Smoke table | (018 Part A5) `OK` column first; long values never drop a column | The 017 run lost the `OK` column | — |
| Form-dialog reset | (018 Part A1, P1 pre-existing) reset from a component inside the `<Form>` subtree, on every open (also the same record), replacing `useResetFormOnOpen` | The dialog renders its content one commit after its parent; a reset in the parent's effect used the previous record's `initialValues` (about 5 % of quick reopenings in jsdom) | AntD major upgrade |

## Task 018 plan — Alerts page; Logs/Audit split into 018b (2026-10-03)

Roadmap change (user, 2026-10-03): the planned 018 is split. **018** = Alerts page, dashboard alert card, Part A
(review 016b questions 3–4 and findings, review 017 questions 1, 3, 5); **018b** = Logs/Audit: a new backend endpoint
over `SystemEventLogs` + the page. Numbers 019+ are unchanged.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Alert detail | No detail route; the list row shows everything the API returns, with links to order, task, channel and stock import | Every field fits a row; `GET /api/alerts/{id}` stays available | Alerts get history or comments |
| New-alert notification | No toast, sound or header badge; `low_stock.created` and `alert.changed` only refresh what is on screen (alerts list, dashboard card) | Bulk changes would flood toasts; a badge would fetch the summary on every page | Users ask for it (026 accessibility pass) |
| Alert tags | `Alert` is a realtime tag type (resync included); `alert.changed` → id + LIST + summary; `low_stock.created` → LIST + summary; `inventory.changed` also → Alert LIST; product and channel updates invalidate Alert LIST | A low-stock row shows the **current** stock and threshold, which change without an alert row changing | — |
| Acknowledge in the UI | Manager/Admin, `Open` rows only, with a confirmation (cannot be undone) | Same pattern as manual retry | — |
| Default status filter | The client sends no `status` for the default view; the server's default (Open + Acknowledged) applies | One rule, on the server | — |
| Logs/Audit access (018b) | **(user, 2026-10-03)** Manager and Admin; events `user.*` only for Admin, filtered on the server | Managers trace processing, imports and acknowledgements; user-management audit is the Admin's | — |
| Logs/Audit payload (018b) | The endpoint returns a per-event-type **allowlist** of payload fields (one map in Application); unknown types and fields are not returned; never `lastError` of the outbox | Payloads are free-form jsonb written for operators; the outbox error may carry broker or exception text | A new event type is added (it must enter the map) |
| Logs/Audit endpoint (018b) | New `GET /api/system-events` (paging contract, filters level / event type / correlation id / order / task / range ≤ 92 days) — doc deviation 18 | Doc §3.2 has the page, §11 no endpoint for it | — |

## Task 018 result (reviewed 2026-10-03)

Alerts page `/alerts` (filters, sort and paging in the address; details per type; acknowledge with confirmation for
Manager/Admin; import link), `Alert` realtime tag, dashboard card "Unresolved alerts"; Part A: form dialogs reset from
inside the form (old hook 3/30 failed, fix 0/50), detail pages remount per path (already guaranteed), stale customer
field errors cleared, products table at 1280 px, smoke table `OK` first, mapping deactivation resolves its stock-sync
alert. Backend 825/825; frontend 338 tests; first-load set 969 kB raw (+0.9 kB; alerts page lazy, 10 kB); smoke 35/35
(throwaway) and on development by the user. The five-run series of the report was completed in 018b A1 (5 of 5 green).
Review: `docs/tasks/018-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Form-dialog reset | `useOpenCount(opening)` in the dialog component (advanced during render when `opening` becomes a different truthy value) + `<ResetFormOnOpen openCount>` as the last child of every dialog `<Form>` (layout effect → `resetFields()`); `clearOnDestroy` stays; `useResetFormOnOpen` removed | The dialog renders the closed state between two openings, its memoised content may not; the reset must run in the commit that gave the form its new `initialValues` | AntD major upgrade |
| Detail page and route parameter | `AppLayout` keys its `Suspense` by `location.pathname`: a detail page is remounted when its parameter changes (no previous `data`, no previous page state); pinned by one test per detail page | Already in place since 011; a second mechanism would duplicate it | The layout key changes |
| Radio-group names (review 018) | Every `Radio.Group` / `Segmented` gets an explicit, page-unique `name` | Under test every generated name is `test-id`, so separate controls became one native radio group (flaky dashboard test) | — |
| Optional-field API errors | A dialog clears the API errors of fields without a client rule before each submit (mapping dialog, customer dialog) | AntD does not re-validate fields without rules on submit, so a server error stayed | — |
| Wide tables | A table may fix its action column to the right and let history columns scroll, as long as what a row is and what can be done with it fit 1280 px (alerts) | Eleven alert columns cannot fit 992 px | — |
| Mapping deactivation race | `wasActive` is read before the transaction without a lock; two concurrent edits of one mapping can miss the resolve | Mapping edits are rare and manual; a manual retry still resolves | Mapping edits become automated |
| In-flight requests in tests | (018b Part A2) the test setup fails a test that ends with an unanswered request; one named opt-out | The gotcha caused three red series; a rule the suite enforces beats a manual pass | — |

## Task 018b result (reviewed 2026-10-03)

`GET /api/system-events` (Manager/Admin; `user.*` for an Admin only; `details` through a per-event-type allowlist;
`actorEmail` / `targetEmail` from one user lookup per page) and the Logs/Audit page `/logs` (filters, sort direction,
page and window in the address; one summary line per type in vi/en; trace by correlation id); Part A: 018's five-run
series completed, test guard against requests in flight. Backend 890/890, 19/19 compiling mutants killed; frontend 373
tests, 5 of 5 full runs green with the guard; smoke 37/37 on development; no migration, no new index (60,000-row
`EXPLAIN`). Doc deviation 18 confirmed. Review: `docs/tasks/018b-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Logs window | Default = `?since=<now − 24 h>` written to the address, **no end**; presets today / 7 / 30 days are whole local days; custom range wins | A fixed end would hide events newer than the first opening; a reload keeps the window | Users need a frozen window for paging through a busy period |
| Open-ended range | `from` without `to` is not bounded by the 92-day rule | The page never sends a far `since`; Manager/Admin only | 026 (`COUNT` over wide ranges) |
| Request guard in tests | Counted at the `fetch` call until its promise settles (wrapper over MSW's patched `fetch`), checked first in `afterEach`; one opt-out `allowUnansweredRequests(reason)`; tests end with `settled()` or the text of the last answer | MSW's `request:start` comes one macrotask (~19 ms) after the call, so a last-tick request would be held against the next test | MSW reports requests synchronously |
| Timing-dependent tests | Found by delaying every answer (120 ms); the switch becomes a committed opt-in in 019 A1 and the suite must be green with it | Red runs appeared only under load; the delay makes them deterministic | — |
| Successful orders | **No system event** for a reserved order; `SystemEventLog` records exceptions and audited manual actions, the normal flow is traced through the task and its attempts | Information already exists; one more statement in the reserve transaction for nothing new | Operators need one timeline of a normal order (then a view joining tasks and events) |
| Rejected messages | The text `reason` stays stored and hidden; a `reasonCode` (one constant per rejection path) is stored next to it and returned (019 A2) | The text can quote parser exceptions and values of the rejected message | — |
| `OutboxEvents.PublishFailed` | Event-type constant in Application, referenced by the publisher | The allowlist must reference constants; Application cannot reference Workers | — |

## Task 019 plan — product images in S3-compatible object storage (2026-10-03)

**Decision (user, 2026-10-03): no AWS** — it costs money; deployment should be free (Vercel or Railway, decided in 025).
The architecture's rules for images (doc §4.2, §14: no binary in PostgreSQL, key and metadata in the database, private
bucket, presigned URLs or backend authorization) stay; only the provider changes. Checked 2026-10-03: MinIO no longer
publishes community container images, and LocalStack's image requires an auth token since 03/2026 — neither is a free
local option any more.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Object storage | S3-compatible storage through the S3 API; `AWSSDK.S3` only as the client library (no AWS account), endpoint (`ServiceUrl`), path-style addressing, region, bucket and keys from configuration; development and integration tests use a **local emulator container** (dev only), chosen in 019 by a spike between Garage and SeaweedFS against fixed criteria | One code path for any provider; offline development; Claude Code can run the smoke without external accounts | 025 (production provider) |
| Images per product | One image per product (doc §11 `…/image`, singular): columns `ImageKey`, `ImageContentType`, `ImageSizeBytes`, `ImageUpdatedAt` on `Product`, all null or all set (check constraint) | No gallery requirement; a separate table adds a join to every product read | Several images are needed |
| Upload | Presigned **PUT** (5 min, `Content-Type` signed) to a server-generated key `products/{productId}/{uuidv7}`; the client then **confirms** with the key: the server checks the key's prefix, HEAD (exists, ≤ 5 MiB, declared type) and the first bytes (magic bytes of the declared type), then stores the metadata; a rejected object is deleted | A presigned PUT cannot enforce size or content; the confirm step keeps the server the authority; no image bytes pass through the API | Large files (020): presigned POST / multipart |
| Storage calls and transactions | HEAD / range GET before the database transaction; the replaced object is deleted **after** commit, best effort (warning on failure) | Never hold a transaction during an external call (same rule as channel APIs) | — |
| Allowed images | `image/jpeg`, `image/png`, `image/webp`; ≤ 5 MiB; no SVG, no GIF | SVG can carry script, and the storage origin serves what is stored | — |
| Orphans | Never-confirmed uploads and replaced objects whose delete failed stay in the bucket; never referenced, never served | No cleanup job at this volume | 026 (sweep, or a lifecycle rule of the production provider) |
| Download | Product responses carry `image { url, expiresAt, contentType, sizeBytes, updatedAt }` or null; `url` is a presigned GET (15 min) computed locally per response; the client reuses the URL it has for the same image version until shortly before `expiresAt` | Private bucket; `<img>` cannot send the bearer token; lists refetch on every inventory hint and a new URL each time would defeat the browser cache | A CDN or public-read image bucket |
| Browser ↔ storage | The browser talks to the storage endpoint directly (PUT, GET) with **no** `Authorization` header; the bucket's CORS allows the dev origins (5173, 4173) for `PUT`/`GET` with `Content-Type` | Presigned URLs are the credential; the API's token must never reach a third party | 025 (production origin) |
| Roles | Presign, confirm, remove: Manager/Admin (as product create/update); the image is readable by every role that reads products | Same as product writes | — |
| Secrets | Storage access key id and secret only in user-secrets / environment / `docker/.env`; never logged; presigned URLs never logged (credentials until they expire) | Secrets rules | — |
| Audit and realtime | No system event and no new hint type for image changes | Product updates write none today | Catalog audit is asked for |

### Deviations / additions to the architecture document
19. S3-compatible object storage instead of AWS S3 (provider by configuration; a local emulator in development) — user
    decision, cost.
20. `PUT /api/products/{id}/image` (confirm) and `DELETE /api/products/{id}/image` in addition to the doc's presign.
21. Image columns on `Product` (the ERD has none; doc §4.2: key and metadata in the database).

## Task 019 result (reviewed 2026-10-05)

Product images in S3-compatible object storage: `IObjectStorage` + S3 implementation (`AWSSDK.S3` 4.0.104.1 as client
library), `ObjectStorage` options, image columns on `Products` (migration `ProductImage`), presign / confirm / remove
endpoints, `image` (presigned GET) on every product response; thumbnails, upload / replace / remove in the product
dialog; emulator **SeaweedFS 4.48** chosen by the spike (Garage also met the criteria, with more setup); key helper
`scripts/setup-object-storage.ps1`; Part A: `npm run test:slow` (120 ms answer delay) with `renderLive` ending settled,
`reasonCode` of rejected messages (19 codes). Backend 1056/1056, 0 warnings; frontend 405 tests, 5 of 5 full runs green,
one full slow run green; smoke 50/50 on a throwaway environment; production-build browser check. Migration
`ProductImage` not yet applied to development (user's manual steps). Doc deviations 19–21 confirmed.
Review: `docs/tasks/019-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Emulator | SeaweedFS 4.48 (`weed mini`): bucket, key pair and CORS are container start-up settings — no config file, no init step; only the S3 port, on `127.0.0.1` | Fewest setup steps; works with the SDK's default request checksums | 025 (production provider) |
| Request checksums | SDK default kept; a provider that rejects them needs `WHEN_REQUIRED` (found with Garage) | The application only presigns, HEADs, reads a range and deletes | 025 |
| Confirm order | 404 → key form/prefix → same key as the current image (200, no storage call) → HEAD → size → type → first 12 bytes → transaction with the product row locked → delete the replaced object after commit | A client retry works while the storage is down; no storage call in a transaction | — |
| Concurrent confirmations | Serialised by the product row lock; every replaced object is deleted (not orphaned) | Measured by the test with three confirmations | — |
| Storage failures | `ObjectStorageException` → 503 `storage_unavailable`; one warning with operation and status / S3 code, never the exception text; one SDK retry, `RequestTimeout` per try | The SDK's exception text can carry the endpoint | — |
| Hosts without storage | `UnconfiguredObjectStorage` registered by `AddInfrastructure`, replaced by `AddObjectStorage` (API; Workers from 020) | Products without an image work everywhere; anything else fails loudly | — |
| Stable image link (client) | The link of the same image version (`productId` + `updatedAt`) is reused until 60 s before it expires; forgotten at logout | A new link per response would reload every image on each list refetch | — |
| Client size limit (review 019 q2) | Constant `MAX_IMAGE_BYTES` (5 MiB) for the early message only; raising `MaxImageBytes` requires changing it (README) | The server decides; a lower limit costs one presign 400 | The limit differs per deployment |
| Slow run (review 019 q3) | One green full `npm run test:slow` is required for frontend tasks with request tests; every test red in any slow run is fixed (or shown to be an application defect) before that run and listed in the report; the stability measure stays five consecutive normal runs | The delay finds timing dependence, but is not perfectly deterministic | — |
| Orphan key in logs (review 019 q4) | The warning of a failed delete names product id and object key | The key is not a credential and is what an operator needs to remove the orphan | — |
| Storage address (review 019 q1) | One `ServiceUrl` for hosts and browsers until 025; then an optional `PublicServiceUrl` with a second, presign-only client (SigV4 signs the host, a signed link cannot be rewritten) | `docker compose --profile app` cannot serve images to the host's browser meanwhile (README) | 025 |
| Upload link after confirm (review 019) | Accepted until 026: the link can replace the confirmed object until it expires (Manager/Admin only) | Fix = copy to a final key at confirmation; signed `Content-Length` depends on the provider | 026 |

## Task 020 plan — Excel export in the background (2026-10-05)

**Decisions (user, 2026-10-05):** every export runs **in the background** — no synchronous path, also for small
results; only **Manager/Admin** may export; files are kept **7 days**.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Export mode | Always background (user): the API writes `ProcessingTask(ReportExport)` + `ReportExports` row + `OutboxEvent(report.export.requested)` + `SystemEventLog` in one transaction and answers 202 `{ taskId }`; Outbox Publisher → queue `report.export` → Report Export Worker. Alternative considered: stream small results from the API and queue only large ones — two code paths, a threshold, ClosedXML memory in the API | One path that is traceable (task, attempts, worker instance), retried, dead-lettered, manually retryable, alerted and pushed to the UI (`processing.task.changed`) with existing machinery; the API does no heavy work | Users need instant small exports |
| Data model | `ReportExports`: `TaskId` PK/FK (1:1 with the task, no separate id), `ReportType`, `Parameters` jsonb (normalised filters, `timeZone`, `language`), `FileName`, `ExpiresAt`; result `ObjectKey`, `SizeBytes`, `RowCount`, `CompletedAt` (all or none); `DeletedAt`; audit fields (`CreatedBy` = requester). Index `(CreatedBy, CreatedAt DESC)`, partial `(ExpiresAt) WHERE DeletedAt IS NULL` | Status lives in the task (one source); the row holds what the task cannot: parameters, result, retention | — |
| Message | Payload `{ taskId }` only; the worker reads the parameters from the database | PostgreSQL is the source of truth; a retry copy can never carry stale parameters | — |
| Object key | `exports/{taskId}.xlsx`, deterministic | A redelivery after upload and before commit overwrites the same object; the cleanup can delete a failed export's file without a stored key — no orphans from crashes | — |
| Worker steps | terminal → ACK; attempt committed first; expired → permanent failure; read in one `REPEATABLE READ READ ONLY` transaction, streamed, building the workbook; upload after that transaction; one transaction completes export + attempt + task and resolves the `ProcessingFailed` alert; ACK | No external call inside a transaction; orders and items from one snapshot | — |
| Failures | Storage and transient database errors → delay tiers of 007 (`MaxAttempts` 5); rows above the limit at the worker and an expired export → permanent failure; final failure as every worker (task/attempt `Failed`, `processing.failed`, `ProcessingFailed` alert, DLQ); manual retry unchanged | Same contract as order and stock-sync processing | — |
| Limits | `Reports:MaxRows` 100,000 (orders + item lines, or products) checked by a `COUNT` at the request (400 `export_too_large`, nothing written) and by the worker; orders need `from`/`to`, range ≤ 366 days; `MaxUnfinishedPerUser` 3 (409 `too_many_exports`, check-then-insert accepted) | ClosedXML builds the workbook in memory (measured in 020); one user cannot fill the queue | Measurement shows a different safe limit |
| Access | Request: Manager/Admin (user). List, detail, download: the requester only; another user's export is 404, Admins included | Bulk personal data (customer names, phones); exports are reproducible, so nobody needs another user's file | Support needs to see others' exports |
| Download | `GET /api/reports/exports/{taskId}/download` → `{ url, expiresAt, fileName }`: presigned GET, 5 min; file stored with `Content-Disposition: attachment` (or the header set on the presigned GET if the provider ignores the stored one); not audited | `<a href>` cannot send the API token; the link is used at once | — |
| Retention | 7 days (user): `ExpiresAt = CreatedAt + Retention`; `ReportExportCleanupService` in the Workers host (hourly, batches): delete the object of terminal exports past `ExpiresAt`, then set `DeletedAt`; status `Expired`. Alternative: a provider lifecycle rule — not available the same way in the emulator and every provider | Exports accumulate with use and contain personal data; unlike image orphans they need a sweep | 025 (provider lifecycle rule could replace the sweep) |
| Workbook | ClosedXML (pinned); orders: sheets *Orders* + *Items* + *Info*; inventory: one sheet + *Info*; customer name and phone only (no e-mail, address); typed cells, money `#,##0`, timestamps converted to the request's IANA time zone (named in the header), header bold / frozen / auto-filter, fixed widths; texts never written as formulas; headers and labels in the request's language (vi/en) | Excel has no time zone; formula injection; minimum personal data | — |
| Filters | The filters of `GET /api/orders` and `GET /api/inventory`, reusing their code | An export contains what the page with the same filters shows | — |
| Storage in Workers | `AddObjectStorage` in the Workers host; `IObjectStorage.PutAsync` added; keys through the Workers user-secrets (setup script) | One abstraction for both hosts | — |
| Audit and realtime | `report.export_requested` (Information, `reportType`, `requestedBy`) in the request transaction, allowlisted; no new hint type | Bulk data extraction is an audited action; task status changes already notify | — |
| Frontend | None in 020; the Sync & Processing and Logs/Audit pages show `ReportExport` and the new event type raw until 021 | Both pages already fall back to raw values | — |

### Deviations / additions to the architecture document
22. `ReportExports` table (the ERD has none).
23. Task type `ReportExport`, event type `report.export.requested`, queue `report.export` (+ `.dlq`) — not in the doc's
    §10 topology.
24. Every export is a background task (doc §13 names it for large exports): the two `POST …/export` endpoints answer
    202 with a task; added `GET /api/reports/exports`, `GET /api/reports/exports/{taskId}`,
    `GET /api/reports/exports/{taskId}/download`.
25. Export files are deleted after 7 days (the doc names no retention).

## Task 020 result (reviewed 2026-10-05)

Excel export, always in the background: `POST /api/reports/{orders|inventory}/export` (Manager/Admin; task + export row
+ outbox event + audit event in one transaction, 202), Report Export Worker on queue `report.export` (snapshot read in
`REPEATABLE READ, READ ONLY`, workbook with ClosedXML 0.105.1, upload of `exports/{taskId}.xlsx` after that
transaction, completion in one transaction), list / detail / download link for the requester only, retention sweep in
the Workers host (7 days), object storage in the Workers host (`PutAsync`); Part A: README provider notes, `127.0.0.1`
dev origins in the emulator's CORS. Backend 1278/1278, 0 warnings, 22 of 22 mutants killed; smoke 46/46 on a throwaway
environment; peak working set at `MaxRows` 255 MiB (orders) / 336 MiB (inventory); log scan clean. Migration
`ReportExports` not yet applied to development (user's manual steps). Doc deviations 22–25 confirmed (23 with the
system event type `report_export.message_rejected`). Review: `docs/tasks/020-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Stored attachment header | The object is stored with `Content-Disposition: attachment; filename="…"` (ASCII name); the presigned GET adds nothing. Fallback if a provider does not return the stored header: `response-content-disposition` on the presigned GET (works with the emulator too) | Checked with the emulator in 020 | 025 (provider check) |
| Formula-safe text | Text only through `Value`, never `FormulaA1`; a leading apostrophe is doubled (ClosedXML drops one as Excel's text mark) | Formula injection; names keep their apostrophe | ClosedXML major upgrade |
| Row limit at the worker | Counted while streaming, checked before each row (stops at `MaxRows + 1`) | No extra `COUNT`; memory stays bounded when the data grew | — |
| Export alerts (review 020 q1) | Final failures of exports open `ProcessingFailed` like every task; **the sweep resolves the alert of each export it expires, in the transaction that sets `DeletedAt`**; a manual retry of an export that is deleted or past `ExpiresAt` is refused with 409 `export_expired` (021 A1) | One alert rule for every task type (`export_parameters_invalid` is a deployment signal); an alert with nothing left to act on must end; a retry of an expired export can only fail and would open an alert nobody resolves | — |
| Expiry in the API (review 020 q2) | A terminal export past `ExpiresAt` is `Expired` in list and detail, and the download answers 409 `export_expired` past `ExpiresAt`, whether or not the sweep has run (021 A2) | Retention is a promise about personal data; the API agrees with the worker | — |
| `SixLabors.Fonts` (review 020 q3) | Pinned centrally at 1.0.0 (Apache-2.0) through transitive pinning (021 A3); 2.x is under the Six Labors Split License | A transitive bump must fail the restore, not change the licence silently | ClosedXML requires ≥ 2.0 (then decide on the licence) |
| Upload timeout (review 020 q4) | `ObjectStorage:RequestTimeout` (10 s) also bounds an export upload; 025 measures a `MaxRows` upload against the production provider and adds a Workers-only `ObjectStorage:UploadTimeout` if it needs more than half of it | ~5 MiB at `MaxRows`; images need the short timeout | 025 |
| Unfinished limit during an outage (review 020 q5) | Accepted: exports waiting while the Workers host is down count toward `MaxUnfinishedPerUser`; manual retries are not counted | The limit exists to protect a queue that does not drain; the list shows the waiting exports | Users complain about blocked exports |
| Time zone ids (review 020 q6) | Contract: an IANA id; the hosts accept what they resolve (also Windows names through ICU); the frontend always sends the browser's IANA zone | Harmless while API and Workers share an image; a mismatch fails cleanly as `export_parameters_invalid` | API and Workers on different platforms |
| Dashboard `processing` (review 020 q7) | Lists `ReportExport` like every task type; labelled in 021 | A failed export is a failed task | — |

## Task 021 plan — Reports & Excel export page (2026-10-05)

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Page | `/reports` ("Báo cáo & Excel", doc §3.2), Manager/Admin only (`REPORTS_ROLES` next to its paths, like `LOGS_ROLES`): menu item hidden for Staff, the address answers with the forbidden page; lazy chunk | The API is Manager/Admin; same pattern as Logs/Audit | — |
| Content | A request card (Orders / Inventory) above the table "My exports"; **no charts** | The dashboard is the overview; the doc names no report beyond the export | Users ask for report views |
| Orders request | Filters of `GET /api/orders`: channel, status, customer, search; a **required** range of local days (default: the last 30 days including today; at most 366 days — constant `MAX_EXPORT_RANGE_DAYS`, the server stays the authority like `MAX_IMAGE_BYTES`), sent as instants with `dateRange.ts` | Same meaning as the list; the API requires the range | `Reports:MaxRangeDays` changes |
| Inventory request | Filters of `GET /api/inventory`: search, low stock only | Same meaning as the list | — |
| Time zone and language | `timeZone` = `Intl.DateTimeFormat().resolvedOptions().timeZone` (IANA), shown read-only on the form; the file language is the UI language (the `Accept-Language` header the client already sends), shown as help text | The file must match what the user sees; no extra choice to make | Users need another zone or language |
| Errors of a request | Field errors (range, search) on the fields; `export_too_large`, `too_many_exports` and errors without a field (e.g. `timeZone`) as a form-level message | 020 deviation 3: these codes belong to no field | — |
| Shortcut from the lists | Orders and Inventory pages get an "Export to Excel" button (Manager/Admin) that opens `/reports` with the list's current filters in **router location state** (not the address); they prefill the form; nothing is requested until the user submits | Lists and exports share their filters (020); exporting stays a deliberate action; the address of `/reports` belongs to the exports table | — |
| Exports table | `GET /api/reports/exports`; page, page size, sort direction (by request time) and `reportType` filter in the address (`useListQueryState`); columns: requested at, report, filters (summary from `parameters`), status, rows, size, kept until, actions | — | — |
| Live status | New realtime tag type `ReportExport`: `processing.task.changed` with `taskType: ReportExport` → `{ReportExport, taskId}` + `{ReportExport, LIST}` (as well as today's tags); `all` → `ReportExport`; part of the resync set. While the connection is not live and an unfinished export is on the page: poll the list every 5 s (the order-outcome pattern of 016) | Hints carry `taskType`, so other task changes do not refetch the exports | — |
| Download | The link is fetched by a **mutation** (`GET …/download`, not cached), followed at once through a temporary `<a rel="noreferrer">`; the link is never kept in the store, the address or a log; 409 `export_expired` / `export_not_ready` → localised message and a refetch of the table | The link is a credential (5 min); the stored `Content-Disposition` makes the browser save the file without leaving the page | — |
| Failed and expired exports | Failed: error-code label (known codes, raw fallback) and a link to the task detail, where retry lives (doc §3.4); Expired: grey tag, no download | One retry path for every task | — |
| Labels elsewhere | `ReportExport` in `TASK_TYPES` (filter, column, task detail, dashboard); event types `report.export_requested` (report type, requester) and `report_export.message_rejected` (`reasonCode`) on the Logs/Audit page | Raw names since 020 | — |
| Expired image links (review 019) | A thumbnail whose image fails to load **after its `expiresAt`** asks for one refetch of the product list; requests of one burst are coalesced; a failure before `expiresAt` refetches nothing (no loop) | Failure-driven, no timers; self-healing with one request | — |
| Development steps (user, 2026-10-05) | Claude Code applies the pending steps of 019/020 first (A0) and the steps of every later task itself; smoke and browser checks run on development | User decision: no manual steps after a task | A step fails or would be destructive (then it stays with the user) |

## Task 021 result (reviewed 2026-10-05)

Reports & Excel export page `/reports` (Manager/Admin): request forms for orders / inventory with the lists' filters,
the browser's IANA time zone and the UI language; "My exports" with live status (tag `ReportExport`, polling only while
not live and an export is unfinished) and a download whose link is kept nowhere; "Export to Excel" on the Orders and
Inventory pages (filters in router location state); labels for `ReportExport` and both export event types. Part A:
the sweep resolves the alert of an export it expires (one transaction), a retry of an expired export → 409
`export_expired`, `Expired` by time in the API (`ReportExportExpiry`), `SixLabors.Fonts` pinned at 1.0.0 (transitive
pinning, 0 resolved-version differences), smoke 020 step 17, expired image links refreshed once. A0: migrations
`ProductImage` and `ReportExports` applied to development by Claude Code as the migrator. Backend 1300/1300, 0 warnings;
frontend 452 tests, 5 of 5 full runs green, one full slow run green (third attempt); first-load set 971.00 kB raw
(+0.86 kB), `ReportsPage` lazy (12.8 kB); smoke 019 50/50, 020 46/46, 021 25/25 on development; production-build
browser check. Review: `docs/tasks/021-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Client copies of server limits (review 021 q3) | Constants next to their use, commented and named in the README beside the server setting (`MAX_IMAGE_BYTES`, `MAX_EXPORT_RANGE_DAYS`); the server stays the authority | Two copies are cheaper than an endpoint with its own authorization, caching and tests | A third copy, or a limit that differs per deployment → read-only `GET /api/config/client-limits` (026) |
| Placeholder pages (review 021 q2) | Removed in 022 A1: every menu item has a built page; no `ComingSoon` | Dead code since 021 | — |
| Time zone label (review 021 q4) | The browser's IANA zone as the OS names it (`Asia/Bangkok` and `Asia/Ho_Chi_Minh` are both correct) | It is the zone the file uses | — |
| Download save check (review 021 q1) | Accepted on evidence (stored `Content-Disposition: attachment`, anchor without `target`); the click is an optional user check | A download in the Browser pane needs the user's OK | 025 provider check |
| Slow-run discipline (review 021) | Slow runs with the Browser pane closed and the machine idle; a red test is not attributed to load without a second run of that file alone; avoid role-and-name queries over large tables inside `waitFor` (022 A3) | Six reds of 021's second slow run were explained, not investigated | — |
| Export filter summary | Dates of a stored range shown in the viewer's zone, not the export's `timeZone` | Only the requester sees an export | Exports become shareable |

## Task 022 plan — Reconciliation Worker (2026-10-05)

**Decisions (user, 2026-10-05):** the simulated channels keep their channel-side stock **in a PostgreSQL table**;
reconciliation runs **periodically only** (no "run now").

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Channel-side state | `IChannelStockClient.ReadAvailableStockAsync` (batch ≤ 100, `null` = no value on the channel), through the same per-channel timeout + circuit breaker as pushes; the simulator stores accepted pushes in `SimulatedChannelStocks` (keyed by channel slug + `id:`/`sku:` external key, no FK, no audit fields, last write wins) and answers reads from it. Alternatives (user chose): in-memory state (lost on restart, differs per Workers instance → false drift), no channel read (compare with the last pushed value — misses drift made on the channel side, deviates from doc §8.5) | Doc §8.5 reads the channel's state; a real adapter implements the same read; persisted state survives restarts and is the same for every instance | A real channel is integrated (the table then stays empty) |
| No backfill | The table starts empty; the first run after the migration creates one stock-sync task per Active mapping (bounded by `MaxTasksPerRun`) | A channel that never stored a value is drifted; faking "in sync" would hide it | — |
| Demo drift | Development SKU rule `SIM-LOST-`: push accepted, not stored | Deterministic drift for the smoke and demos, same mechanism as `SIM-PERM-` / `SIM-TEMP-` | — |
| Trigger | `ReconciliationService` (Workers host) every `Reconciliation:Interval` (15 min; Development 1 min), first round after `min(Interval, 60 s)` | User decision; the API never talks to channels | Operators need an on-demand run |
| One run at a time | `pg_try_advisory_lock` on a dedicated connection for the run; not acquired → skip | Several Workers instances; PostgreSQL only, no new table or infrastructure | — |
| Candidates | Active mappings of Active channels with a stock-sync queue, per channel, keyset batches; skipped: pairs with an unfinished `StockSync` task, and pairs whose most recently finished task `Failed` with `channel_rejected`. A final failure with a transient code is **not** skipped | An unfinished task pushes the current value anyway; a rejected pair would fail every round (and the mapping change or a manual retry is the fix); transient final failures are exactly the outages reconciliation must heal (log 014) | Channels reject transiently-looking errors |
| Drift | Reading `null` or ≠ central `AvailableStock` read before the channel read; races accepted (a false drift costs one push of the current value, a missed one is found next run) | No lock or transaction across an external call | — |
| Action | Per batch one transaction: new `StockSync` task + directed `stock.sync.requested` (`StockSyncRequests.NewTask`), key `reconcile:{runId}:{channelId}:{productId}`, correlation id `reconciliation-{runId:N}`; at most `MaxTasksPerRun` (500) per run, the rest deferred | Reuses the sync path, its retries, alerts and UI; a run is traceable on Sync & Processing and Logs/Audit | — |
| Source of truth | A run writes only `ProcessingTasks`, `OutboxEvents` and one `SystemEventLogs` row; never stock, products, mappings, orders, movements, alerts | Doc §8.5: no business rule allows changing the source of truth from channel values | A business rule for channel-side sales appears |
| Unreadable channel | A failed read ends that channel for the run; the others go on; reported in the event | One failing channel must not stop the others (doc §1.2) | — |
| Audit | `reconciliation.completed` (Warning) only when a run created a task or could not read a channel; counts and unreadable channels allowlisted; a clean run writes nothing (Serilog line only) | `SystemEventLog` records exceptions, not the normal flow (log 018b) | — |
| Frontend | Only the Logs/Audit label of the new event (022 C5); two full `npm test` runs + one slow run instead of five (no new page or async behaviour) | The event is how an operator sees drift | — |

### Deviations / additions to the architecture document
26. `SimulatedChannelStocks` table — state of the simulated external channels, not business data (user decision
    2026-10-05: no real channel accounts; doc §8.5 needs a readable channel state).
27. `IChannelStockClient` gains a batch read of the channel's available stock.
28. System event type `reconciliation.completed`; reconciliation runs carry the correlation id `reconciliation-{runId}`.

## Task 022 result (reviewed 2026-10-05)

Reconciliation Worker: `ReconciliationService` (Workers host, every `Reconciliation:Interval`, 1 min in Development)
runs `StockReconciliation` under a PostgreSQL advisory lock; candidates per channel in keyset pages (one raw query with
the unfinished / last-finished facts per pair), a batch read of the channel through the resilient client, a task +
directed `stock.sync.requested` per drifted pair (correlation id `reconciliation-{runId}`, cap per run), one
`reconciliation.completed` event when a run created a task or could not read a channel; the simulated channels keep
accepted pushes in `SimulatedChannelStocks` (migration, plain Npgsql on their own connections), `SIM-LOST-` for
permanent drift; Logs/Audit label. Part A: placeholder page removed (menu pages typed by the menu's paths), README §4,
load-sensitive test pattern. Backend 1396/1396, 0 warnings, 23 of 23 mutants killed; candidate page 6 ms at 1,000
mappings / 50,000 tasks (no new index); frontend 454 tests, 2 of 2 full runs and the slow run green at the first
attempt; first-load set 969.55 kB raw (−1.45 kB); migration applied on development as the migrator; smoke 23/23 on
development. Doc deviations 26–28 confirmed. Review: `docs/tasks/022-*.review.md`.

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Failure isolation of a run (review 022 q2) | Any exception of a channel read except host shutdown makes that channel unreadable for the run (`unexpected`, Error log with the exception); a database failure of the run's own queries ends the run (023 A1) | One failing channel must not stop the others (doc §1.2) | — |
| Failed pairs already in sync (review 022) | A pair whose newest finished task `Failed` with a transient code gets one task even without drift (counted `failedRetried`), so its `StockSyncFailed` alert ends through the existing success path (023 A2) | The alert ends only with a `Succeeded` task; reconciliation is the promised recovery path (log 014); the run still writes no alert | — |
| External SKU per channel (review 022 q4) | Unique among a channel's mappings without an external product id (partial unique index, 409 `external_sku_taken`) (023 A3) | Two products writing one channel item is wrong on any channel; the simulator made it visible | Channels that key by SKU and product id together |
| Interrupted run (review 022 q1) | No event; its tasks carry the run's correlation id, the failure is in the log, the next run reports what is still drifted | A best-effort event fails for the same reason as the run | — |
| Event contents | `unreadable[].channel` = channel name (snapshot); the run reads on after the cap to count `deferred` exactly | The Logs line needs no lookup; every run reads every pair anyway | — |
| Trusting unfinished tasks | A pair with a non-terminal stock-sync task is skipped; a task that can never end blocks its pair | Accepted for now | 026: detection of stale non-terminal tasks |
| Reconciliation in tests | Each reconciliation test uses its own database; test hosts run with `Reconciliation:Enabled=false` unless a test asks for it | A run looks at every mapping | — |

## Task 023 plan — webhooks (2026-10-05)

**Decision (user, 2026-10-05):** a webhook carries the **channel's own identifiers**: items by external product id /
external SKU (resolved through the channel's Active mappings), the customer by the channel's external customer id
(resolved through a new `ChannelCustomers` mapping; a customer is created on first sight). The normalised order then
goes through the existing order intake (doc §8.1 "Normalize → Common Order DTO"). Alternatives: the order-entry body
with our GUIDs (no normalisation, mappings unused); channel product ids with our customer GUID (a channel cannot know it).

| Topic | Decision | Reason | Revisit when |
| --- | --- | --- | --- |
| Authentication | `POST /api/webhooks/{slug}` is anonymous to JWT; the caller proves itself with **HMAC-SHA256** over `"{timestamp}.{raw body}"` with the channel's secret: headers `X-Webhook-Timestamp` (Unix seconds) and `X-Webhook-Signature: v1=<hex>` (several comma-separated `v1=` values allowed); constant-time comparison; timestamp within ±5 min of the API clock | Doc §6 "verify signature/secret"; the scheme of common webhook providers; the timestamp bounds replays, the order idempotency makes a replay inside the window a duplicate | A real channel prescribes its own scheme (then one verifier per channel kind) |
| Secrets | Per channel slug in configuration (`Webhooks:Channels:{slug}:Secrets`, one or two for rotation, ≥ 32 characters), from user-secrets / environment only; never logged; a slug without a secret has no webhook (404 like an unknown channel). Key helper `scripts/setup-webhook-secrets.ps1` | Doc §6 secrets in environment/secret manager; no secret in the database; rotation without downtime | Channels are created at runtime and need webhooks (then a secret store) |
| Identity of a webhook | After a valid signature the request acts as `webhook:{slug}` (audit fields, `CreatedBy` of task and outbox row), with **no role**: it can reach nothing but this endpoint | Audit must say who wrote; a webhook is not staff | — |
| Rate limit | Fixed window per (client IP, slug), 120 per minute, 429; applied before anything else; body limit 64 KiB (413) | Doc §6 rate limit for public/webhook endpoints; IP alone would let one sender exhaust every channel | Behind a proxy: ForwardedHeaders (025) |
| Body | `{ externalOrderId, customer { externalCustomerId, name, phone?, email? }, items [ { externalProductId? \| externalSku?, quantity } ] }`; exactly one identifier per item; same limits as the order intake (128-character order id, ≤ 100 items, quantity 1–10,000) | A channel speaks in its own ids and words; our limits stay one set | — |
| Item resolution | The key rule of the simulator: an item names a mapping by its external product id, or — for a mapping without one — by its external SKU; the mapping and its channel must be Active; anything else is a 400 on the item | One rule for "how a channel knows a product" (`SimulatedChannelStock.ExternalKeyOf`); unique since 023 A3 | — |
| Customer resolution | `ChannelCustomers` (`ChannelId`, `ExternalCustomerId` unique per channel → `CustomerId`); first sight creates the customer (name, phone, email from the payload) and the mapping in one short transaction before the intake (race: unique index → re-read); later webhooks never change the customer | Deterministic and idempotent; no matching by phone (optional, not unique, free format); our edits are not overwritten | Channels send customer updates |
| Order of steps | rate limit → body size → channel + secret → signature + timestamp → JSON + validation → items → customer → intake. A customer created for an order the intake then refuses (409) stays | Nothing is read from the body before it is authenticated; the customer is real either way | — |
| Responses | 202 with the intake's `SubmitOrderResponse` (duplicate included); 400 (camelCase keys, localised), 401 `webhook_signature_invalid` / `webhook_timestamp_invalid`, 404 `channel_not_found`, 409 `order_conflict`, 413, 429 | One contract with the order intake; distinct 401 codes help an integrator and reveal nothing | — |
| Audit and logs | No new system event; an accepted webhook is traced by its task (actor, correlation id from `X-Correlation-Id` when valid); a rejected one is a Warning log (slug, reason code, client IP) — never the body, signature or secret | The task already is the audit of an accepted order; rejections are unauthenticated noise | Operators need webhook failures on the dashboard |
| Frontend | None for webhooks (023 changes only the mapping dialog and the Logs line of Part A) | The Order Entry page stays the demo input; `scripts/send-webhook.ps1` signs and sends a webhook for demos | — |

### Deviations / additions to the architecture document
29. Webhook contract: HMAC-SHA256 signature headers, timestamp window, channel-shaped body (doc §11 names only the route).
30. `ChannelCustomers` table (the ERD has no customer mapping per channel).
31. Webhook secrets per channel in configuration; the webhook acts as `webhook:{slug}` without a role.

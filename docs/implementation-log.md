# Implementation log

Decisions taken while implementing the architecture document, in order of the implementation plan (section 15).
Anything listed under "Deviations" differs from, or adds to, the architecture document and should be confirmed.

## Working model

- Planning and review: Claude Cowork. Implementation, build, test: Claude Code (desktop Code tab or VS Code).
- Handoff through `docs/tasks/` (see `docs/tasks/README.md`): Cowork writes `NNN-*.md`, Claude Code runs `/next-task`
  and writes `NNN-*.report.md`. Only results in a report count as verified.
- `.claude/settings.json` pre-approves read-only/build commands and denies reading `.env` files.

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

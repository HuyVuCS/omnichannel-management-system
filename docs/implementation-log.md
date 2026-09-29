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
- **StockSync per channel (step 8):** `ProcessingTask` has no `ChannelId` yet; add it when stock-sync tasks are introduced.
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

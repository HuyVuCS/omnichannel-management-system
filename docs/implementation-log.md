# Implementation log

Decisions taken while implementing the architecture document, in order of the implementation plan (section 15).
Anything listed under "Deviations" differs from, or adds to, the architecture document and should be confirmed.

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

- **Flow 8.1 vs. reliability (step 5):** the document publishes `order.created` from the API straight to RabbitMQ before anything is persisted. If the publish fails after the API has answered, the order is lost. Proposal: persist the incoming order request (or an outbox row) in PostgreSQL first and publish through the Outbox Publisher. Needs a decision before step 5.
- **Reservation SQL (step 7):** use one conditional `UPDATE ... SET "ReservedStock" = "ReservedStock" + @qty WHERE "ProductId" = @id AND "AvailableStock" >= @qty` per item inside the transaction; raw SQL bypasses the audit interceptor, so set `UpdatedAt/UpdatedBy` explicitly. Do not enable `EnableRetryOnFailure` without wrapping the transaction in the execution strategy.
- **StockSync per channel (step 8):** `ProcessingTask` has no `ChannelId` yet; add it when stock-sync tasks are introduced.
- **Integration tests:** concurrency/oversell and idempotency tests need a real PostgreSQL (Testcontainers or the compose database). Not added yet.
- **User management:** only the bootstrap admin exists; endpoints to create Manager/Staff users are not in the API contract yet.

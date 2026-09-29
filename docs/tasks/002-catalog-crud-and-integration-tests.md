# Task 002 — Step 4a: Channel / Customer / Product CRUD, inventory list, integration-test infrastructure

Status: Open
Written by: Cowork, 2026-09-29
Depends on: 001 (done)

## Context
Implementation plan step 4 ("CRUD Channel/Product/Customer/Inventory"). Stock *changes* (import, movements, outbox)
are task 003; order intake is task 004. Read `docs/implementation-log.md` → section "Steps 4–5" for the decisions
this task must follow (service placement, paging contract, error mapping, test stack).

## Part A — Housekeeping (do first, separate commit)
1. `backend/src/Omnichannel.Api/Omnichannel.Api.http`: replace the literal email/password with
   `{{$dotenv BOOTSTRAP_ADMIN_EMAIL}}` / `{{$dotenv BOOTSTRAP_ADMIN_PASSWORD}}` and add a comment that the values live in
   `backend/src/Omnichannel.Api/.env` (git-ignored). **Never commit a real password.** Do not read or create that `.env`.
2. `README.md` section 4: the `InitialCreate` migration now exists; keep only the `database update` part.
   Add one line: integration tests need Docker; `dotnet test --filter "Category!=Integration"` runs unit tests only.
3. `backend/Directory.Build.props`: `TreatWarningsAsErrors` → `true`, remove the TODO comment.

## Part B — Integration-test infrastructure
New test-only packages (allowed): `Testcontainers.PostgreSql` (latest 4.x), `Microsoft.AspNetCore.Mvc.Testing` (10.0.x,
same patch as the other Microsoft packages). Add them to `Directory.Packages.props`.

- `Program.cs`: append `public partial class Program;` so `WebApplicationFactory<Program>` can see it. No other change.
- `Omnichannel.Tests/Integration/`:
  - `PostgresFixture`/`ApiFactory` shared through **one xUnit collection** (`[CollectionDefinition("api")]`):
    starts `postgres:17` via Testcontainers, applies migrations with `Database.MigrateAsync()`, and builds **one**
    `WebApplicationFactory<Program>` overriding `ConnectionStrings:Default` and `Jwt:*` (test key ≥ 32 bytes),
    no `BootstrapAdmin`. One factory per test run is required: Serilog's `CreateBootstrapLogger()` throws
    "logger is already frozen" when a second host is built in the same process.
  - Helper to create an `HttpClient` authenticated as a role: issue the token with the app's own `IAccessTokenIssuer`
    (random user id). Endpoints in this task do not need the user to exist in Identity.
  - Tests are independent: use unique data (random SKU/slug suffix) instead of cleaning the database.
  - Mark all integration tests `[Trait("Category", "Integration")]`.

## Part C — Features

Placement (see implementation-log): contracts, validators and service interfaces in
`Omnichannel.Application/<Feature>/`; EF implementations in `Omnichannel.Infrastructure/Persistence/<Feature>/`;
thin controllers in `Omnichannel.Api/Controllers/`. Domain mutations as methods on the entities.

### Shared
- `Application/Common/Paging`: `PageRequest`-style query base (`page` ≥ 1 default 1, `pageSize` 1–100 default 20,
  `search` ≤ 100 chars, `sortBy` from a per-endpoint whitelist, `sortDir` `asc|desc` default `asc`) + validator;
  `PagedResult<T>(items, page, pageSize, totalCount)`.
- Text search: `EF.Functions.ILike` with `%`, `_` and `\` escaped in user input.
- `Infrastructure/Persistence/DbUpdateExceptionExtensions`: detect `PostgresException` with
  `SqlState == PostgresErrorCodes.UniqueViolation` and a given `ConstraintName`; services translate it to
  `ConflictException(errorCode, message)`. Never check-then-insert as the only uniqueness guard.

### Channels — `Application/Channels`
| Method | Route | Policy | Notes |
| --- | --- | --- | --- |
| GET | `/api/channels` | AnyStaff | all channels (no paging, small table) |
| GET | `/api/channels/{id}` | AnyStaff | 404 if missing |
| POST | `/api/channels` | AdminOnly | `{name, slug, type}` → 201 + Location. Slug `^[a-z0-9]+(-[a-z0-9]+)*$`, ≤ 64. Duplicate slug → 409 `channel_slug_exists` |
| PUT | `/api/channels/{id}` | AdminOnly | `{name, status}`; slug and type are immutable |

### Customers — `Application/Customers`
| Method | Route | Policy | Notes |
| --- | --- | --- | --- |
| GET | `/api/customers` | AnyStaff | paged; search name/phone/email; sortBy `name`,`createdAt` |
| GET | `/api/customers/{id}` | AnyStaff | |
| POST | `/api/customers` | AnyStaff | `{name, phone?, email?, address?}`; email format; phone ≤ 32 of `[0-9+\-() ]` |
| PUT | `/api/customers/{id}` | AnyStaff | same fields |

### Products — `Application/Products`
| Method | Route | Policy | Notes |
| --- | --- | --- | --- |
| GET | `/api/products` | AnyStaff | paged; search sku/name; sortBy `sku`,`name`,`price`,`createdAt`; each item includes `availableStock` (order-entry UI needs it) |
| GET | `/api/products/{id}` | AnyStaff | includes total/reserved/available stock |
| POST | `/api/products` | ManagerOrAdmin | `{sku, name, price, lowStockThreshold, expiryDate?}` → creates Product **and** `Inventory(ProductId, 0)` in the same `SaveChanges`. SKU normalised upper-case; duplicate → 409 `product_sku_exists` |
| PUT | `/api/products/{id}` | ManagerOrAdmin | `{name, price, lowStockThreshold, expiryDate?}`; SKU immutable |

Stock is **never** set through product endpoints; it changes only through inventory movements (task 003).

### Inventory (read-only in this task) — `Application/Inventory`
| Method | Route | Policy | Notes |
| --- | --- | --- | --- |
| GET | `/api/inventory` | AnyStaff | paged; search sku/name; `lowStockOnly` bool; sortBy `sku`,`name`,`availableStock`,`updatedAt`. Item: `productId, sku, name, totalStock, reservedStock, availableStock, lowStockThreshold, isLowStock, updatedAt`; `isLowStock = availableStock <= lowStockThreshold` computed in SQL |

No DELETE endpoints: rows are referenced by orders/movements. Deactivation exists only for channels (status).

## Tests (required)
Integration (`Category=Integration`):
1. Manager creates a product → 201; inventory row exists with 0/0/0.
2. Duplicate SKU differing only in case → 409 `product_sku_exists`.
3. 5 concurrent POSTs with the same SKU → exactly one 201, four 409, one product row.
4. Staff `POST /api/products` → 403; anonymous `GET /api/products` → 401.
5. `GET /api/inventory?lowStockOnly=true` returns a product with threshold 5 and stock 0, not one with threshold 0.
6. Channel: duplicate slug → 409; invalid slug `Web Site` → 400; Staff create → 403.
7. Customer search for `50%` matches only a name that literally contains `50%`.
8. `pageSize=101` → 400; `sortBy=unknown` → 400 (ProblemDetails with `errorCode=validation_failed`).
Unit: validators (paging, product, channel, customer) and the new entity `Update` methods' guards.

## Acceptance criteria
- [ ] `dotnet build` 0 errors, 0 warnings with `TreatWarningsAsErrors=true`.
- [ ] `dotnet test` — all pass (unit + integration; Docker running). Report counts separately for each category.
- [ ] No migration needed. If you believe one is, stop and report (`Blocked`).
- [ ] Controllers contain no business logic (only binding, service call, result mapping).
- [ ] `Omnichannel.Api.http` contains no credential; README updated.

## Out of scope
Inventory import/movements/outbox (003), order intake (004), ChannelProduct mapping endpoints (step 8), S3 images
(step 12), deletes, soft-delete, user management.

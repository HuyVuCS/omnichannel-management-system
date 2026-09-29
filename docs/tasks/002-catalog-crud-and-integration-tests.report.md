# Report 002 — Step 4a: Channel / Customer / Product CRUD, inventory list, integration-test infrastructure

Status: Done
Commit: daf481d (Part A housekeeping), b16c026 (Parts B + C)

## Summary
All endpoints in the task are implemented with thin controllers, Application contracts/validators/interfaces and
EF services in Infrastructure. Integration tests run against Testcontainers `postgres:17` through one shared
`WebApplicationFactory<Program>`. Build is 0 errors / 0 warnings with `TreatWarningsAsErrors=true`; 114/114 tests pass
(88 unit, 26 integration). No migration was needed (`has-pending-model-changes`: no changes).
One conflict in the task (low-stock rule vs. test 5) was decided by the user during the run — see Deviations.

## Files changed
Part A (`daf481d`)
- `backend/src/Omnichannel.Api/Omnichannel.Api.http` — `@email`/`@password` now `{{$dotenv BOOTSTRAP_ADMIN_EMAIL}}` /
  `{{$dotenv BOOTSTRAP_ADMIN_PASSWORD}}`, comment points to `backend/src/Omnichannel.Api/.env` (git-ignored; verified with
  `git check-ignore`). The `.env` was not read or created. The committed file had only placeholders; no credential in history.
- `README.md` — section 4 keeps only `database update`; integration-test/Docker line added (see Deviations for placement).
- `backend/Directory.Build.props` — `TreatWarningsAsErrors=true`, TODO removed.

Part B / C (`b16c026`)
- `backend/Directory.Packages.props`, `Omnichannel.Tests.csproj` — `Microsoft.AspNetCore.Mvc.Testing` 10.0.12,
  `Testcontainers.PostgreSql` 4.15.0 (latest 4.x).
- `Omnichannel.Api/Program.cs` — `public partial class Program;` appended (plus a one-line comment). No other change.
- `Omnichannel.Api/Controllers/{Channels,Customers,Products,Inventory}Controller.cs` — binding, service call, result only.
- `Omnichannel.Application/Common/Paging/` — `PageQuery` (base record for `[FromQuery]`), `PageQueryValidator<T>`
  (whitelist passed by each endpoint, case-insensitive), `PagedResult<T>`.
- `Omnichannel.Application/{Channels,Customers,Products,Inventory}/` — contracts, validators, service interfaces, error codes.
- `Omnichannel.Infrastructure/Persistence/{Channels,Customers,Products,Inventories}/` — EF implementations.
- `Omnichannel.Infrastructure/Persistence/LikePattern.cs` — escapes `\`, `%`, `_`; used with `EF.Functions.ILike(..., "\\")`.
- `Omnichannel.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs` — `IsUniqueViolation(constraintName)` on
  `PostgresException` 23505.
- `Omnichannel.Infrastructure/Persistence/QueryableExtensions.cs` — `OrderBy(key, descending)` helper.
- `Configurations/{Product,Channel}Configuration.cs` — `SkuIndexName`/`SlugIndexName` constants and `HasDatabaseName` with the
  **existing** names (`IX_Products_Sku`, `IX_Channels_Slug`) so the 409 mapping cannot drift from the schema. Model unchanged.
- `Omnichannel.Infrastructure/DependencyInjection.cs` — registers the four services (scoped).
- `Omnichannel.Domain/Entities/{Channel,Customer,Product}.cs` — `Update(...)` methods with guards; constructors reuse them.
  `Customer` stores blank optional fields as null; `Product.NormalizeSku` (trim + upper) extracted.
- Tests: `Integration/{ApiFactory,TestData,ProductEndpointsTests,ChannelEndpointsTests,CustomerEndpointsTests,PagingValidationTests}.cs`,
  `Validation/{PageQueryValidatorTests,RequestValidatorTests}.cs`, `Domain/CatalogEntityTests.cs`, `Persistence/LikePatternTests.cs`.

### Implementation notes
- **Test host:** environment `Testing` (not `Development`) so user-secrets are not loaded → no `BootstrapAdmin`, no developer
  connection string. Overrides via `UseSetting` (`ConnectionStrings:Default`, `Jwt:SigningKey` = test-only constant ≥ 32 bytes).
  Migrations applied with `Database.MigrateAsync()` before the host is built; host built once in `InitializeAsync`.
- **Tokens:** `CreateClientAs(role)` uses the app's `IAccessTokenIssuer` with a random user id.
- **Uniqueness:** no check-then-insert. Product create adds Product + `Inventory(ProductId, 0)` and calls `SaveChanges` once;
  23505 on `IX_Products_Sku` → 409 `product_sku_exists`; on `IX_Channels_Slug` → 409 `channel_slug_exists`.
- **Stable paging:** every sort gets a `ThenBy(id)` tie-breaker. Default sort: channels by name (unpaged), customers by name,
  products and inventory by SKU.
- **Inventory `updatedAt`:** returns `UpdatedAt ?? CreatedAt` of the inventory row (non-null "last change"); `sortBy=updatedAt`
  uses the same expression.
- **Product responses:** list and detail share one shape (`ProductResponse`) with total/reserved/available stock.

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0.

| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` (after Part A) | 0 errors, 0 warnings (warnings as errors on) |
| `dotnet restore` (new packages) | success, no warnings |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final, all) | Failed 0, Passed 114, Total 114 |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 88, Total 88 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 26, Total 26 |

Confirmed from the test output that a real container was used (`[testcontainers.org] Docker container ... ready`,
`pg_isready` on `postgres:17`).

**Mutation check:** with `LikePattern.Contains` temporarily changed to skip escaping,
`Search_treats_percent_sign_literally` failed (1/1 failed); restored afterwards (file unchanged in the commit).

### Required tests → where
| # | Requirement | Test |
| --- | --- | --- |
| 1 | Manager creates product → 201, inventory 0/0/0 | `ProductEndpointsTests.Manager_creates_product_with_empty_inventory_row` (checks DB row) |
| 2 | Duplicate SKU, case only → 409 | `Duplicate_sku_differing_only_in_case_is_a_conflict` |
| 3 | 5 concurrent same SKU → 1×201, 4×409, 1 row | `Concurrent_creates_with_same_sku_produce_exactly_one_product` |
| 4 | Staff POST → 403; anonymous GET → 401 | `Staff_cannot_create_products`, `Anonymous_cannot_list_products` |
| 5 | lowStockOnly: threshold 5 / stock 0 in, threshold 0 out | `Low_stock_filter_returns_products_at_or_below_a_positive_threshold` |
| 6 | Channel dup slug 409, `Web Site` 400, Staff 403 | `ChannelEndpointsTests` (3 tests) |
| 7 | `50%` matches literally | `CustomerEndpointsTests.Search_treats_percent_sign_literally` (control row `500 units` excluded) |
| 8 | `pageSize=101`, `sortBy=unknown` → 400 `validation_failed` | `PagingValidationTests` (products, customers, inventory) |
| Unit | validators, entity `Update` guards | `PageQueryValidatorTests`, `RequestValidatorTests`, `CatalogEntityTests`, `LikePatternTests` |

Expected noise in integration output: EF logs `Failed executing DbCommand` at Error level for each intentional unique violation
(tests 2 and 3), and `Failed to determine the https port for redirect` (TestServer has no HTTPS port).

## Deviations from the task
1. **Low-stock rule (decided by the user during this run).** The task says `isLowStock = availableStock <= lowStockThreshold`,
   but test 5 expects threshold 0 / stock 0 to be *not* low, which contradicts it (0 <= 0). User chose:
   **`isLowStock = LowStockThreshold > 0 AND AvailableStock <= LowStockThreshold`** — threshold 0 disables the warning.
   Used identically for `lowStockOnly` and `isLowStock`, computed in SQL. Please record it in `implementation-log.md`
   (it matters for low-stock alerts in step 11).
2. **Infrastructure folder `Persistence/Inventories`** (not `Persistence/Inventory`): a namespace
   `Omnichannel.Infrastructure.Persistence.Inventory` would shadow the `Inventory` entity for all code under `Persistence`
   (e.g. `OmnichannelDbContext`). Application keeps `Application/Inventory` as in CLAUDE.md.
3. **README integration-test line** is in section 2 (Build and test), not section 4, because that is where `dotnet test` is.
4. `HasDatabaseName` added for the two existing unique indexes (same names) — no schema change, verified.

## Blocked / questions for Cowork
None open (low-stock question answered by the user; see Deviation 1).

## Out of scope / follow-ups
- **Model-binding 400s lack `errorCode`:** a malformed body or query value (e.g. unknown enum string, `pageSize=abc`) is rejected
  by `[ApiController]`'s automatic 400 before `ValidationFilter`, so the ProblemDetails has no `errorCode=validation_failed`.
  Fix: configure `ApiBehaviorOptions.InvalidModelStateResponseFactory` to add it. Pre-existing behaviour (also affects login).
- **SKU format:** only non-empty and ≤ 64 chars are validated (no character whitelist was specified). Decide whether spaces etc.
  should be rejected.
- `ChannelProduct` mapping, deletes, S3 images, stock changes — out of scope per task.

## Manual steps for the user
- For the `.http` file: create `backend/src/Omnichannel.Api/.env` (git-ignored) with
  `BOOTSTRAP_ADMIN_EMAIL=...` and `BOOTSTRAP_ADMIN_PASSWORD=...`.
- Integration tests need Docker Desktop running; otherwise use `dotnet test --filter "Category!=Integration"`.

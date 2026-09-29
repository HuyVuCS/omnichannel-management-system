# Report 003 — Step 4b: Inventory import, movement history, `inventory.changed` outbox events

Status: Done
Commit: 98a88a1 (Part A, review-002 follow-ups), 828ee18 (Part B)

## Summary
`POST /api/inventory/import` applies a goods-receipt document atomically: per item (locked in `ProductId` order) one
`UPDATE ... RETURNING`, an `Import` movement and a `Pending` `inventory.changed` outbox row, all in one explicit transaction.
Re-posting a document is rejected by the new unique index `UX_InventoryMovements_Reference` (migration
`InventoryImportIdempotency`) → 409 `stock_import_already_applied`. `GET /api/inventory/movements` lists history.
All four review-002 follow-ups are done. Build 0 warnings; 165/165 tests (116 unit, 49 integration).

## Files changed
Part A (`98a88a1`)
- `Api/Filters/ValidationProblemResponse.cs` (new) — the single 400 builder (ProblemDetailsFactory → `correlationId`,
  `errorCode=validation_failed`); used by `ValidationFilter` and by `ApiBehaviorOptions.InvalidModelStateResponseFactory` (`Program.cs`).
- `Application/Common/Paging/PageQuery.cs`, `PageQueryValidator.cs` — `MaxPage = 10_000`, `page` 1–10,000.
- `Domain/Entities/Product.cs` — `MaxSkuLength`, `SkuPattern`, `IsValidSku`; constructor guard (throws `ArgumentException`).
  `Application/Products/ProductValidators.cs` — SKU validated after `NormalizeSku` (`Cascade.Stop` so null never reaches the regex).
- `Infrastructure/Persistence/Inventories/InventoryPredicates.cs` (new) — `IsLowStock = i => i.AvailableStock <= i.Product.LowStockThreshold`
  (defined once) + `Inline(...)` helper that substitutes the predicate into the list projection, so filter and flag share one
  expression and still translate to SQL. `InventoryQueryService` uses it for both. XML doc on `LowStockOnly` updated.
- Tests: low-stock test replaced (threshold 5 / stock 0 and threshold 0 / stock 0 both low); SKU accept/reject (integration + unit +
  entity); `page=2147483647`, `page=10001`, `pageSize=abc`, `page=abc`, malformed JSON → 400 `validation_failed`; binding 400 carries
  `correlationId`; `page=10000` → 200.

Part B (`828ee18`)
- `Application/Common/Messaging/` (new) — `EventTypes` (5 routing keys), `InventoryChangedPayload`, `MessageJson` (Web defaults = camelCase,
  `JsonStringEnumConverter`).
- `Application/Common/Abstractions/ICorrelationContext.cs` (new) — see Deviations. Implementations: `Api/Middleware/HttpCorrelationContext.cs`
  (request's `X-Correlation-Id`), `Workers/WorkerCorrelationContext.cs` (per scope; settable for message handlers). Registered in both `Program.cs`.
- `Application/Inventory/` — `InventoryImportContracts.cs`, `IInventoryImportService.cs`, `InventoryMovementContracts.cs`;
  `IInventoryQueryService.ListMovementsAsync`; validators `ImportInventoryRequestValidator`, `InventoryMovementListQueryValidator`.
- `Infrastructure/Persistence/Inventories/InventoryImportService.cs` (new) — the import (details below).
- `Infrastructure/Persistence/Inventories/InventoryQueryService.cs` — `ListMovementsAsync`.
- `Infrastructure/Persistence/Configurations/InventoryMovementConfiguration.cs` — index renamed/extended, `ReferenceIndexName` constant.
- `Infrastructure/Persistence/Migrations/20260929090821_InventoryImportIdempotency{,.Designer}.cs` + snapshot.
- `Infrastructure/DependencyInjection.cs` — registers `IInventoryImportService`.
- `Api/Controllers/InventoryController.cs` — `POST import` (ManagerOrAdmin), `GET movements` (AnyStaff).
- Tests: `Integration/InventoryImportTests.cs`, `Validation/InventoryValidatorTests.cs`, `ModelConfigurationTests` (new index name + filter,
  old name gone).

### Import implementation
1. `BeginTransactionAsync`; no `EnableRetryOnFailure`.
2. Items sorted by `ProductId` (`Guid` comparison). Any future code that locks several inventory rows (reservation, step 7) must use
   the same ordering.
3. Per item `Database.SqlQuery<StockRow>($"UPDATE \"Inventories\" SET \"TotalStock\" = \"TotalStock\" + {stock}, \"UpdatedAt\" = {now},
   \"UpdatedBy\" = {actor} WHERE \"ProductId\" = {id} RETURNING ...")` — parameterised, no read-modify-write. `actor = ICurrentUser.UserId ?? "system"`.
4. 0 rows → `ValidationException` on `Items[i].ProductId` (index in request order) → 400; transaction disposed without commit.
5. Movement (`Import`, `StockImport`, `ReferenceId = series/number` or number) + `OutboxEvent(inventory.changed, aggregateId = productId,
   payload snapshot, correlationId, movementId)`; one `SaveChangesAsync`, then commit. Unique violation on `UX_InventoryMovements_Reference`
   → 409, everything rolled back.
6. Response items in request order.

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0.

| Command | Result |
| --- | --- |
| `dotnet test` after Part A | Failed 0, Passed 138, Total 138 |
| `dotnet ef migrations add InventoryImportIdempotency ...` | `Done.` |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final, all) | Failed 0, Passed 165, Total 165 |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 116, Total 116 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 49, Total 49 |

**Mutation checks** (each reverted afterwards):
- `InvalidModelStateResponseFactory` removed → `Model_binding_errors_carry_error_code_and_correlation_id` failed.
- `OrderBy(ProductId)` removed from the import → `Concurrent_multi_item_imports_in_opposite_order_do_not_deadlock` failed 3/3 runs.
- `ICorrelationContext` registration removed from Workers → Workers host (Development, DI validation on build) exits with
  `Unable to resolve service for type '...ICorrelationContext'`. With the registration it starts (ran until the 20 s timeout;
  dummy password-less connection string, no database needed for build validation).

**Stock-write audit:** `grep` over `src/` (excluding tests/migrations) for `TotalStock`/`ReservedStock` assignments, `ExecuteUpdate`,
`ExecuteSql*`, `SqlQuery`, `"Inventories"`: the only writes are the import `UPDATE` (movement in the same transaction) and the
`Inventory` constructor used for the initial all-zero row at product creation.

### Migration SQL (`dotnet ef migrations script InitialCreate InventoryImportIdempotency`)
```sql
START TRANSACTION;
DROP INDEX "UX_InventoryMovements_Reference_Reservation";

CREATE UNIQUE INDEX "UX_InventoryMovements_Reference" ON "InventoryMovements" ("ReferenceType", "ReferenceId", "ProductId", "Type") WHERE "ReferenceId" IS NOT NULL AND "Type" IN ('Import', 'Reserve', 'Release');

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260929090821_InventoryImportIdempotency', '10.0.12');

COMMIT;
```
`Down` restores the old index. Safe on existing data: no `Import` movements exist before this task.

### Required tests → where
| # | Requirement | Test (`InventoryImportTests` unless noted) |
| --- | --- | --- |
| 1 | 20 concurrent +1 → 20 stock, 20 movements, 20 Pending outbox rows linked | `Concurrent_imports_for_one_product_are_all_applied_with_movement_and_outbox_each` |
| 2 | Re-post → 409, counts unchanged | `Reposting_the_same_document_is_rejected_and_changes_nothing` |
| 3 | Valid + unknown product → 400, nothing applied | `Import_with_an_unknown_product_is_rolled_back_completely` |
| 4 | Opposite-order concurrent imports → both 200 | `Concurrent_multi_item_imports_in_opposite_order_do_not_deadlock` (5 rounds) |
| 5 | Payload deserialises; CorrelationId = `X-Correlation-Id` | `Outbox_payload_is_a_stock_snapshot_carrying_the_request_correlation_id` |
| 6 | Movements: productId, referenceId, newest first, paging | `Movements_are_filtered_and_paged_newest_first` |
| 7 | Staff import → 403 | `Staff_cannot_import` |
| A4 | threshold 5 + import 10 → not low; + import 5 → low | `Low_stock_flag_follows_imported_stock` (theory) |
| Unit | import validator (duplicates, bounds) | `ImportInventoryRequestValidatorTests`, `InventoryMovementListQueryValidatorTests` |

## Deviations from the task
1. **`ICorrelationContext` (new abstraction).** The task requires the outbox `CorrelationId` to equal the request's `X-Correlation-Id`,
   but Infrastructure had no access to it. Added `ICorrelationContext` next to `ICurrentUser` (same pattern), implemented by the API and,
   so the Workers host still passes DI validation, by a per-scope `WorkerCorrelationContext` (message handlers set it in step 6+).
2. **Movements query:** `sortBy` is rejected (empty whitelist); `search` and `sortDir` are accepted by the shared contract but ignored
   (documented on `InventoryMovementListQuery`). `from`/`to` are inclusive and converted to UTC (Npgsql rejects non-UTC `timestamptz`
   parameters; a `+07:00` value would otherwise be a 500).
3. **Unknown-product error key** is `Items[i].ProductId` (PascalCase, same as every other FluentValidation error key in the API) rather
   than `items[i].productId`.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- `TotalStock` is `integer`: repeated imports up to 1,000,000 per item could eventually exceed 2,147,483,647 → PostgreSQL 22003 → 500.
  Not reachable in practice soon; consider a CHECK/validation or mapping 22003 → 409 when adjustments arrive.
- Unique violations from the import are logged by EF at Error level (same noise as noted in review 002, step 9).
- `WorkerCorrelationContext` must be set from the envelope's `correlationId` by every message handler (step 6+).

## Manual steps for the user
- Apply the new migration to your database (migrator role):
  ```powershell
  $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
  dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
  ```

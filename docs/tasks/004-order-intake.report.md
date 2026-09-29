# Report 004 — Step 5: Order intake `POST /api/orders/{slug}` (decision A: outbox first)

Status: Done
Commit: 5333ae0 (Part A, review-003 follow-ups), b249f45 (order intake)

## Summary
`POST /api/orders/{slug}` validates the order against current data, normalises it (price snapshot) and stores a
`ProcessingTask(OrderProcessing)` plus a `Pending` `order.created` outbox row in one `SaveChanges`, then returns **202**.
No `Order` is created, stock is not checked and there is no RabbitMQ dependency. Duplicate submissions (sequential or
concurrent) resolve to the existing task through the new unique index `UX_ProcessingTasks_IdempotencyKey`.
Part A: validation error keys are JSON paths (`items[1].productId`); stock overflow on import → 409 `stock_limit_exceeded`.
Build 0 warnings; 193/193 tests (132 unit, 61 integration).

## Files changed
Part A (`5333ae0`)
- `Application/Common/Validation/ValidationConventions.cs` (new) — `UseJsonPropertyNames()` sets FluentValidation's global
  `PropertyNameResolver` (camelCase for every segment of the member chain; collection indexers come out as `items[0].stock`);
  `JsonName`/`ItemPath` helpers for manually built failures. Called once in `Api/Program.cs` right after `CreateBuilder`.
- `Tests/TestAssemblySetup.cs` (new) — `[ModuleInitializer]` calling the same method (see Deviations 1).
- `Api/Filters/ValidationProblemResponse.cs` — remarks documenting that model-binding 400s keep ASP.NET's keys
  (`PageSize`, `$.items[0].stock`).
- `Infrastructure/Persistence/Inventories/InventoryImportService.cs` — unknown-product key via `ValidationConventions.ItemPath`
  (`items[i].productId`); `PostgresException` 22003 from the stock `UPDATE` → `ConflictException(stock_limit_exceeded)`, transaction
  rolled back on dispose. `InventoryErrorCodes.StockLimitExceeded` added.
- Tests: key assertions switched to camelCase (Login, Product/Channel/Customer, Import validators); new integration tests
  `Validation_error_keys_are_json_paths` (`items[1].productId`; `documentNumber` + `items[0].stock`) and
  `Import_beyond_the_maximum_stock_is_a_conflict_and_changes_nothing` (stock set to `int.MaxValue - 10` in the test DB, +1,000,000 → 409,
  stock/movements/outbox unchanged).

Part B (`b249f45`)
- `Domain/Entities/ProcessingTask.cs` — `IdempotencyKey` (optional ctor parameter, ≤ 200, not blank), `MaxIdempotencyKeyLength`.
- `Domain/Entities/OutboxEvent.cs` — optional `outboxEventId` ctor parameter (pre-generated id; not `Guid.Empty`).
- `Application/Common/Exceptions/NotFoundException.cs` — optional `errorCode` (default `not_found`); `GlobalExceptionHandler` uses it.
- `Application/Orders/` (new) — `SubmitOrderRequest`/`SubmitOrderItem`/`SubmitOrderResponse`, `OrderErrorCodes.ChannelNotFound`,
  `NormalizedOrder`/`NormalizedOrderItem`, `OrderIdempotencyKey.For`, `IOrderIntakeService`, `SubmitOrderRequestValidator`.
- `Application/Common/Messaging/OrderCreatedPayload.cs` (new) — see Deviations 3.
- `Infrastructure/Persistence/Orders/OrderIntakeService.cs` (new), registered in `Infrastructure/DependencyInjection.cs`.
- `Infrastructure/Persistence/Configurations/ProcessingTaskConfiguration.cs` — column length + `UX_ProcessingTasks_IdempotencyKey`.
- `Infrastructure/Persistence/Migrations/20260929093626_ProcessingTaskIdempotencyKey{,.Designer}.cs` + snapshot.
- `Api/Controllers/OrdersController.cs` (new) — binds slug + body, calls the service, returns `Accepted(result)`.
- Tests: `Integration/OrderIntakeTests.cs`, `Validation/SubmitOrderRequestValidatorTests.cs`, `Domain/ProcessingTaskTests.cs`.

### Intake flow (`OrderIntakeService.SubmitAsync`)
1. Slug trimmed + lower-cased; channel must exist **and** be `Active` → else 404 `channel_not_found`.
2. Customer exists? Products exist (one query, also loads `Price`)? All failures collected into one `ValidationException`
   (`customerId`, `items[i].productId`) → 400. Stock is not checked.
3. `externalOrderId` trimmed; key `order:{channelId:N}:{externalOrderId}` (case-sensitive).
4. `outboxEventId = Guid.CreateVersion7()` first → `ProcessingTask(OrderProcessing, orderId: null, correlationId,
   messageId: outboxEventId.ToString(), key)` → `NormalizedOrder` (taskId, channel, items with `unitPrice = Product.Price`,
   `receivedAt` from `TimeProvider`, `receivedBy = ICurrentUser.UserId ?? "system"`) → `OutboxEvent(order.created,
   aggregateId = taskId, MessageJson payload, correlationId, null, outboxEventId)`.
5. One `SaveChangesAsync` (single transaction). Unique violation on `UX_ProcessingTasks_IdempotencyKey` → `ChangeTracker.Clear()`,
   load the existing task → 202 with its **current** status, its original `correlationId`, `duplicate: true`. No check-then-insert.
6. `correlationId` from `ICorrelationContext`; the controller does not pass it.

## Verification (actually executed)
Run from `backend/`, SDK 10.0.401, Docker 29.8.0. No RabbitMQ was running at any point.

| Command | Result |
| --- | --- |
| `dotnet test` after Part A | Failed 0, Passed 167, Total 167 |
| `dotnet ef migrations add ProcessingTaskIdempotencyKey ...` | `Done.` |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final, all) | Failed 0, Passed 193, Total 193 |
| `dotnet test --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 132, Total 132 |
| `dotnet test --filter "Category=Integration"` | **Integration:** Failed 0, Passed 61, Total 61 |

### Migration SQL (`dotnet ef migrations script InventoryImportIdempotency ProcessingTaskIdempotencyKey`)
```sql
START TRANSACTION;
ALTER TABLE "ProcessingTasks" ADD "IdempotencyKey" character varying(200);

CREATE UNIQUE INDEX "UX_ProcessingTasks_IdempotencyKey" ON "ProcessingTasks" ("IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260929093626_ProcessingTaskIdempotencyKey', '10.0.12');

COMMIT;
```
Nullable column + partial index: existing rows (none expected) keep `NULL` and are unaffected.

### Required tests → where (`OrderIntakeTests` unless noted)
| # | Requirement | Test |
| --- | --- | --- |
| 1 | 202; one task (OrderProcessing, Pending, OrderId null, MessageId = outbox id); one Pending `order.created`; price snapshot; no Order | `Staff_order_is_accepted_as_task_and_outbox_event_without_creating_an_order` |
| 2 | `X-Correlation-Id` on both rows and in the body | `Correlation_id_header_is_stored_on_task_and_outbox_and_returned` |
| 3 | Same request → same taskId, `duplicate: true`, still one task/outbox | `Resubmitting_the_same_order_returns_the_existing_task` (resubmitted id has extra spaces → trimmed to the same key) |
| 4 | 10 concurrent → one task + one outbox; same taskId everywhere | `Concurrent_identical_submissions_create_one_task` (also exactly one `duplicate: false`) |
| 5 | Same external id on two channels → two tasks | `Same_external_order_id_on_two_channels_creates_two_tasks` |
| 6 | Unknown/inactive slug → 404; unknown customer/product → 400, nothing written | `Unknown_or_inactive_channel_is_not_found`, `Unknown_customer_or_product_is_rejected_and_nothing_is_written` |
| 7 | Empty items, duplicate product, `stock: 0` → 400 | `Invalid_requests_are_rejected` (asserts exact keys `items`, `items[0].stock`) |
| 8 | Anonymous → 401 | `Anonymous_cannot_submit_orders` |
| 9 | Works with RabbitMQ stopped | The whole integration suite runs with no broker; `Api_has_no_rabbitmq_dependency` asserts no API/Infrastructure/Application assembly references a RabbitMQ client |
| Unit | validator; key format | `SubmitOrderRequestValidatorTests`, `ProcessingTaskTests` (key format, longest key fits `varchar(200)`, entity guards, pre-generated outbox id) |

## Documented consequence (as requested)
The idempotency key has no status component, so **an external order id that was rejected (e.g. insufficient stock in step 6)
cannot be resubmitted under the same id**: the resubmission returns the existing (failed) task with `duplicate: true`. The channel
must send a new external order id. The worker keeps `UX_Orders_ChannelId_ExternalOrderId` as the final guard.
Related: the key is case-sensitive (`WEB-1` and `web-1` are different orders), matching `UX_Orders_ChannelId_ExternalOrderId`.

## Deviations from the task
1. **Resolver also set in the test assembly.** FluentValidation's resolver is a process-wide static. Set only at API start-up, validator
   unit tests would see PascalCase or camelCase depending on whether an integration test had already started the host in the same
   process. A `[ModuleInitializer]` in the test assembly calls the same `ValidationConventions.UseJsonPropertyNames()` so results are
   deterministic. The convention lives in Application (not Api) so both hosts and the tests share it.
2. **`NotFoundException` got an optional `errorCode`** (default `not_found`, existing behaviour unchanged) — needed for
   404 `channel_not_found`; the handler previously hard-coded `not_found`.
3. **`OrderCreatedPayload` is its own record** with the same fields as `NormalizedOrder` plus `From(NormalizedOrder)`, instead of
   reusing the type: keeps the message contract separate from the internal DTO so it changes only deliberately (workers depend on it).
4. **`OutboxEvent` optional `outboxEventId` parameter** — the task said "generate both ids up front"; only the outbox id needs to be
   known before the task (the task's own id is generated by its constructor and then used for the payload and aggregate id).
5. Duplicate response returns the **original** submission's `correlationId` (the one stored on the task), so the client can trace the
   flow that actually runs.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- Intake does not check that products are sellable (no product status exists) or that the customer is active — none of these
  fields exist yet.
- Duplicate submissions log an EF Error for the unique violation (same noise as review 002/003; step 9).
- `WorkerCorrelationContext` must be set from the envelope in the Order Worker (task 006), and the worker must parse
  `OrderCreatedPayload` with `MessageJson`.

## Manual steps for the user
- Apply the new migration (migrator role):
  ```powershell
  $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
  dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
  ```

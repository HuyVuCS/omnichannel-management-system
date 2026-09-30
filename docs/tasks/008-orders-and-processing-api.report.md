# Report 008 — Orders and processing read API, manual retry, audit events

Status: Done
Commit: `a1b588f` (Part A), `886d31b` (Parts B–D)

## Summary
- **Part A:** when a retry copy cannot be published, the consumer waits before the NACK-requeue: 1 s doubling to 30 s per
  consecutive failure, reset when a copy is published. The error is logged at most once per 30 s; the other failures go to Debug.
  A persistently unroutable copy used to be redelivered 1,172 times in 10 s; with the backoff it is redelivered 3–6 times.
- **Parts B–C:** new endpoints `GET /api/orders`, `GET /api/orders/{id}`, `GET /api/processing/tasks`,
  `GET /api/processing/tasks/{id}` and `POST /api/processing/tasks/{id}/retry`. A manual retry writes a new outbox row that copies
  the original message and sets the task to `Pending`. It also sets the new column `AttemptBudgetStart`, so the task gets a fresh
  budget of attempts (migration `ProcessingTaskAttemptBudget`).
- **Part D:** audit events `processing.retry_requested` and `inventory.imported`.
- Build: 0 warnings. Tests: **366/366** (250 unit, 116 integration), 3 consecutive full runs green.

## Files changed
**Part A (`a1b588f`)**
- `Infrastructure/RabbitMQ/RabbitMqQueueConsumer.cs` — `PublishDelayedAsync`: if the copy fails, wait
  `RetryBackoff.Compute(consecutive, 1 s, 30 s)` with equal jitter, then requeue. The counter is per consumer and resets on a
  successful copy. The wait is cancelled by `StopAsync`/`DisposeAsync`, so shutdown is not delayed. `LogRequeue` writes at most
  one Error per 30 s, and that Error includes the number of suppressed failures.
- Tests: `Integration/Workers/FailureInjection.cs` (`OnlyTaskId`, `Heal()`), `OrderWorkerRetryTests.cs` (new test).

**Parts B–D (`886d31b`)**
- `Domain/Entities/ProcessingTask.cs` — `AttemptBudgetStart` and `RequeueForManualRetry(newMessageId)`. Only a `Failed` task can
  be retried. The method sets the new message id and status `Pending`, sets the budget start to the highest `AttemptNo`, keeps the
  attempts, and returns the previous message id.
- `Infrastructure/Persistence/Configurations/ProcessingTaskConfiguration.cs` + migration `20260930130908_ProcessingTaskAttemptBudget`
  (column `DEFAULT 0` + CHECK `>= 0`).
- Attempt budget:
  - `Application/Orders/IOrderReservationService.cs`: `AttemptStart` now carries `AttemptBudgetStart`.
  - `OrderReservationService` fills it in.
  - `OrderMessageHandler` passes it on in `HandlerResult.Retry`.
  - `Infrastructure/RabbitMQ/QueueMessageHandling.cs`: `Retry(..., AttemptBudgetStart = 0)`, `Retry.AttemptInBudget` and
    `RetryPolicy.AttemptInBudget(attemptNo, budgetStart)`.
  - `RabbitMqQueueConsumer` uses `AttemptInBudget` for the limit check and to pick the delay tier.
- `Application/Orders/OrderQueryContracts.cs`, `IOrderQueryService.cs`, `OrderListQueryValidator.cs`; `OrderIntakeContracts.cs`
  (`order_not_found`).
- `Application/Processing/ProcessingContracts.cs` (responses, `ProcessingTaskListQuery`, error codes `task_not_found`,
  `task_not_failed`, `retry_source_missing`), `IProcessingTaskService.cs`, `ProcessingTaskListQueryValidator.cs`,
  `ProcessingEvents.cs` (`processing.retry_requested`).
- `Infrastructure/Persistence/Orders/OrderQueryService.cs`, `Persistence/Processing/ProcessingTaskService.cs`,
  `Persistence/Processing/ProcessingProjections.cs` (shared attempt projection), `DependencyInjection.cs`.
- `Api/Controllers/OrdersController.cs` (GET list/detail), `Api/Controllers/ProcessingController.cs` (new).
- `Application/Inventory/InventoryImportContracts.cs` (`InventoryEvents.Imported`); `InventoryImportService.cs` writes the event
  in the import transaction.
- `README.md` §6 — how to use manual retry.
- Tests:
  - New: `Integration/OrderQueryTests.cs`, `Integration/ProcessingTaskEndpointsTests.cs`, `Integration/ManualRetryEndToEndTests.cs`
    (with `RabbitMqBrokerFixture`), `Integration/Workers/QueryShapeTests.cs`, `Validation/OrderAndProcessingQueryValidatorTests.cs`,
    `Workers/AttemptBudgetTests.cs`.
  - Extended: `Domain/ProcessingAttemptFlowTests.cs`, `Integration/InventoryImportTests.cs`, `Integration/ApiFactory.cs`
    (`CreateClientAs(role, userId?)`).

### API (as implemented)
| Endpoint | Policy | Notes |
| --- | --- | --- |
| `GET /api/orders` | AnyStaff | Paging contract. Filters: `channel` (slug, case-insensitive; an unknown slug returns an empty page), `status`, `customerId`, `from`/`to` (inclusive, on `CreatedAt`), `search` (ILIKE, escaped, on external id or customer name). `sortBy`: `createdAt` (default), `totalAmount`, `externalOrderId`. Tie-breaker `OrderId`. |
| `GET /api/orders/{id}` | AnyStaff | Header, `items[]` (sorted by SKU), and `tasks[]` (by `createdAt`), each with `attempts[]` sorted by `attemptNo`. 404 `order_not_found` if the order does not exist. |
| `GET /api/processing/tasks` | AnyStaff | Filters: `taskType`, `status`, `orderId`, `correlationId`, `from`/`to`. Attempt filters `workerName`, `workerInstanceId`, `errorCode`, `attemptNo` go into **one** `EXISTS`, so a single attempt must match all of them. `sortBy`: `createdAt` (default), `updatedAt`. Each item has the task fields, `attemptBudgetStart`, `attemptCount` and `lastAttempt`. |
| `GET /api/processing/tasks/{id}` | AnyStaff | The task, all attempts, and `message` (the outbox row for its current `messageId`: status, `publishedAt`, `publishAttempts`, `lastError`; null if there is no row). 404 `task_not_found`. |
| `POST /api/processing/tasks/{id}/retry` | ManagerOrAdmin | 202 `{taskId, status: "Pending", messageId}`. 409 `task_not_failed` or `retry_source_missing`; 404 `task_not_found`. |

### Manual retry transaction
1. Take the lock with `SELECT 1 ... FOR UPDATE`, then load the task and its attempts.
2. Check the status. Anything other than `Failed` returns 409 `task_not_failed`.
3. Find the outbox row by `MessageId`. If it is missing, return 409 `retry_source_missing`.
4. Insert a new `OutboxEvent` with the same `EventType`, `AggregateId`, `Payload` and `CorrelationId`, a new id, and
   `InventoryMovementId = null`.
5. Call `task.RequeueForManualRetry(newId)`.
6. Write `SystemEventLog` Information `processing.retry_requested` with `{taskId, taskType, requestedBy, previousMessageId,
   newMessageId, attemptBudgetStart}`. `requestedBy` is the JWT `sub`.
7. Commit.

If two retries of the same task run at the same time, the lock makes them run one after the other. The second one then sees
`Pending` and returns 409.

### Query shape (no N+1), measured by `QueryShapeTests` (counts EF Core commands)
| Call | SQL commands |
| --- | --- |
| Order detail (lines + products + 2 tasks with 3 and 2 attempts) | **1**. A single statement: the tasks are a correlated subquery on `ProcessingTasks.OrderId`, and lines and attempts are joined into it |
| Order list page | **2** (COUNT + page; `itemCount` is a subquery) |
| Task list page (7 tasks, attempt filter) | **2** (COUNT + page; `attemptCount` is a subquery; `lastAttempt` is a correlated subquery ordered by `attemptNo desc`) |
| Task detail | **2** (task + attempts in one statement, then the outbox row by primary key) |

## Verification (actually executed)
All commands ran from `backend/`, with SDK 10.0.401 and Docker 29.8.1.

While I worked, the user's own `Omnichannel.Api.exe` and `Omnichannel.Workers.exe` were running and locked `bin/Debug`. So every
build and test used `--artifacts-path <scratchpad>/artifacts`, and `dotnet ef` used `--configuration Release`. I did not stop the
user's processes.

| Command | Result |
| --- | --- |
| `dotnet build --artifacts-path … --no-incremental` | 0 errors, 0 warnings |
| `dotnet test --artifacts-path … --no-build` (3 consecutive full runs) | Failed 0, Passed 366, Total 366 (×3; 1 m 19 s / 1 m 3 s / 1 m 4 s) |
| `dotnet test … --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 250, Total 250 |
| `dotnet test … --filter "Category=Integration"` | **Integration:** Failed 0, Passed 116, Total 116 |
| `dotnet ef migrations add ProcessingTaskAttemptBudget -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api --configuration Release` | `Done.` |
| `dotnet ef migrations has-pending-model-changes … --configuration Release` | `No changes have been made to the model since the last migration.` |
| Build + unit tests again after a comment-only edit that went into `886d31b` | 0 warnings; 250/250 |

### Migration SQL
`dotnet ef migrations script OutboxPublishTracking ProcessingTaskAttemptBudget`:
```sql
START TRANSACTION;
ALTER TABLE "ProcessingTasks" ADD "AttemptBudgetStart" integer NOT NULL DEFAULT 0;

ALTER TABLE "ProcessingTasks" ADD CONSTRAINT "CK_ProcessingTasks_AttemptBudgetStart_NonNegative" CHECK ("AttemptBudgetStart" >= 0);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930130908_ProcessingTaskAttemptBudget', '10.0.12');

COMMIT;
```

### Mutation checks (each one reverted and confirmed reverted with grep)
| Mutation | Result |
| --- | --- |
| Part A: no wait before the requeue | Test **failed**: `Assert.InRange() Failure … Range: (3 - 6) Actual: 1172` (redeliveries in 10 s) |
| Consumer checks the limit with `AttemptNo` instead of `AttemptInBudget` | Fresh-budget test **failed**: attempts `[…, 2, 3, 4]` instead of `[…, 2, 3, 4, 5, 6]` (only one attempt after the manual retry) |
| `FOR UPDATE` removed from the retry | Concurrent-retry test **failed** 3 out of 3 runs: `[Accepted, Accepted]` instead of `[Accepted, Conflict]` |

### Required tests → where
| # | Requirement | Test |
| --- | --- | --- |
| A | Delay exchange missing: few redeliveries, nothing lost, processed after the topology is re-declared | `OrderWorkerTests.Unroutable_retry_copy_is_requeued_with_backoff_and_processed_once_the_topology_is_back`. The first tier's exchange is deleted after start-up and the handler throws on every delivery. Result: 3–6 deliveries in 10 s, exactly 1 Error log, task still `Pending`, DLQ empty. After re-declaring the topology the task is `Succeeded`, stock is `(10, 2, 8)` and the queues are empty. |
| 1 | Orders list | `OrderQueryTests.Order_list_filters_sorts_and_pages`. Covers channel, status, customer, inclusive `from`/`to` with a +07:00 offset, `search=50%25` (the customer "500 off …" must not match), search by external id, newest first by default, `sortDir=asc`, `totalAmount`, `externalOrderId`, paging, and an unknown channel. `Invalid_order_list_parameters_return_validation_problem` covers `sortBy=unknown`, an unknown status, an inverted range and `pageSize=101`, each returning 400. |
| 2 | Order detail and 404 | `Order_detail_has_items_and_tasks_with_every_attempt`: product data and line totals; attempt #1 `timeout` on `instance-a`, #2 succeeded on `instance-b`. `Unknown_order_returns_404_order_not_found`. |
| 3 | Task search | `ProcessingTaskEndpointsTests.Task_search_filters_on_task_and_attempt_fields`. `status=Failed`, `errorCode=timeout`, `workerName=OrderWorker`, `attemptNo=2`, `workerInstanceId` and `taskType` each return exactly the expected tasks. Combining `workerName` with `errorCode` or `workerInstanceId` matches only when a single attempt satisfies all of them. Also covers range, ascending order, `lastAttempt` null / `attemptCount` 0, and invalid parameters returning 400. |
| 4 | Task detail shows the publish state | `Task_detail_shows_attempts_and_the_publish_state_of_its_message`: `Pending` with 1 publish attempt and `lastError`, then `Published` with `publishedAt`. `Task_without_an_outbox_row_has_no_message_and_unknown_task_is_404`. |
| 5 | Manual retry end to end | `ManualRetryEndToEndTests.Failed_task_retried_manually_is_processed_again_and_the_order_is_reserved`. It uses the real API, Outbox Publisher, RabbitMQ and Order Worker. Steps: attempt #1 `check_violation` → `Failed` and the original message is in the DLQ → 202 → attempt #2 `Succeeded` → order `Reserved`, stock `(10, 4, 6)`. The test then checks: `GET /api/orders/{id}` shows both attempts; the new outbox row is `Published`; there are 2 outbox rows; and the event's `requestedBy` equals the manager's user id. |
| 6 | Fresh budget | `Manual_retry_gives_a_fresh_attempt_budget_while_the_failure_persists`, with `MaxAttempts` 3 and permanent timeouts. The task has attempts 1–3 → `Failed`, then after the retry attempts 4–6 → `Failed`. `AttemptBudgetStart` is 3, and `processing.failed` is logged for attempt 3 and attempt 6. |
| 7 | Retry rules | `Retry_of_a_task_that_is_not_failed_is_409` (Pending, RetryScheduled, Succeeded). `Concurrent_retries_of_one_failed_task_accept_exactly_one`: one 202, one 409, 2 outbox rows, 1 event. `Retry_copies_the_original_message_and_records_who_asked`. `Retry_without_the_original_outbox_row_is_409_and_unknown_task_is_404`. |
| 8 | Authorization | `Staff_cannot_retry_but_can_read` (403; the task stays `Failed`; anonymous task list is 401) and `Anonymous_order_list_is_401`. |
| 9 | `inventory.imported` | `InventoryImportTests.Applied_import_writes_an_audit_event_with_the_request_correlation_id`: payload `{A1/…, 2, 42}`, the request's correlation id, and no second event for a rejected re-import. |
| Unit | Validators, budget arithmetic, domain | `OrderAndProcessingQueryValidatorTests`, `AttemptBudgetTests`, `ProcessingAttemptFlowTests` (manual retry: budget, kept attempts, numbering continues, non-Failed statuses rejected). |
| AC | No N+1 | `QueryShapeTests.Order_detail_is_one_statement_and_lists_are_count_plus_page` |

## Deviations from the task
1. **The doc's `processing.retry` routing key is not used**, as the task specifies. The republished copy uses its original routing
   key (`order.created`).
2. **Default sort direction.** Time fields sort descending unless the request has `sortDir=asc`. This covers `createdAt` on both
   endpoints and `updatedAt` on tasks. `totalAmount` and `externalOrderId` keep the usual ascending default. The task only said
   that `createdAt` defaults to descending.
3. **`search` is ignored on the task list.** The task does not list it as a filter. This is documented on the query type, the same
   way as for inventory movements.
4. **Order lines are sorted by SKU.** No line number is stored, and UUIDv7 ids are not ordered within the same millisecond, so
   request order cannot be rebuilt reliably.
5. **`RetryCount` is not changed by a manual retry.** It still counts automatic retries. The manual retry is recorded by
   `AttemptBudgetStart` and the audit event.
6. **The copied message's envelope `occurredAt` is the retry time.** It comes from the new outbox row's `CreatedAt`. The payload
   itself, including `receivedAt`, is unchanged.
7. **Extra test seams:** `FailureInjection.OnlyTaskId`/`Heal()` and `ApiFactory.CreateClientAs(role, userId)`. The end-to-end
   retry tests run a Workers host against the API test database, so failures are injected only for that test's own task.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- DLQ copies still pile up: a manual retry republishes from the outbox and leaves the DLQ copy. Purge or re-drive tooling is not
  built yet (review 007 P3).
- There is no read endpoint for `SystemEventLogs` (`processing.retry_requested`, `inventory.imported`, `processing.failed`). It is
  planned for tasks 017/018.
- A manual retry of a task whose original outbox row was purged returns `retry_source_missing`. There is no retention job yet; when
  one is added, it must keep the rows of `Failed` tasks.
- `OrderEndToEndTests` still starts its own broker for each test. It could reuse `RabbitMqBrokerFixture`.

## Manual steps for the user
1. Apply the migration `ProcessingTaskAttemptBudget` with the migrator role:
   ```powershell
   $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
   dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
   ```
   Stop and restart the running API and Workers afterwards. They were started before this change, and the new code needs the
   new column.
2. Manual acceptance check, with API and Workers running:
   - Order that needed a retry: repeat the task-007 check (stop PostgreSQL right after submitting an order, start it again). Then
     `GET /api/orders?sortBy=createdAt&pageSize=1` gives the order id, and `GET /api/orders/{id}` should show the task with
     attempt #1 `Failed` (`timeout`) and attempt #2 `Succeeded`, each with worker name and instance.
   - Retry a failed task: use `GET /api/processing/tasks?status=Failed` to find a `Failed` task (for example one of the
     `processing.failed` tasks from the task-007 check). As Manager, call `POST /api/processing/tasks/{id}/retry`. Expect 202.
     Then `GET /api/processing/tasks/{id}` shows `message.status` going from `Pending` to `Published`, and the task becomes
     `Succeeded` if the cause of the failure is gone. If it fails again, it has attempted `OrderWorker:MaxAttempts` new times.

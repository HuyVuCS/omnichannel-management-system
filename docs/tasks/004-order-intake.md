# Task 004 — Step 5: Order intake `POST /api/orders/{slug}` (decision A: outbox first)

Status: Open
Written by: Cowork, 2026-09-29
Depends on: 003 (message contract, `MessageJson`, error mapping)

## Part A — Follow-ups from review 003 (separate commit)
1. **Validation error keys use the JSON (camelCase) names**, e.g. `items[0].productId`, `pageSize`, `documentNumber`,
   so the frontend can map errors to form fields. Configure FluentValidation globally (property-name resolver producing
   camelCase for every segment of the path) in one place at API start-up; fix the manually built failure in
   `InventoryImportService` to use the same format. Update existing tests that assert keys. Add an integration test:
   `POST /api/inventory/import` with an unknown product at index 1 → error key `items[1].productId`.
   Model-binding (automatic) 400s keep ASP.NET's own keys; document that difference on `ValidationProblemResponse`.
2. **Stock overflow:** in the import, PostgreSQL `22003` (`numeric_value_out_of_range`) from the `UPDATE` → roll back,
   409 `stock_limit_exceeded`. Integration test: set a product's `TotalStock` near `int.MaxValue` directly in the test
   database, import 1,000,000 → 409, stock unchanged.

## Decision (user, 2026-09-29): option A
The API does **not** create the order and does **not** talk to RabbitMQ. It validates, normalises, and writes a
`ProcessingTask(OrderProcessing)` plus an `OutboxEvent("order.created")` in **one transaction**, then returns 202.
The Outbox Publisher (step 6/8) publishes; the Order Worker (step 6–7) creates the `Order` inside the reserve-stock
transaction exactly as in flow 8.2. The API keeps accepting orders while RabbitMQ is down.

## Endpoint: `POST /api/orders/{slug}` — AnyStaff
Request (doc §3.1): `{ "externalOrderId": "...", "customerId": "...", "items": [ { "productId": "...", "stock": 2 } ] }`

Validation (400): `externalOrderId` required, trimmed, ≤ 128; `customerId` non-empty; `items` 1–100;
`productId` non-empty and distinct; `stock` 1–10,000.

Flow (`Application/Orders/IOrderIntakeService`, implementation in `Infrastructure/Persistence/Orders/`):
1. Channel by slug; missing **or inactive** → 404 `channel_not_found`.
2. Customer must exist → otherwise 400 on `customerId`. All products must exist → otherwise 400 on `items[i].productId`.
   (Stock is **not** checked here: availability is decided atomically by the worker.)
3. Build the normalised order (`Application/Orders/NormalizedOrder`, the doc's "Common Order DTO"; the webhook will
   reuse it): `taskId, channelId, channelSlug, externalOrderId, customerId, items[{productId, stock, unitPrice}],
   receivedAt, receivedBy`. `unitPrice` = current `Product.Price` (price snapshot at intake).
4. One transaction: insert `ProcessingTask(OrderProcessing, orderId: null, correlationId, messageId, idempotencyKey)`
   and `OutboxEvent(order.created, aggregateId = taskId, payload = OrderCreatedPayload, correlationId)`.
   Generate both ids up front: `messageId = outboxEventId.ToString()` (the outbox id is the RabbitMQ messageId).
5. `correlationId` comes from `ICorrelationContext` (added in task 003; the API implementation returns the request's
   `X-Correlation-Id`). The controller does not pass it.

Response **202 Accepted**: `{ "taskId": "...", "status": "Pending", "correlationId": "...", "duplicate": false }`.

### Idempotency at intake
Two identical submissions (double click, webhook retry, concurrent requests) must produce **one** task.
- Migration `ProcessingTaskIdempotencyKey`: add `ProcessingTask.IdempotencyKey` (`varchar(200)`, nullable) with unique
  index `UX_ProcessingTasks_IdempotencyKey` filtered `"IdempotencyKey" IS NOT NULL`. Add it as an optional constructor
  parameter of `ProcessingTask`.
- Key: `$"order:{channelId:N}:{externalOrderId}"`.
- On unique violation: clear the change tracker, load the existing task, return 202 with its **current** status and
  `duplicate: true`. Nothing else written.
- Consequence to document in the report: an external order id that was rejected (e.g. insufficient stock) cannot be
  resubmitted under the same id; the channel must send a new id. The worker keeps `UX_Orders_ChannelId_ExternalOrderId`
  as the final guard.

### Message contract
`Application/Common/Messaging/OrderCreatedPayload` = the normalised order. Serialised with `MessageJson`.

## Tests (required)
Integration:
1. Staff posts to `website` → 202; exactly one `ProcessingTask` (`OrderProcessing`, `Pending`, `OrderId` null,
   `MessageId` = outbox id) and one `Pending` outbox row `order.created`; payload has the price snapshot; **no `Orders` row**.
2. `X-Correlation-Id` header value is stored on both rows and returned in the body.
3. Same request again → 202, same `taskId`, `duplicate: true`, still one task/outbox row.
4. 10 concurrent identical requests → one task and one outbox row; all responses carry the same `taskId`.
5. Same `externalOrderId` on two different channels → two tasks.
6. Unknown slug → 404; inactive channel → 404; unknown customer / product → 400 and nothing written.
7. Validation: empty items, duplicate productId, `stock: 0` → 400.
8. Anonymous → 401.
9. Works with RabbitMQ stopped (the API has no RabbitMQ dependency at all in this task).
Unit: request validator; idempotency key format.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (unit/integration counts).
- [ ] Migration + clean `has-pending-model-changes`; new migration SQL in the report.
- [ ] Controller only binds, passes slug and body, maps the result to 202.

## Out of scope
RabbitMQ, Outbox Publisher, Order Worker, stock reservation, `GET /api/orders*`, webhook endpoint and signature
verification, SignalR notifications.

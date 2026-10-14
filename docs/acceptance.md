# Acceptance — doc §16, criterion by criterion

Task 027. For each acceptance criterion of the architecture document (section 16) — the Vietnamese wording below is
this repository's rendering; the document itself (`THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx`) is
kept in the Cowork project, not here — what proves it:

- **Backend tests** (`backend/src/Omnichannel.Tests`, xUnit; integration tests run PostgreSQL, RabbitMQ and the
  storage emulator in containers), by class and test name;
- **Frontend tests** (`frontend/src/**/*.test.tsx`, Vitest + jsdom, axe-core after every test);
- **`scripts/smoke/task-027.ps1`** — the block (B1–B8) and the steps of the `-Mode Full` run whose table is in the
  report of task 027 (step numbers of that run);
- **End-to-end specs** (`frontend/e2e/*.spec.ts`, Playwright, Chromium, axe-core with colour contrast);
- the **load-test baseline** of task 026b (`docs/implementation-log.md`, "Task 026b result");
- the **live evidence** of the release run (section "Runs").

How to run them: README "Acceptance". Known limitations: at the end.

## Runs

| Run | Where | Commit | Result |
| --- | --- | --- | --- |
| `task-027.ps1 -Mode Full` | throwaway stack `omni027` (production compose + acceptance override, 2 Workers), 2026-10-14 | (task 027 report) | 106 / 106, every criterion PASS (14: one step skipped, see 14) |
| `npm run e2e` × 5, `E2E_STACK_CONTROL=1` | `omni027`, `vite preview` of the build for it, 2026-10-14 | (task 027 report) | see the report of task 027 |
| `task-027.ps1 -Mode NoDrills` | development, 2026-10-14 | (task 027 report) | see the report of task 027 |
| `npm run e2e` | development (dev server), 2026-10-14 | (task 027 report) | see the report of task 027 |
| CI job `acceptance` | GitHub Actions (`-Mode Full` + `npm run e2e`) | (task 027 report) | see the report of task 027 |
| Live: `task-027.ps1 -Mode NoDrills -Live`, `npm run e2e` | production (Vercel origin + public API) | (task 027 report) | see the report of task 027 |

## Criteria

### 1. Orders from Website / POS / Digital Platform
*Nhận đơn hàng từ ba thành phần Website, POS và Digital Platform.*

- Backend: `OrderIntakeTests` (`Staff_order_is_accepted_as_task_and_outbox_event_without_creating_an_order`,
  `Unknown_or_inactive_channel_is_not_found`, `Anonymous_cannot_submit_orders`), `OrderEndToEndTests`
  (`Order_submitted_through_the_api_is_reserved_by_the_worker_with_one_correlation_id`), `WebhookEndToEndTests`,
  `WebhookEndpointsTests`, `WebhookSignatureTests`.
- Frontend: `orderEntry.test.tsx` (three panels, outcome by push and by polling).
- `task-027.ps1` B1: one order per slug through `POST /api/orders/{slug}` → task `Succeeded`, order `Reserved`; Full:
  a signed webhook on website.
- E2E: `order-entry.spec.ts` — Staff enters one order in each panel, each shows "Reserved" with the hint received over
  the WebSocket, all three in the Orders list.

### 2. Orders normalised and put on RabbitMQ
*Đơn hàng được chuẩn hoá và đưa vào RabbitMQ.*

- Backend: `OrderIntakeTests.Correlation_id_header_is_stored_on_task_and_outbox_and_returned`,
  `WebhookEndpointsTests.Orders` (the channel's shape → the normalised order), `OutboxPublisherTests`,
  `MessageEnvelopeTests`, `OrderWorkerTests.Order_with_enough_stock_is_reserved_with_movement_outbox_and_attempt`.
- `task-027.ps1` B1: the task names its message id; one attempt by the `OrderWorker` with its instance id; the message
  (`order.created`) `Published` with `PublishedAt` (API; Full also `psql`); the order normalised (channel, customer,
  product line with the server price); the webhook order of the same shape.

### 3. Duplicate orders not processed twice
*Đơn trùng (cùng kênh, cùng mã đơn ngoài) không bị xử lý hai lần.*

- Backend: `OrderIntakeTests` (`Resubmitting_the_same_order_returns_the_existing_task`,
  `Concurrent_identical_submissions_create_one_task`, `Resubmission_with_different_content_is_a_conflict_and_writes_nothing`,
  `Same_external_order_id_on_two_channels_creates_two_tasks`), `OrderWorkerTests`
  (`Duplicate_delivery_is_acknowledged_without_a_second_reservation`), `OrderWorkerTests`
  (`Delayed_copy_arriving_after_success_is_acknowledged_without_a_second_reservation`).
- `task-027.ps1` B2: the same id again → `202`, `duplicate: true`, the same task; the same id on another channel → a
  second order; Full: load scenario `duplicates` (50 ids sent twice at once → 50 orders); invariant "one order per
  channel and external order id".
- Load baseline 026b: 200 requests / 100 ids → 100 orders.

### 4. Concurrent orders do not oversell
*Nhiều đơn đồng thời cho cùng một SKU không bán vượt tồn kho.*

- Backend: `OrderWorkerTests` (`Concurrent_orders_for_the_last_units_never_oversell`,
  `Concurrent_multi_item_orders_in_opposite_order_and_imports_do_not_deadlock`, `One_short_item_reserves_nothing`),
  `InventoryTests` (domain bounds).
- `task-027.ps1` B3 (Full): load scenario `contention` — 60 orders for one product with a stock of 20 → 20 reserved,
  40 refused; invariants "Available = Total − Reserved, nothing negative".
- Load baseline 026b: 300 orders / stock 100 → 100 reserved, 200 refused.

### 5. Total / Reserved / Available stock correct
*TotalStock, ReservedStock, AvailableStock luôn đúng.*

- Backend: `InventoryTests`, `InventoryImportTests` (`Concurrent_imports_for_one_product_are_all_applied_with_movement_and_outbox_each`,
  `Import_beyond_the_maximum_stock_is_a_conflict_and_changes_nothing`), `OrderWorkerTests` (stock after each case).
- `task-027.ps1` B3: P1's figures before / after its orders by the API; Full: invariants "Reserved = lines of the
  Reserved orders" and (new in 027) "Total = imports − deductions, Reserved = reservations − releases − deductions"
  over the whole movement history.

### 6. InventoryMovement + OutboxEvent in one transaction
*Biến động tồn kho và sự kiện outbox được ghi trong cùng một giao dịch.*

- Backend: **`OrderWorkerTests.A_failure_inside_the_reserve_transaction_leaves_neither_movement_nor_outbox_row_and_the_retry_reserves_once`**
  (task 027: the reservation's commit fails after its stock update, order, movement and outbox event were written →
  none of them is left; the retry reserves once), `InventoryImportTests.Import_with_an_unknown_product_is_rolled_back_completely`,
  `InventoryImportTests.Concurrent_imports_for_one_product_are_all_applied_with_movement_and_outbox_each`,
  `OrderWorkerTests.Order_with_enough_stock_is_reserved_with_movement_outbox_and_attempt`.
- `task-027.ps1` B3 (Full): P1's movements each with exactly one `inventory.changed` outbox event (`psql`); invariants
  (new in 027) "every movement has exactly one inventory.changed event" and "every inventory.changed event has its
  movement".

### 7. Stock sync with retry and DLQ
*Đồng bộ tồn kho có retry và DLQ.*

- Backend: `StockSyncWorkerTests` (`Transient_channel_failure_is_retried_with_delay_and_then_succeeds`,
  `Channel_that_stays_unavailable_fails_the_task_after_max_attempts_and_the_message_goes_to_the_dlq`,
  `Manual_retry_reaches_only_the_queue_of_the_task_channel_and_gets_a_fresh_attempt_budget`), `RetryTopologyTests`,
  `RetryBackoffTests`, `OrderWorkerTests.Always_failing_message_fails_the_task_after_max_attempts_and_goes_to_the_dlq`,
  `ManualRetryEndToEndTests`, `ChannelStockClientTests.Sku_rules_fail_the_down_prefix_as_unavailable_on_every_attempt`.
- `task-027.ps1` B4: `SIM-TEMP-` → `Succeeded` at the second attempt (first `channel_unavailable`); Full: `SIM-DOWN-` →
  every sync task of the pair `Failed` after 5 attempts (temporary errors: `channel_unavailable`, then
  `channel_circuit_open` once the channel's breaker opened), one message per failed task in
  `stock.sync.marketplace.dlq`, `StockSyncFailed` alert open → mapping corrected, Manager retries → all `Succeeded`,
  alert resolved; Staff may not retry.
- Frontend: `processing.test.tsx`, `processingTask.test.tsx`, `channelDetail.test.tsx`.

### 8. Near-real-time dashboard
*Dashboard cập nhật gần thời gian thực.*

- Backend: `RealtimeHubTests` (`Staff_client_receives_a_hint_after_a_stock_import`,
  `Listener_reconnects_after_losing_its_connection_and_tells_clients_to_resync`), `RealtimeNotifyTests`,
  `RealtimeHintProducerTests`, `DashboardSummaryTests`.
- Frontend: `realtime.test.tsx`, `dashboard.test.tsx`.
- `task-027.ps1` B5: a WebSocket client on `/api/hubs/notifications` receives `processing.task.changed` (Succeeded),
  `inventory.changed` of the product and `order.created` of an order, the task hint within 5 s of the end of the task.
- E2E: `realtime.spec.ts` — the dashboard's figure changes without a reload within 5 s of the end of an order made by
  another client; with stack control the API container is restarted: indicator reconnecting → live, figures equal to
  a fresh API read.

### 9. Trace an order to task, worker, instance, attempt, error
*Truy vết đơn hàng tới tác vụ, worker, instance, lần thử và lỗi.*

- Backend: `ProcessingTaskEndpointsTests` (`Task_detail_shows_attempts_and_the_publish_state_of_its_message`,
  `Task_search_filters_on_task_and_attempt_fields`), `ProcessingAttemptFlowTests`, `OrderQueryTests`,
  `SystemEventEndpointsTests`, `CorrelationIdTests`.
- Frontend: `orderDetail.test.tsx`, `processingTask.test.tsx`, `logs.test.tsx`.
- `task-027.ps1` B5: Full — the `SIM-DOWN-` flow's stock-sync task: id, type, status, channel, product, correlation
  id, attempts 1–5 by `StockSyncWorker` with instance id, error code and message; `GET /api/system-events?correlationId=`
  lists its `processing.failed`. Both modes — an order that cannot be reserved: its task on `GET /api/orders/{id}`
  with attempt 1 by the `OrderWorker` and its instance, and its `order.insufficient_stock` event by correlation id.
- E2E: `trace.spec.ts` — order detail → its task and attempt (worker, instance, No.) → the flow's stock-sync task with
  the failed first attempt (`SIM-TEMP-`, `channel_unavailable`) → the Logs page filtered by a flow's correlation id.

### 10. Low-stock alert by threshold
*Cảnh báo sắp hết hàng theo ngưỡng.*

- Backend: `AlertLifecycleTests`, `AlertEndpointsTests` (`Manager_acknowledges_an_open_alert_once_with_audit_and_hint_and_acknowledging_again_changes_nothing`,
  `Acknowledge_is_refused_for_a_resolved_alert_for_staff_for_anonymous_and_for_an_unknown_alert`), `AlertKeyTests`, `AlertModelTests`.
- Frontend: `alerts.test.tsx`, `dashboard.test.tsx`.
- `task-027.ps1` B6: threshold 5, stock 6, order of 2 → `LowStock` alert `Open` with stock 4 / threshold 5; Staff may
  not acknowledge, Manager does; an import of 10 → resolved by the system.
- E2E: `alerts.spec.ts` — the alert on the Alerts page and counted on the dashboard card; the Manager acknowledges.

### 11. Inventory and processing history
*Lịch sử tồn kho và lịch sử xử lý.*

- Backend: `InventoryImportTests` (`Movements_are_filtered_and_paged_newest_first`,
  `Movement_carries_the_email_of_the_user_who_made_it_and_null_for_any_other_actor`), `ProcessingTaskEndpointsTests`,
  `SystemEventEndpointsTests`.
- Frontend: `movements.test.tsx`, `processing.test.tsx`.
- `task-027.ps1` B6: P6's movements in order (Import 6, Reserve 2, Import 10) with document, order, series / number
  and user; its stock-sync tasks, all `Succeeded`, each with its `StockSyncWorker` attempt.

### 12. Excel export
*Xuất báo cáo Excel.*

- Backend: `ReportExportEndToEndTests`, `ReportExportEndpointsTests`, `ReportWorkbookTests`, `ReportExportSnapshotTests`.
- Frontend: `reports.test.tsx`.
- `task-027.ps1` B7: Staff 403; Manager's inventory export → `Succeeded` → download link → a ZIP (`.xlsx`) whose
  worksheet contains the SKU.
- E2E: `export.spec.ts` — requested on Reports, "Succeeded" without a reload, the download starts with `PK`.

### 13. Product images in S3-compatible storage
*Ảnh sản phẩm lưu trên S3 (lưu trữ tương thích S3, quyết định của task 019).*

- Backend: `ProductImageEndpointsTests`, `ProductImageHygieneTests` (task 027 A1:
  `A_second_confirmation_of_one_upload_that_loses_the_race_answers_the_product_the_first_one_confirmed`),
  `ImageCleanupServiceTests`, `StoragePublicOriginTests`.
- Frontend: `productImages.test.tsx`.
- `task-027.ps1` B7: presign → `PUT` → confirm → a link that returns the uploaded bytes; the response carries only
  link and metadata; Full: `psql` — `Products` stores key, type, size and time of the image and has no binary column.
- E2E: `image.spec.ts` — uploaded in the product dialog, the thumbnail loads (`naturalWidth > 0`).

### 14. API authentication / authorization
*Xác thực và phân quyền API.*

- Backend: `UserManagementTests`, `TokenRevocationTests`, `JwtAccessTokenIssuerTests`, `JwtOptionsValidatorTests`,
  `UserGuardTests`, `SecurityHardeningTests`, `OrderIntakeTests.Anonymous_cannot_submit_orders`, the role checks of
  each endpoint class (`*EndpointsTests`).
- Frontend: `auth.test.tsx`, `users.test.tsx`.
- `task-027.ps1` B8: no token → 401; a changed signature → 401; a Staff payload claiming Admin under the old signature
  → 401; an expired token is **skipped** (it needs the signing key; the lifetime is the JwtBearer validation with 30 s of
  clock skew, and `JwtAccessTokenIssuerTests.ExpiresAt_follows_configured_lifetime`); the role matrix, one request per cell:

  | Cell | Expected |
  | --- | --- |
  | Admin: create a user / list users | 201 / 200 |
  | Staff: order intake (website, pos, digital-platform) | 202 |
  | Staff: create a product, list users, pay an order, acknowledge an alert, request an export, read the system events, retry a failed task | 403 |
  | Manager: pay an order / acknowledge an alert / request an export / retry a failed task | 200 / 200 / 202 / 202 |
  | Manager: list users | 403 |
- E2E: `access.spec.ts` — Staff has no Users menu, `/users` → the forbidden page, sign-out → login, a protected
  address while signed out → login.

### 15. Tests for the important flows
*Có kiểm thử cho các luồng quan trọng.*

| Flow (CLAUDE.md §19) | At least one test |
| --- | --- |
| Inventory reservation | `OrderWorkerTests.Order_with_enough_stock_is_reserved_with_movement_outbox_and_attempt` |
| Concurrent stock reservation | `OrderWorkerTests.Concurrent_multi_item_orders_in_opposite_order_and_imports_do_not_deadlock` |
| Oversell prevention | `OrderWorkerTests.Concurrent_orders_for_the_last_units_never_oversell` |
| Order idempotency | `OrderIntakeTests.Concurrent_identical_submissions_create_one_task`, `OrderWorkerTests.Duplicate_delivery_is_acknowledged_without_a_second_reservation` |
| Retry | `OrderWorkerTests.Transient_failures_are_retried_with_delay_until_the_attempt_succeeds`, `StockSyncWorkerTests.Transient_channel_failure_is_retried_with_delay_and_then_succeeds` |
| DLQ | `OrderWorkerTests.Always_failing_message_fails_the_task_after_max_attempts_and_goes_to_the_dlq`, `StockSyncWorkerTests.Channel_that_stays_unavailable_fails_the_task_after_max_attempts_and_the_message_goes_to_the_dlq` |
| Transactional outbox | `OrderWorkerTests.A_failure_inside_the_reserve_transaction_leaves_neither_movement_nor_outbox_row_and_the_retry_reserves_once`, `OutboxPublisherTests` |
| Processing task tracking | `ProcessingTaskEndpointsTests.Task_detail_shows_attempts_and_the_publish_state_of_its_message`, `ProcessingTaskTests` |
| Worker attempt tracking | `ProcessingAttemptFlowTests`, `OrderWorkerTests.Redelivery_after_a_crash_abandons_the_running_attempt_and_starts_a_new_one` |
| Authentication / authorization | `UserManagementTests.Only_admins_reach_user_management`, `TokenRevocationTests`, `ProcessingTaskEndpointsTests.Staff_cannot_retry_but_can_read` |
| API validation | `ProductRequestValidatorTests`, `SubmitOrderRequestValidatorTests`, `OrderIntakeTests.Invalid_requests_are_rejected`, `ValidationProblemKeyTests` |

No flow is without a test. The suites as a whole: backend `dotnet test` (≈ 1,940 tests), frontend `npm test`
(≈ 546 tests, axe-core after each), `npm run e2e` (8 specs), CI on every push to `master`.

## Known limitations

- **Simulated integrations**: the three channels, the invoice provider, the mail sender and the payment step are
  simulated in-process (no real external API); the simulators' failure rules exist for drills only (off in production).
- **No production backup** (decision of the user, 2026-10-12): losing Docker Desktop's data or the disk loses all
  production data; after a migration only fix-forward (runbook §6, §7 are drills only).
- **Uptime = the user's machine and Tailscale Funnel**: asleep, off or offline means the backend is down; Funnel is a
  beta with unpublished limits; no monitoring beyond `/health` (runbook §12).
- **One PostgreSQL and one RabbitMQ instance**, no replica, no failover; RabbitMQ is not backed up (runbook §8 for
  tasks whose messages are lost).
- **One accessibility exception**: the options list of an AntD `Select` has no accessible name and the component
  offers no way to give one (`frontend/src/test/accessibilityException.ts`, shared by the jsdom and the browser
  checks). The focus of checkboxes, radios and switches is still drawn by AntD on a sibling element (task 027 report).
- **Recorded "revisit when" items** of `docs/implementation-log.md`: indexes not added (`updatedAt` sort of the task
  list, `Products.ImageKey`, unused indexes not dropped), `ReportExports` rows kept without a purge, bearer token in
  `sessionStorage` (until frontend and API share a site), `style-src 'unsafe-inline'`, third-party images pinned by
  digest and updated only by a release, the load-test numbers being a one-machine baseline.
- **Chunked oversize bodies through Funnel** may be answered 502 by the tunnel (review 026c q7; `_hardening.ps1`
  accepts it for a public origin only).

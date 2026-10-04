# Task 020 — Excel export in the background (ClosedXML + object storage); Part A (review 019)

Status: Open
Written by: Cowork, 2026-10-05
Depends on: 019 (accepted)

## Context
Doc §1.1 ("Xuất Excel"), §4.2 ("File Excel lớn cũng có thể lưu S3"), §7.1 (ReportService, FileService), §11
`POST /api/reports/orders/export`, `POST /api/reports/inventory/export`, §13 ("Export lớn → Background export task +
S3"), §16 ("Có export Excel"); CLAUDE.md §9–12, §15, §17.

**Decisions (user, 2026-10-05):** every export runs **in the background** (no synchronous path); only **Manager/Admin**
may export; files are kept **7 days**. Every other decision of this task is in the implementation log, "Task 020 plan" —
read it first; doc deviations 22–25 are listed there.

This task is backend only. The frontend shows the new task type and event types with their raw names until task 021
(both pages already fall back to the raw value); no frontend file changes in this task.

Read first: implementation log "Task 020 plan", "Task 019 plan" and "Task 019 result", "Task 004"/"Steps 4–5"
(decision A: task + outbox in one transaction), "Task 007 plan" (retry tiers, final failure), "Task 008 plan" (manual
retry), "Task 017 plan" (`AlertStore`, `ProcessingFailed`), "Task 018b result" (allowlist); review 019; backend
`OrderIntakeService` (task + outbox + 202), `StockSyncService` and `StockSyncMessageHandler` / `StockSyncMessageParser`
/ `StockSyncWorkerService` (a worker with attempts, rejection codes, final failure, alerts), `QueueMessageHandling.cs`,
`ResilientQueueConsumer`, `RabbitMqTopology`, `WorkerHostSetup.cs`, `ProcessingErrorClassifier`, `OrderQueryService`
(filters of `GET /api/orders`), `InventoryQueryService` (filters of `GET /api/inventory`), `SystemEventDetails`,
`MessageRejection.cs`, `IObjectStorage` / `S3ObjectStorage` / `ObjectStorageServiceCollectionExtensions`,
`RealtimeHintInterceptor` (task changes already send `processing.task.changed`), `Integration/ApiFactory.cs`,
`Integration/StorageEmulator.cs`, the Workers integration fixture; `scripts/setup-object-storage.ps1`,
`scripts/smoke/_common.ps1` (`Start-SmokeWorkers`), `task-019.ps1`; README "Product images", "System events".

**Stop and report (`Blocked`)** if: the emulator cannot store an object from the SDK or serve it with an attachment
`Content-Disposition` by either way described in C4; ClosedXML writes a value that starts with `=` as a formula and no
value-only API prevents it; generating an export at `MaxRows` needs more than 1.5 GiB in the Workers process (report the
measurement and a proposed limit instead); anything needs a cloud account; or a decision is needed that is not in this
file or the log.

## Part A — Follow-ups of review 019 (one commit, first)

### A1. README "Product images" (P3, review 019 findings and question 2)
- "Switching the provider": the key needs **list** permission on the bucket as well — several S3-compatible services
  answer `HEAD` of a missing object with 403 (not 404) without it, which would turn "not uploaded" into a 503; the 025
  provider check must show a 404.
- The client's early size check is the constant `MAX_IMAGE_BYTES` (`imageUpload.ts`): changing
  `ObjectStorage:MaxImageBytes` upwards needs that constant changed too (a lower limit only costs one presign 400).
- An upload link stays valid for its whole lifetime, also after the confirmation; a second `PUT` with it would replace
  the confirmed object (task 026: copy to a final key at confirmation).

### A2. Development CORS origins (P3, review 019)
- `-s3.allowedOrigins` of the compose service and the emulator of the integration tests: add `http://127.0.0.1:5173`
  and `http://127.0.0.1:4173` next to the two `localhost` origins (one constant in the tests).
- The existing preflight test also covers `http://127.0.0.1:5173`; README CORS paragraph updated.

## Part B — Infrastructure

### B1. Object storage in the Workers host
- `AddObjectStorage()` in the Workers host (same `ObjectStorage` options and validation; `appsettings.Development.json`
  of the Workers gets the endpoint and bucket, no keys). `UnconfiguredObjectStorage` stays for hosts that do not call it.
- `IObjectStorage.PutAsync(key, content, contentType, contentDisposition, cancellationToken)` — stores a seekable
  stream with the given headers; failures as `ObjectStorageException` like the other operations (nothing of the
  storage in the message or the log). Nothing else is added to the interface.
- `scripts/setup-object-storage.ps1` also writes the same pair to the user-secrets of `Omnichannel.Workers`
  (`ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`), same rules (stdin, nothing printed, idempotent). Try it
  only on throwaway targets, as in 019.

### B2. Options `Reports` (Application, bound by both hosts, validator + `ValidateOnStart`)
`MaxRows` (100,000), `MaxUnfinishedPerUser` (3), `Retention` (7 days), `CleanupInterval` (1 h), `CleanupBatchSize`
(100), `DownloadLinkLifetime` (5 min), `MaxRangeDays` (366). Ranges only; the Workers options of the consumer
(`ReportExportWorker:Concurrency` = 1, `MaxAttempts` = 5) as for the other workers.

### B3. Package
`ClosedXML` through Central Package Management: current stable version, pinned; verify the version on NuGet (do not
guess) and name its licence and those of its new transitive dependencies in the report.

## Part C — Backend

### C1. Model and migration `ReportExports`
- `ProcessingTaskType.ReportExport`.
- Entity `ReportExport` (`AuditableEntity`; `CreatedBy` is the requester): `TaskId` PK + FK → `ProcessingTasks`
  (1:1, no separate id), `ReportType` varchar(32) (`Orders`, `Inventory`), `Parameters` jsonb (normalised filters,
  `timeZone`, `language`), `FileName` varchar(100), `ExpiresAt` timestamptz not null (= creation + `Retention`), result
  `ObjectKey` varchar, `SizeBytes` bigint, `RowCount` int, `CompletedAt` timestamptz — the four result columns all null
  or all set (check constraint) — and `DeletedAt` timestamptz null. Domain methods for "completed" and "deleted"; no
  public setters.
- Indexes: `(CreatedBy, CreatedAt DESC)` for the requester's list; partial `(ExpiresAt) WHERE "DeletedAt" IS NULL` for
  the cleanup. Migration generated, not applied by Claude Code to development.

### C2. Requests — `ReportsController`, Manager/Admin
- `POST /api/reports/orders/export` — body: `channel`, `status`, `customerId`, `search` (the filters of
  `GET /api/orders`, same meaning — reuse the list's filter code, do not copy it), `from` and `to` **required**,
  `to ≥ from`, range ≤ `MaxRangeDays`; `timeZone` required (IANA id, valid when `TimeZoneInfo.TryFindSystemTimeZoneById`
  finds it on the host — verify on Windows and in the Linux test container). Rows = orders + their item lines.
- `POST /api/reports/inventory/export` — body: `search`, `lowStockOnly` (the filters of `GET /api/inventory`, reused),
  `timeZone`. Rows = products.
- Both, in this order: validation (400, localised en/vi); unfinished exports of this user (`Pending`, `InProgress`,
  `RetryScheduled`) ≥ `MaxUnfinishedPerUser` → 409 `too_many_exports`; a `COUNT` with the same filters > `MaxRows` →
  400 `export_too_large` (nothing written). Then **one transaction**: `ProcessingTask(ReportExport)`, the
  `ReportExport` row (language = the request's UI culture, file name e.g. `orders_20261005-1030.xlsx` from the local
  time in `timeZone`, ASCII only), `OutboxEvent(report.export.requested)` with payload `{ taskId }` (the parameters are
  read from the database by the worker), `SystemEventLog` `report.export_requested` (Information; `TaskId` column;
  details `reportType`, `requestedBy` — `requestedBy` is already an actor field resolved to an e-mail by the Logs
  endpoint). Answer **202** `{ taskId }` with `Location: /api/reports/exports/{taskId}`.
- The limit check is check-then-insert: two simultaneous requests may admit one more — accepted, say so in the code.

### C3. Reading and downloading — the requester's own exports only
- `GET /api/reports/exports` — paging contract (`page`, `pageSize`; `sortBy` only `createdAt`, default newest first);
  filter `reportType`. Only rows with `CreatedBy` = the caller (an Admin included).
- `GET /api/reports/exports/{taskId}` — 404 for another user's export (never 403: no existence leak).
- Both answer `{ taskId, reportType, status, parameters, fileName, rowCount, sizeBytes, createdAt, completedAt,
  expiresAt, errorCode }`: `status` = the task's status, or `Expired` when `DeletedAt` is set; `errorCode` = the last
  attempt's code when `Failed`, otherwise null (never the error message).
- `GET /api/reports/exports/{taskId}/download` — 409 `export_not_ready` unless `Succeeded`; 409 `export_expired` when
  deleted; otherwise `{ url, expiresAt, fileName }`, a presigned GET with `DownloadLinkLifetime`. Not audited (the
  request is). The link is a credential: never logged.
- Localised messages for the three error codes (en/vi); `.http` samples.

### C4. Report Export Worker (`Omnichannel.Workers/ReportExportWorker/`)
- Topology: event type `report.export.requested` (constant in `EventTypes`), quorum queue `report.export` bound to it,
  DLX/DLQ `report.export.dlq`, the delay tiers of 007 — declared like the other queues (an existing broker gets the new
  queue at the next start; nothing is changed or deleted).
- Handler, in this order: parse (reuse `MessageRejectionCodes`; the task must be a `ReportExport` task with an export
  row, otherwise `task_mismatch` / `unknown_task`; a new code only if no existing one fits — then constant, case in
  `MessageRejectionTests`, README; the frontend label follows in 021); terminal task → ACK without work; start the
  attempt (committed first, as the other workers); export already deleted or past `ExpiresAt` → permanent `Fail`
  `export_expired`.
- **Read** in one `REPEATABLE READ, READ ONLY` transaction (orders and items from the same snapshot), streamed without
  tracking, in a fixed order (orders by `CreatedAt`, `OrderId`; items by order then SKU; products by SKU); more than
  `MaxRows` rows → permanent `Fail` `export_too_large` (the data grew after the request's count). Build the workbook
  while reading; no external call in that transaction.
- **Upload** after the read transaction: `PutAsync("exports/{taskId}.xlsx", …)` with content type
  `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` and `Content-Disposition: attachment;
  filename="<FileName>"`. Verify that a presigned GET from the emulator returns that header; if it does not, set it on
  the presigned GET (`response-content-disposition`) instead and say which way works (025 needs it).
- **Complete** in one transaction: export result columns, attempt and task `Succeeded`, resolve the task's
  `ProcessingFailed` alert (as the other workers after a manual retry); commit; ACK. The key is deterministic, so a
  redelivery after the upload and before the commit overwrites the same object.
- Failures: `ObjectStorageException` and the existing transient classes → `Retry` (delay tiers, `MaxAttempts`); final
  failure → task and attempt `Failed`, `processing.failed`, `ProcessingFailed` alert, DLQ — the same as the other
  workers. Manual retry (`POST /api/processing/tasks/{id}/retry`) must work unchanged (test it). Host shutdown during
  generation → `Abandon`.
- Concurrency 1 per instance (prefetch 1); several instances may run.

### C5. Workbook content (ClosedXML; texts from resx, en and vi, by the export's stored language)
- **Orders:** sheet *Orders* — created at, channel, external order id, customer name, customer phone, status (label),
  item count, total amount, order id; sheet *Items* — order created at, external order id, channel, SKU, product name,
  quantity (`Stock`), unit price, line total (a value, not a formula), order id. No customer e-mail or address.
- **Inventory:** one sheet — SKU, name, price, total, reserved, available, low-stock threshold, low stock (yes/no label,
  the shared predicate), expiry date, stock updated at.
- Both: a sheet *Info* — report, filters as entered (channel by name, customer by name), time zone, generated at,
  requested by (e-mail), row counts.
- Format: header row bold, frozen, with auto-filter; fixed column widths (no `AdjustToContents` over data rows);
  numbers as numbers (money `#,##0`), timestamps as Excel date-times **converted to `timeZone`** with the zone in the
  header text, dates as dates. **Every text is written as a value, never as a formula** (a customer named `=1+1` or
  `@SUM(A1)` stays text — test it on the re-opened file).

### C6. Cleanup (`ReportExportCleanupService`, Workers host)
Every `CleanupInterval` (first run shortly after start-up): up to `CleanupBatchSize` exports with `ExpiresAt <= now`,
`DeletedAt IS NULL` and a **terminal** task; delete `exports/{taskId}.xlsx` for each (outside any transaction; a
missing object is success — this also removes the file of a failed export whose upload happened); then one
`UPDATE … SET "DeletedAt" = now WHERE "TaskId" = ANY(@deleted) AND "DeletedAt" IS NULL`. A delete that fails is logged
as a warning (task id, not the key's link) and retried next round; several instances may delete the same object
(idempotent). Batches repeat until none is left; cancellation-aware.

### Backend tests
Integration (API + Workers fixture, RabbitMQ and the storage emulator in containers):
- Requests: 202 + task + export row + outbox + audit event, all or nothing (failure injected before commit → none of
  the four); every 400 of both endpoints (missing/inverted range, range above the limit, unknown time zone, invalid
  status) in en and vi; `export_too_large` with nothing written; `too_many_exports`; Staff 403, anonymous 401.
- Reading: list and detail show only the caller's exports (another Manager → 404, not in the list; an Admin → 404 for a
  Manager's export); `status` `Expired`; `errorCode` of a failed export, never its message.
- End to end: orders and inventory exports reach `Succeeded`; `download` gives a link that an `HttpClient` **without**
  `Authorization` reads; the bytes open with ClosedXML: sheets, headers in **vi and en**, row counts, numeric cells,
  date-times converted to the time zone (a zone with daylight saving time, e.g. `Europe/Berlin`, across a change), the
  formula-looking customer name as text; `Content-Disposition` attachment with the file name.
- Worker: duplicate delivery of a finished task → one attempt, one upload; redelivery after the upload and before the
  commit (failure injection) → one object, `Succeeded`, two attempts; storage stopped → `RetryScheduled`, then
  `Succeeded` after it is back; storage stopped for all attempts → `Failed` + `ProcessingFailed` alert + DLQ, then
  manual retry → `Succeeded` and the alert resolved; rows above `MaxRows` at the worker → `Failed` `export_too_large`
  without retry; expired export → `Failed` `export_expired`; poison messages → their codes; snapshot: an order inserted
  from another connection between the orders and the items query is in neither sheet (command interceptor hook).
- Cleanup: an expired succeeded export → object gone, `DeletedAt` set, download 409 `export_expired`; an expired failed
  export whose object exists → object gone; a non-terminal one untouched; storage stopped → nothing marked, done on the
  next round.
- `processing.task.changed` is sent for the export task's status changes (existing interceptor — one assertion).
Unit: workbook builder (headers per language, cell types, formats, time-zone conversion, formula-looking text), file
name, parameter normalisation, `Reports` options validator, the cleanup's selection.
Existing tests green; list any changed (e.g. task-type enumerations) and why.

## Acceptance criteria
- [ ] Part A in a separate commit first.
- [ ] Backend build 0 warnings, all tests pass. No frontend file changed (so no frontend runs are required).
- [ ] Migration `ReportExports` generated; smoke run first on a **throwaway** environment (PostgreSQL, RabbitMQ, the
      emulator through the compose service, migrator role, `omnichannel_app`), as 019 did; the user applies it to
      development afterwards.
- [ ] **Measurement** on the throwaway environment (data seeded by SQL there only): an orders export and an inventory
      export at `MaxRows` — duration, file size, peak working set of the Workers process. Table in the report.
- [ ] No secret, key id, presigned link or connection detail in logs, ProblemDetails, the repository or the report;
      log scan of the throwaway API and Workers as in 019.
- [ ] `scripts/smoke/task-020.ps1` (strict `Assert-Step`, A5 table format; API and Workers — `Start-SmokeWorkers`;
      the emulator must be up — a clear failed step if it is not): smoke Manager, second Manager, Staff; Staff export →
      403; invalid range → 400; orders export → 202; poll the export until `Succeeded` (bounded wait); its detail
      (row count); download link → bytes start with `PK` and the header is an attachment; the second Manager → 404 on
      detail and download; inventory export → `Succeeded`; `report.export_requested` visible in `GET /api/system-events`
      with the requester's e-mail; lock smoke users. No frontend checks in this script. Table in the report.
- [ ] README: section "Reports and Excel export" (flow, roles, limits, configuration, retention, file contents, how
      to read a failed export on the Sync & Processing page), "System events" allowlist table, setup steps (setup script
      again for the Workers), `.http` samples; doc deviations 22–25 confirmed or corrected in the report.
- [ ] "Manual steps for the user" in the report: run the setup script again (Workers user-secrets), migration
      `ReportExports`, restart the Workers host — nothing else.

## Out of scope
The Reports page and every frontend change (021); exports of movements, processing tasks, alerts or logs; scheduled or
e-mailed reports; CSV or other formats; charts or formulas in the workbook; streaming writers other than ClosedXML;
templates; sharing an export with another user; production provider and public storage endpoint (025); orphan sweep
for images (026).

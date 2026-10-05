# Task 021 — Frontend: Reports & Excel export page; labels for exports; Part A (review 020: export end of life; review 019: expired image links)

Status: Open
Written by: Cowork, 2026-10-05
Depends on: 020 (accepted)

## Context
Doc §3.2 (page "Báo cáo & Excel"), §3.4 (failed task → error detail and retry), §11 (export endpoints), §12
(realtime → refetch), §16 ("Có export Excel"); CLAUDE.md. Task 020 delivered the export API (README "Reports and Excel
export"): `POST /api/reports/{orders|inventory}/export` → 202 `{ taskId }`, `GET /api/reports/exports`,
`GET /api/reports/exports/{taskId}`, `GET /api/reports/exports/{taskId}/download` — Manager/Admin, an export is visible
to its requester only.

Every decision of this task is in the implementation log, "Task 021 plan" and "Task 020 result" (the decisions of
review 020) — read them first.

Read first: log "Task 021 plan", "Task 020 plan/result", "Task 019 result" (stable image links), "Task 018b result"
(request guard, logs labels), "Task 016 plan/result" (realtime client, tag map, order-outcome polling while not live),
"Task 018 result" (form-dialog reset, wide tables); reviews 019 and 020; README "Reports and Excel export", "Frontend",
"Alerts", "System events"; frontend `app/navigation.tsx`, `app/router.tsx`, `features/logs/*` (restricted page,
`LOGS_ROLES`, `eventSummary.ts`, `systemEventsApi.ts`), `features/processing/*` (`TASK_TYPES`, `TaskStatusTag`,
`paths.ts`), `features/orders/OrdersPage.tsx` and `components/OrderOutcome.tsx` (polling while not live),
`features/inventory/InventoryPage.tsx`, `features/customers/customersApi.ts` (`getCustomer`),
`features/realtime/hintTags.ts`, `features/products/imageLinks.ts`, `components/ProductThumbnail.tsx`,
`components/ProductImageField.tsx`, `shared/utils/dateRange.ts`, `shared/hooks/useListQueryState.ts`,
`shared/api/problemDetails.ts`, `test/*`; backend `ReportExportCleanup.cs`, `ReportExportService.cs`,
`ProcessingTaskService.RetryAsync`, `ReportExportProcessingService.CompleteAsync` (how it resolves an alert),
`AlertStore.ResolveAsync`, `Directory.Packages.props`; `scripts/smoke/task-020.ps1`, `_common.ps1`.

**Stop and report (`Blocked`)** if: enabling transitive pinning (A3) changes the resolved version of any package other
than `SixLabors.Fonts`; the stored `Content-Disposition` does not make a real browser save the file (the download
would navigate away or open the file inline); the page needs an API change not listed here (a new field, a cancel or
delete endpoint, another filter); or a decision is needed that is not in this file or the log.

## Part A — Follow-ups (separate commits, first; A1–A4 backend/script, A5 frontend)

### A0. Pending development steps of 019 and 020 (new workflow, user 2026-10-05)
Before any change, apply on development what 019 and 020 left for the user, following "Development steps" in
`docs/tasks/README.md` (read it first; it is new):
1. `scripts\setup-object-storage.ps1` (stores the keys in the Workers user-secrets too).
2. `docker compose … up -d objectstorage` (recreates the emulator with the `127.0.0.1` CORS origins of 020 A2; data kept).
3. `dotnet ef migrations list`, then `dotnet ef database update` as the migrator: pending must be `ReportExports` and
   possibly `ProductImage` — nothing else (both are additive and were smoke-tested on throwaway environments).
4. Smoke on development: `task-019.ps1` (if 019 was never run there) and `task-020.ps1`; tables in the report.
If `OMNICHANNEL_MIGRATOR_CONNECTION` is not set, do steps 1–2, record step 3 as the one manual step with that reason,
and run the rest of the task's checks on a throwaway environment as in 020.

### A1. End of an export's alert (P3, review 020 question 1 and finding 2)
- **Sweep:** `ReportExportCleanup` sets `DeletedAt` **and** resolves the `ProcessingFailed` alert of the same tasks
  (`AlertDedupKey.ProcessingFailed(taskId)`) in **one transaction** (`AlertStore` is written only inside the transaction
  of the cause). Resolving a key without an unresolved alert is a no-op, so the keys of all exports whose file was
  deleted in the batch may be passed. The resolution sends `alert.changed` the way the other resolutions do (use the
  same mechanism as `CompleteAsync`; do not add a new one).
- **Manual retry:** `ProcessingTaskService.RetryAsync` answers **409 `export_expired`** (the localised message of
  `ValidationMessages.Keys.ExportExpired`) for a `ReportExport` task whose export has `DeletedAt` set or
  `ExpiresAt <= now` — checked after the row lock and the `Failed` check, nothing written (no outbox row, no event).
  Other task types unchanged.
- Tests (integration): an expired failed export with an open `ProcessingFailed` alert → after one sweep round the file
  is gone, `DeletedAt` set, the alert `Resolved` (system resolution), `alert.changed` sent; a failure injected before the
  commit (as 020's failure injection, if the Workers fixture allows it — otherwise say so) → neither the mark nor the
  resolution stored; a retry of an expired failed export → 409 `export_expired` in en
  and vi, task still `Failed`, no outbox row, no `processing.retry_requested` event; a retry of a failed export that is
  not expired → accepted as today.

### A2. Expired by time in the API (P3, review 020 question 2)
- List and detail: `status` = `Expired` when `DeletedAt` is set **or** the task is terminal (`Succeeded`, `Failed`) and
  `ExpiresAt <= now` (the API's `TimeProvider`); `errorCode` stays null for `Expired`. A non-terminal export past
  `ExpiresAt` keeps its task status.
- Download: 409 `export_expired` when `DeletedAt` is set or `ExpiresAt <= now` (checked before "not ready").
- Tests: a succeeded export moved past `ExpiresAt` (SQL in the test) and not swept → `Expired` in list and detail,
  download 409 `export_expired` (en/vi); one second before → `Succeeded` and a link; a pending export past `ExpiresAt` →
  `Pending`.

### A3. Pin `SixLabors.Fonts` (P3, review 020 question 3)
- `Directory.Packages.props`: `CentralPackageTransitivePinningEnabled` = true and `PackageVersion` `SixLabors.Fonts`
  1.0.0, with a comment: 2.x is under the Six Labors Split License — do not raise without a licence decision.
- Verify on NuGet (do not guess) that 1.0.0 is Apache-2.0. `dotnet list package --include-transitive` for every project
  before and after: the resolved versions are identical (diff in the report) — otherwise stop (see above).
- Show that the pin bites: temporarily add a package version that depends on `SixLabors.Fonts` ≥ 2.0 to one project,
  restore, report the error code, revert (not committed).

### A4. Smoke step 17 of task 020 (P3, review 020)
Step 17 of `scripts/smoke/task-020.ps1` reads inverted (expected `False` / actual `False` on success). Make the step
say what it checks with expected `True` on success (e.g. "17 the Report Export Worker reached the object storage",
value `-not $waited.StorageDown`); the explaining `throw` stays. Run `_assert-selftest.ps1` if it covers anything
touched.

### A5. Expired image links on an idle products page (P3, review 019)
A thumbnail (table row, product dialog preview — every place that renders a product image link) whose image fails to
load **after its `expiresAt`** asks for one refetch of the product list (or of the one product for a detail-bound
view). Requirements:
- requests of one burst (twenty rows failing at once) are coalesced into **one** refetch;
- a failure **before** `expiresAt` (storage down, broken object) asks for nothing — so a link that keeps failing can
  never cause a loop;
- after the refetch, `withStableUrl` takes the new link (the old one has expired) and the thumbnail tries it once.
Tests: twenty thumbnails with an expired link fail → one list request, then the new links render; a failure of a
link that has not expired → no request; the new link failing again → no second request.

## Part B — Reports page (`/reports`, Manager/Admin)

### B1. Route, menu, roles
- `features/reports/` with `paths.ts` (`REPORTS_PATH`, `REPORTS_ROLES = ['Admin', 'Manager']`), `reportsApi.ts`,
  `ReportsPage.tsx` (lazy chunk), components as needed. The navigation item loses `plannedTask` and gets `roles`;
  Staff has no menu item and the address answers with the forbidden page (as `/logs`).

### B2. Request card
- A card with a `Segmented` (or tabs) **Orders / Inventory**, one form each:
  - **Orders:** channel (active channels, as the orders page), status, customer (`CustomerSelect`), search, and a
    **required** range of local days (`DatePicker.RangePicker`; default: the last 30 days including today; at most
    `MAX_EXPORT_RANGE_DAYS` = 366 days, a client rule with the server staying the authority — comment like
    `MAX_IMAGE_BYTES`). Sent as instants through `dateRange.ts` (`startOfLocalDay` / `endOfLocalDay`).
  - **Inventory:** search, low stock only.
  - Both: `timeZone` = `Intl.DateTimeFormat().resolvedOptions().timeZone` (fallback `UTC` if it is empty), shown
    read-only ("Times in the file: Asia/Ho_Chi_Minh"); help text that the file is written in the current UI language.
- Submit → 202: success message ("Export requested — it appears below when it is ready"), the table refetched, the
  form keeps its values. Validation errors with a field on the field; `export_too_large`, `too_many_exports` and errors
  without a form field (e.g. `timeZone`) as a form-level message (`problemMessages`). The submit button is disabled
  while the request runs.
- **Shortcut from the lists:** the Orders page and the Inventory page get an "Export to Excel" button (Manager/Admin
  only, next to the existing toolbar buttons) that navigates to `/reports` with the list's current filters in **router
  location state** (not in the address): orders → channel, status, customer, search, `from`/`to` days (the default
  range when the list has none); inventory → search, low stock only. The page opens the matching form prefilled;
  nothing is requested until the user submits. Opening `/reports` directly shows the defaults.

### B3. "My exports" table
- `GET /api/reports/exports`; `page`, `pageSize`, `sortDir` (request time) and `reportType` (filter) in the address
  through `useListQueryState`; default newest first. A reload button as on the other lists.
- Columns: requested at; report (Orders / Inventory); filters (one line from `parameters`: channel by name from the
  channel list, status label, customer by name through `getCustomer` — at most one request per distinct customer,
  search, the range as local days, "low stock only"; `TruncatedText` with the full text in its tooltip); status
  (`TaskStatusTag` for the task statuses, a grey "Expired" tag); rows; size (KB/MB); kept until; actions.
- Actions: **Download** for `Succeeded`; for `Failed` the error-code label (labels for `export_too_large`,
  `export_expired`, `export_parameters_invalid`, `storage_unavailable`; any other code raw) and a link to the task
  detail (`processingTaskPath`), where retry is; nothing for `Expired` and unfinished exports (status only).
- The table fits 1280 px; the action column may be fixed right (log, "Task 018 result").

### B4. Live status
- New realtime tag type `ReportExport` (in `REALTIME_TAG_TYPES`, so it is part of the resync set). In `tagsForEvent`,
  `processing.task.changed` with `taskType === 'ReportExport'` adds `{ ReportExport, taskId }` and
  `{ ReportExport, LIST }` to today's tags; with `all` (or no task id) it adds `ReportExport`. Other task types do not
  touch `ReportExport`.
- The list provides `{ ReportExport, LIST }` and one tag per row; the request mutation invalidates the list.
- While the connection is **not live** and an unfinished export (`Pending`, `InProgress`, `RetryScheduled`) is on the
  page: poll the list every 5 s (the `OrderOutcome` pattern); no polling otherwise.

### B5. Download
- `GET /api/reports/exports/{taskId}/download` as a **mutation** (not cached). On success the page follows the link
  at once with a temporary `<a href rel="noreferrer">` click (no `target`, no new window) and then resets the mutation
  so the link does not stay in the store; the link is never put in the address, a log or a toast.
- 409 `export_expired` / `export_not_ready` → the localised server message and a refetch of the list; 404 → the generic
  not-found message and a refetch.
- The button shows a loading state per row.

### B6. Labels elsewhere
- `TASK_TYPES` gains `ReportExport` (Sync & Processing filter and column, task detail, dashboard `processing` card):
  `processing.taskTypes.ReportExport` in vi/en.
- Logs/Audit: `EVENT_TYPES` gains `report.export_requested` (summary: report type label, requester = `actorEmail`) and
  `report_export.message_rejected` (summary: `reasonCode` label as the other rejected-message types; the codes of the
  export queue are among the existing ones). vi/en texts.

## Part C — Tests (required)
Frontend (MSW, fake hub, every test ends `settled()`; request tests also pass under `npm run test:slow`):
- Role guard: Manager and Admin see the menu item and the page; Staff has no item and gets the forbidden page.
- Orders request: defaults (last 30 days, browser zone shown); the body sent (filters, instants of the local days,
  `timeZone`); a range of 367 days blocked on the client; server field errors en/vi on the fields; `export_too_large`
  and `too_many_exports` as form-level messages; success → message + list refetched.
- Inventory request: body sent; success.
- Shortcut: from the Orders page with filters (and without a range) and from the Inventory page → the form prefilled,
  no request sent until submit; Staff has no shortcut button.
- Table: columns and the filter summary (channel and customer names, range as local days); paging / direction /
  `reportType` in the address; `Expired` and `Failed` rows (labels, raw fallback, task-detail link, no download).
- Live: a `processing.task.changed` hint for a `ReportExport` task refetches the list once (throttled as the other
  lists); a hint for another task type does not; reconnect → resync refetches it; polling only while not live and an
  unfinished export is shown (fake clock).
- Download: the mutation result is followed through an anchor with `rel="noreferrer"` (spy), the link is not in the
  store afterwards; 409 `export_expired` → message + refetch.
- Labels: `ReportExport` on the processing page and the dashboard; both event types on the Logs page.
- A5 tests as listed; `hintTags` unit tests for the new cases.
Backend: A1 and A2 tests as listed; all existing tests green.

## Acceptance criteria
- [ ] A0 applied (commands and results, no secret) before any code change; Part A in separate commits first (A1–A4 may
      share one backend commit; A5 its own).
- [ ] Backend build 0 warnings, all tests pass; A3 before/after list and the "pin bites" error in the report.
- [ ] Frontend typecheck, lint, test, build pass; **5 consecutive full `npm test` runs green** (wall times) and one full
      `npm run test:slow` green (every test red in any slow run fixed or shown to be an application defect, listed).
- [ ] Bundle: first-load set before/after; the reports page is a lazy chunk.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity.
- [ ] `scripts/smoke/task-021.ps1` (strict `Assert-Step`, A5 table format; API and Workers — `Start-SmokeWorkers`; the
      emulator must be up — a clear failed step if not): smoke Manager and Staff; Staff → 403 on the exports list;
      Manager inventory export → `Succeeded` (bounded wait); the list shows it with `status` `Succeeded`; download link
      → bytes start with `PK`; retry of that succeeded task → 409 (unchanged rule); `processing` of
      `GET /api/dashboard/summary` lists `ReportExport`; lock smoke users; `Invoke-FrontendChecks`. Run it on
      **development** after the development steps of this task (A1–A2 change no schema); table in the report.
- [ ] Real-browser check on the production build (`npm run build` + `vite preview`), 1280 px, vi and en: Manager opens
      the orders shortcut from the Orders page, requests the export, sees the status change live to `Succeeded`, and
      downloads the file (the browser saves it under its file name, the page stays; the download request carries no
      `Authorization` header); an inventory export; the Sync & Processing and Logs pages show the new labels; Staff has
      no menu item. On development.
- [ ] README: "Reports and Excel export" gets a short "Reports page" part (roles, shortcut, time zone and language,
      live status, download); A1/A2 behaviour (alert resolved by the sweep, retry refused, expiry by time);
      "Frontend" notes if a new pattern was needed.
- [ ] "Development steps (applied)" in the report; "Manual steps for the user": none, or only a step that could not be
      applied, with the reason.

## Out of scope
Charts or report views beyond the export; scheduled or e-mailed exports; cancelling or deleting an export; sharing an
export; other export types or formats; a choice of time zone or file language on the form; a link from the task detail
to the export (another user's export is 404); polling while live; any change to the export API beyond A1/A2; the
production storage address (025); an export retry button on the Reports page (retry stays on the task detail).

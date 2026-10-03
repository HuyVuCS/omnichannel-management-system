# Task 018b — Logs/Audit: `GET /api/system-events` + Logs/Audit page; Part A (finish 018, in-flight request guard)

Status: Open
Written by: Cowork, 2026-10-03
Depends on: 018 (accepted with the open item of Part A1)

## Context
Doc §3.2 (page "Logs/Audit"), §5.2 (`SYSTEM_EVENT_LOG`: "Log/audit nghiệp vụ cần tra cứu trên dashboard"), §5.1/§5.3
(trace a flow by correlation id, task, order), §6 (audit of important actions). No endpoint reads `SystemEventLogs`
today, and doc §11 lists none — this task adds **`GET /api/system-events`** (doc deviation 18). Split from 018 by the
user on 2026-10-03.

**Decisions (user, 2026-10-03):** the page and the endpoint are for **Manager and Admin**; events `user.*` are
**Admin only**, filtered on the server. Payloads are returned through a **per-event-type allowlist** of fields; the
outbox's `lastError` is never returned. Every decision is in the implementation log, "Task 018 plan" and "Task 018
result" — read them first.

Read first: implementation log "Steps 1–3" (`SystemEventLog`, `LogId` identity), "Steps 4–5" (paging contract),
"Task 008 plan" (SystemEventLog writes), "Task 009 plan" (user audit), "Task 016 plan" (processing page, range filter),
"Task 018 plan/result"; reviews 017 and 018; backend `SystemEventLog.cs`, `SystemEventLogConfiguration.cs`, every
`SystemEventLogs.Add` (10 places: `IdentityUserService`, `AlertService`, `StockSyncService`, `InventoryImportService`,
`OrderReservationService` ×3, `ProcessingAttemptStore`, `ProcessingTaskService`, `OutboxPublisherService`) and the
event-type constants next to them, `ProcessingTaskListQueryValidator` / `PageQueryValidator`, `AuthorizationPolicies`,
`ProcessingTaskService` (list query, `createdByEmail`-style lookups); frontend `features/processing/ProcessingPage.tsx`
(filters, local-day range, address state), `features/alerts/*` (multi-value filters, `someOf`),
`shared/hooks/useListQueryState.ts`, `app/navigation.tsx`, `src/test/server.ts`.

If the implementation needs a decision that is not settled here or in the log (a write endpoint, a new event type, a
change of what an existing event writes, realtime hints for events, export of logs, retention), stop and report
(`Blocked`).

## Part A — Follow-ups (separate commits, A1 first)

### A1. Finish the verification of 018 (P2, review 018)
The 018 report has placeholders where the five consecutive full `npm test` runs belong (`@@FIVE_RUNS@@`,
`@@FIVE_RUNS_DETAIL@@`), and the report and the README changes of 018 are not committed.
- On the state of `ebfb38d` + the uncommitted README (nothing of 018b yet): 5 consecutive full `npm test` runs with
  PostgreSQL and RabbitMQ running and nothing else; fill both placeholders with wall time and result of each run (and
  the cause of any red run). Commit the README and the completed report as the first commit of this task
  ("task 018: report").
- If a run is red, do not fix it in that commit: name the test and the cause in the 018 report, and fix it in A2 or as
  its own commit of this task (deviation with the reason).

### A2. No test may end with a request in flight (P3, review 018 question 4)
Handoff gotcha "a frontend test must end with its requests settled" has caused three red series. Make it a rule the
suite enforces instead of a manual pass:
- In the test setup (`src/test/*`), track requests that MSW has received but not yet answered (its life-cycle events —
  use the API of the installed MSW version; do not guess it) and **fail the test in `afterEach`** when any is still
  pending, naming method and URL. Requests the test deliberately never answers (if any helper does that) are allowed
  only through one explicit, named opt-out, used where needed and listed in the report.
- Run the full suite, fix every test the guard flags so it ends on its settled state (as `dd5efb1` did), and list them
  (file, test, what it now waits for). No `testTimeout` change.
- Check that the guard bites: a temporary test that ends while a delayed response is pending must fail with the
  message (not committed; say so).

## Part B — Backend: `GET /api/system-events` (ManagerOrAdmin)

### Query
Paging contract of the project (`page`, `pageSize`; sort `createdAt` only, default descending, `sortDir=asc` allowed;
tie-break `LogId`). Filters, all optional:
- `level` (repeatable; `SystemEventLevel`), `eventType` (repeatable; exact match), `correlationId` (exact),
  `orderId`, `taskId`, `from` / `to` on `CreatedAt` (inclusive, ISO 8601 with offset, ≤ 92 days when both are given;
  same validation and localised messages as the processing list).
- No search text, no payload filter.
- **Role filter:** when the caller is not Admin, events whose type starts with `user.` are excluded from the result
  (also when the caller asks for them by `eventType`: an empty page, not an error). The controller decides from the
  caller's role and passes it to the service as a parameter; `ICurrentUser` is not extended for it.

### Response item
`logId`, `level`, `eventType`, `createdAt`, `correlationId`, `taskId`, `orderId`, `workerName`, `details` (object or
null), `actorEmail`, `targetEmail` (null when not applicable or unknown).
- **`details`** = the allowlisted top-level fields of the payload for that event type, values copied as they are
  (arrays/objects included); fields not in the list are dropped; an event type not in the map → `details: null`.
  The map lives in one place in Application (one entry per event type constant, referencing the existing constants,
  not string copies). Initial map (verify every field against the code that writes it; a field in the code but not
  here is **not** added without a reason in the report):

  | Event type | Allowed fields |
  | --- | --- |
  | `inventory.imported` | `referenceId`, `itemCount`, `totalQuantity` |
  | `order.insufficient_stock` | `orderId`, `taskId`, `items` |
  | `order.already_exists` | `taskId`, `orderId`, `externalOrderId` |
  | `order.message_rejected` | `messageId`, `reason`* |
  | `stock_sync.message_rejected` | `messageId`, `reason`*, `queue` |
  | `processing.failed` | `taskId`, `taskType`, `attemptNo`, `errorCode`, `errorMessage` (sanitised, doc §5.1), `queue` |
  | `processing.retry_requested` | `taskId`, `taskType`, `requestedBy`, `previousMessageId`, `newMessageId`, `attemptBudgetStart` |
  | `outbox.publish_failed` | `outboxEventId`, `eventType`, `publishAttempts` — **never `lastError`** |
  | `alert.acknowledged` | `alertId`, `type`, `acknowledgedBy` |
  | `user.created` | `targetUserId`, `actorUserId`, `role` |
  | `user.role_changed` | `targetUserId`, `actorUserId`, `from`, `to` |
  | `user.locked`, `user.unlocked`, `user.password_reset` | `targetUserId`, `actorUserId` |

  \* `reason` only if every value the code writes is the application's own text (no exception message, no fragment of
  the rejected message body). Check the writers; if any path can carry such text, leave `reason` out and say why.
- **`actorEmail`** from `actorUserId` / `requestedBy` / `acknowledgedBy`, **`targetEmail`** from `targetUserId`:
  resolved with **one** user lookup per page (collect the ids of the page, one `WHERE Id IN (…)`), not per row and not
  by a text join per row. Values that are not user ids (e.g. `system:…`) → null.
- Statements per request: one count, one page, at most one user lookup (test with a command counter, as 017 did).
- No `GET /api/system-events/{id}`.

### Indexes
Existing: `CreatedAt`, `CorrelationId`, `OrderId`, `TaskId`, `(EventType, CreatedAt)`. No new index unless a plan
of the default query (no filter, page 1) on a seeded table of ≥ 50,000 rows shows a sequential scan for the page query;
report the `EXPLAIN` of the default query and of `eventType` + range. The count over a wide range is accepted (026).

### Tests (integration)
Default order and paging; every filter (repeatable `level` / `eventType`, inclusive range, 92-day rule in en / vi,
inverted range); Manager does not see `user.*` (list and explicit `eventType=user.created` → empty), Admin does;
Staff → 403, anonymous → 401; `details` per event type of the map (one seeded row each, with an extra field that must be
dropped); `outbox.publish_failed` never returns `lastError`; unknown event type → `details` null; `actorEmail` /
`targetEmail` resolved, `system:` actor → null; statement count. Unit: the allowlist map covers every event-type
constant written today (a test that fails when a new constant has no entry — reflect over the constant classes or list
them explicitly with a comment).

## Part C — Logs/Audit page (`/logs`, Manager/Admin)
- Menu item `/logs`: `roles: ['Admin', 'Manager']`, `plannedTask` removed. Staff neither sees the item nor can open the
  route (existing `RequireRole` mechanism).
- `systemEventsApi.listSystemEvents`, tag type `SystemEvent` (LIST). **No realtime** (no hint exists for events): a
  "Refresh" button refetches; `refetchOnMountOrArgChange` as on the other lists.
- Filters in the address (`useListQueryState`): level (multi), event type (multi; options = the known types, labels
  translated; `user.*` options only for Admin — the server enforces it anyway), correlation id, order id, task id
  (exact text; invalid GUID → field message without a request), range in local days with presets (last 24 h default,
  today, 7 days, 30 days — same component as the dashboard/processing pages; the default is written to the address so a
  reload keeps the same window). "Clear filters".
- Columns (fit 1280 px in vi and en, column-width test): time, level (tag, icon + text), event type (translated label;
  unknown types shown as the raw string), summary (one line per type built from `details` with translated templates,
  e.g. import: reference, items, units; failure: task type, attempt, error code — the `errorMessage` in a tooltip or an
  expandable row), who (actor e-mail; for `user.*` "actor → target"), links. Empty values "—".
- Links: order → `orderPath`, task → `processingTaskPath`, correlation id → the same page filtered by it (the trace
  view of doc §5.3: one click shows every event of a flow), alert → `alertsListPath` filtered by product is not
  possible from the payload — link nothing for alerts.
- Language switch: text only.

## Tests (required)
Frontend (mocked API; the A2 guard active for every test):
1. Part A2 as listed there.
2. Logs page: Staff has no menu item and the route shows the not-allowed page; Manager sees no `user.*` type options,
   Admin does; default range written to the address; each filter, sort direction and page round-trip through the
   address and reset the page; invalid GUID → message, no request; "Clear filters"; "Refresh" refetches.
3. Rows: one fixture per event type of the map → its summary text (vi and en), links, "—" for empty values; unknown
   type → raw type, no summary; `details: null` handled.
4. Correlation-id link filters the page by it.
5. Column widths at 1280 px; language switch keeps everything but the text.
Backend: Part B tests; existing tests green without modification.

## Acceptance criteria
- [ ] A1: the 018 report complete (five runs with wall times) and committed with the 018 README changes, first commit.
- [ ] A2: the guard in place, the list of fixed tests, and the "guard bites" check described.
- [ ] Backend build 0 warnings, all tests pass; frontend typecheck, lint, test, build pass; **5 consecutive full
      `npm test` runs green** (wall times) with the guard active.
- [ ] Bundle: first-load set before/after; the logs page is a lazy chunk.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; payload filtering and the `user.*` rule are the server's.
- [ ] `scripts/smoke/task-018b.ps1` (API **and** Workers in Development; strict `Assert-Step`; A5 table format): smoke
      Admin-created Manager and Staff; Staff → 403; Manager imports stock for a smoke product → `inventory.imported`
      visible to Manager with `details.referenceId`; a `SIM-PERM-` mapping → `processing.failed` with `errorCode`
      and the task id, found again by `correlationId`; Manager acknowledges an alert → `alert.acknowledged` with
      `actorEmail` = the Manager; locking a smoke user (Admin) → `user.locked` visible to Admin with `targetEmail`, **not**
      visible to Manager (also with `eventType=user.locked`); no item of the response has a `lastError` anywhere; smoke
      mapping ends Inactive; lock the smoke users; `Invoke-FrontendChecks`. Run it on the **development** environment if
      it is up (otherwise a throwaway one, and say so) and paste the table.
- [ ] Real-browser check on the production build: page at 1280 px in vi and en; Manager traces one smoke order from
      its import to its processing events by clicking the correlation id; Admin sees the user events of the smoke run;
      Staff has no menu item.
- [ ] README: section "System events (Logs/Audit)" (endpoint, roles, allowlist, how to add an event type), `.http`
      sample.
- [ ] Doc deviation 18 confirmed or corrected in the report.

## Out of scope
Writing or deleting events, new event types or changes to what existing writers store, realtime hints for events,
export of logs (021), retention/purge (026), full-text or payload search, Serilog technical logs (they are not in the
database), a detail route.

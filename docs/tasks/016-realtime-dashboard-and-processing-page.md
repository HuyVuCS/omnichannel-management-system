# Task 016 — Frontend: realtime client, dashboard, Sync & Processing page

Status: Open
Written by: Cowork, 2026-10-02
Depends on: 015 (accepted)

## Context
Doc §3.2 (Dashboard, Sync & Processing), §12 (SignalR → React → RTK Query invalidate/refetch), §16 "Dashboard nhận cập
nhật gần real-time" and "trace an order to task, worker, instance, attempt, error". Task 015 delivered the hub
`/api/hubs/notifications` (message `hints` `{ events, resync }`) and `GET /api/dashboard/summary` — README section
"Realtime hints and dashboard summary" is the contract.

**Roadmap change (user, 2026-10-02):** the planned 016 is split. This task: realtime client, dashboard, Sync &
Processing page, order-entry outcome by push, Part A. The channel product mapping UI is **016b** (not here).

Every design decision is in the implementation log, "Task 016 plan" — read it first, with "Frontend decisions",
"Task 012 plan/result", "Task 013 plan/result", "Task 015 result", and reviews 013, 014, 015. If the implementation
needs a decision that is not settled there (another realtime library, a chart library, a global state change, a new
backend endpoint beyond `productSku`), stop and report (`Blocked`).

Read first: `shared/api/baseApi.ts`, `app/store.ts`, `app/tabVisibility.ts`, `features/auth/SessionManager.tsx`,
`features/orders/components/OrderOutcome.tsx`, `features/processing/*`, `shared/hooks/useListQueryState.ts`,
`shared/utils/dateRange.ts`, `app/navigation.tsx`, `app/router.tsx`, `AppLayout.tsx`, `shared/i18n/index.ts`; backend
`ProcessingTaskService` (list/detail queries), `ProcessingContracts.cs`.

## Part A — Follow-ups (separate commits)
1. **(review 015) Smoke scripts never start hosts for a foreign environment.** `_common.ps1` starts an API or a Workers
   host only when `SMOKE_BASE_URL` is not set (the local default). With `SMOKE_BASE_URL` set and the API unreachable, the
   script fails at once with a message naming the URL. Existing scripts unchanged otherwise. Check both branches by
   running a script (any of 013–015) once with `SMOKE_BASE_URL` pointing at an unused port — paste the output.
2. **(review 013) Pickers hide only stale answers.** `useServerSearchSelect` (or wherever the rule lives now) offers no
   option while the debounce is pending or while `currentData` is `undefined` (a *different* argument is loading); a
   background refetch of the *same* search keeps its options. Test: an invalidation of `Product` while the dropdown is
   open keeps the options visible; typing a new search still offers nothing until its answer arrives (the 013 test
   stays).
3. **(review 013) Panel title repetition.** The order-entry panel shows the component title and the channel name only
   when they differ ("Website Website" → "Website"). Text only, no layout change.
4. **(review 013) Orders table at 1280 px.** Column order and widths so that the status column is visible without
   horizontal scrolling at a 1280 px window (content width ≈ 992 px); long values truncate with tooltip (existing
   `TruncatedText`). No layout redesign. Measure in the browser and report widths in vi and en.
5. **(review 013) Load only the active language.** The active language's JSON is loaded before the application renders
   (no flash of keys); the other one is loaded on the first switch, **before** `changeLanguage` (switch stays "text
   only": no remount, no layout shift, state kept); a failed load keeps the current language and shows a localised
   error notification. Both files leave the first-load set. Tests: start in en and in vi; switch loads once; failed load.
6. **(review 014) Bundle measurement script.** Commit the first-load-set measurement (entry + static imports, raw and
   gzip, per file and total; main chunk listed) as a Node script without new dependencies, e.g.
   `frontend/scripts/measure-bundle.mjs`, runnable after `npm run build` (`npm run measure`). Use it in this report and
   state the numbers before/after Part A5 and after Part B.

## Part B — Backend: product SKU on tasks (one small commit)
- `ProcessingTaskSummaryResponse` and `ProcessingTaskDetailResponse` get `productSku` (string?, null when the task has no
  product), from a left join in the **same** query (no second round trip, no N+1). Nothing else changes in the contract.
- Integration tests: list and detail of a `StockSync` task carry the SKU; an `OrderProcessing` task has `null`. Existing
  tests green without modification.

## Part C — Realtime client
- **Dependency:** `@microsoft/signalr` (latest stable at the time of the task; say which). Loaded with a dynamic
  `import()` only after sign-in; the login page and the first-load set do not contain it (report its chunk size).
  Client logging at `Warning` (the client must never print a URL — it carries the token).
- **Dev proxy:** `vite.config.ts` forwards WebSockets for `/api` (`ws: true`); nothing else changes in the proxy.
- **Lifecycle** (`features/realtime/`, one module owns the connection; mounted once next to `SessionManager`):
  - started when a session exists, stopped on logout / expiry / session change (a new login = a new connection); never
    two connections at once (React StrictMode double effects included);
  - `accessTokenFactory` reads the current token from the store at every (re)connect;
  - automatic reconnect with delays 0, 2, 5, 10, 30 s, then every 30 s while signed in — never gives up on its own; a
    failed initial start is retried with the same delays;
  - a **401** on negotiate (revoked token, changed security stamp) ends the session like a 401 from the API (same rule
    as `apiBaseQuery`: only if the token used is still the current one);
  - connection state in a small Redux slice (`connecting` / `connected` / `reconnecting` / `disconnected`) — UI state,
    not server state; reset on session end.
- **Hints → cache** (one module, pure functions where possible, unit-tested):
  - map (implementation log, "Task 016 plan"): `order.created` → `Order` LIST + `DashboardSummary`;
    `inventory.changed` → `Inventory`, `InventoryMovement`, `Product` LIST + `DashboardSummary`;
    `processing.task.changed` → `ProcessingTask` id + LIST, `Order` id when `orderId` is set, `DashboardSummary`;
    `all: true` → the same tags without ids (every entry of the type); unknown event types are ignored;
  - **coalescing:** tags are collected and flushed with `baseApi.util.invalidateTags` at most once per second (trailing);
  - **resync** (`resync: true`, and every transition to `connected`, the first included) → flush the realtime tags
    (`Order`, `Inventory`, `InventoryMovement`, `Product`, `ProcessingTask`, `DashboardSummary`) once; never
    `resetApiState`;
  - a cached query that is not on screen must not be shown stale later: verify what RTK Query does with invalidated
    entries without subscribers and state it; lists keep `refetchOnMountOrArgChange` (decision in the log).
- **Indicator:** header (`AppLayout`) shows *live* / *reconnecting* / *offline* with icon + text + tooltip ("data may be
  outdated" when not live), localised, fixed width (no layout shift on language switch or state change).
- **Order entry:** `OrderOutcome` stops polling while the connection is `connected` (the task's tag invalidation refetches
  it); while not connected the 013 polling applies unchanged (1.5 s, 60 s window, "Check again", paused when hidden).
  The 60 s "still processing" state and "Check again" (one refetch, restarts the window) apply in push mode too — a
  timer, no requests. The once-per-terminal-state invalidation of 013 stays. Switching from push to polling (connection lost while waiting)
  and back must not lose the outcome.

## Part D — Dashboard (`/`, every role)
- `DashboardSummary` tag on a `getDashboardSummary` query; page replaces the placeholder.
- **Range:** presets *last 24 hours* (default, no parameters sent: the server's default), *today*, *last 7 days*,
  *last 30 days* (local day boundaries via `dateRange.ts`, sent with offset), and a custom range (date picker, inclusive
  local days). Kept in the address (preset name or the two dates). A 400 from the server is shown on the range control
  (the 92-day rule is the server's).
- **Content** (AntD `Statistic`/`Card`/`Table`, no chart library):
  - orders in the range: total, sales amount (whole VND), by status, by channel (total, reserved, insufficient stock);
  - inventory now: products, low stock (link to the inventory list with its low-stock filter), out of stock;
  - processing per task type: pending, in progress, retry scheduled (now), failed, succeeded (in the range); each
    non-zero number links to `/processing` with the matching filters;
  - stock sync per channel: retry scheduled, failed (link: `/processing?taskType=StockSync&status=Failed&channelId=…`),
    last success (relative time + exact time in a tooltip; "never" when null);
  - outbox: pending, failed, oldest pending (age); no threshold judged by the client;
  - `generatedAt` ("updated hh:mm:ss") and a manual refresh button.
- Realtime: refetches through the `DashboardSummary` tag (coalesced). No polling.

## Part E — Sync & Processing (`/processing`, every role; retry Manager/Admin)
- **List** `GET /api/processing/tasks`: filters task type, status, channel (select from the channel list), created range
  (local days), correlation id, error code; sort `createdAt` / `updatedAt`; paging — all through `useListQueryState`
  (address, reload keeps them). Columns: created, type, status (`TaskStatusTag`), channel name (from the channel list;
  the GUID if unknown), product SKU, order (link to the order detail), attempts, last attempt (error code, worker
  instance; error message truncated with tooltip — sanitised by the API), updated. Fits 1280 px without hiding the status.
- **Detail** route `/processing/tasks/:taskId` (outside the menu, menu highlights *Sync & Processing*): task fields,
  attempts (`AttemptsTable`), message status (`lastError` is never rendered), product SKU, channel name, link to the
  order (the movement history has no correlation-id filter; none is added). Non-GUID id → not found without a request;
  404 → not found. "Back" returns to the list address.
- **Retry** (Manager/Admin; Staff sees no button): on a `Failed` task, with a confirmation; 202 → task shows `Pending`
  (refetch), 409 `task_not_failed` / 404 shown localised. The same retry action on the order detail's task list.
- Realtime: list and detail refetch through `ProcessingTask` tags (coalesced).
- Remove `plannedTask: '016'` from the menu item.

## Tests (required)
Frontend (mocked API and a **fake hub adapter**: wrap `@microsoft/signalr` behind a small interface so tests never
mock the library's internals; fake timers only where needed — say where):
1. Hint mapping: each event type → tags; `all: true`; unknown type ignored; resync tag set.
2. Coalescing: several messages within 1 s → one `invalidateTags` with the union; next flush after the window.
3. Lifecycle: start on sign-in, stop on logout and on expiry, no connection without a session, one connection under
   StrictMode, a new login gets a new connection with the new token; reconnect delays; resync on (re)connect; 401 on
   negotiate ends the session only for the current token.
4. Indicator: each state's text; language switch keeps the state and width (`expectLanguageSwitchChangesTextOnly`).
5. Order outcome: push while connected (no polling request; outcome follows the task refetch); polling when not
   connected; connected → disconnected → connected while waiting keeps the outcome; 013 tests unchanged or listed as a
   deviation with the reason.
6. Dashboard: numbers and zero rows from a fixture; presets and custom range round-trip through the address and send the
   right parameters (none for the default); 400 on the range control; links carry the filters; a hint refetches once.
7. Processing list: every filter and the sort round-trip through the address and reset the page; channel name / GUID
   fallback; SKU column; detail link carries the list address.
8. Processing detail and retry: fields, attempts, `lastError` never rendered; non-GUID and 404 → not found; retry visible
   only to Manager/Admin and only on `Failed`; confirmation; 202, 409, 404 handling; retry on the order detail.
9. Language switch on dashboard, processing list and detail keeps route, filters, table state (text only).
10. Part A 2–5 as listed there; Part B backend tests.

## Acceptance criteria
- [ ] Frontend typecheck, lint, test, build pass; backend build 0 warnings, all tests pass (Part B is the only backend
      change).
- [ ] Bundle: first-load set before/after (Part A6 script), signalr chunk size, growth above 10 kB raw explained.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no client-side business rules (no computed totals, no
      thresholds, no range limit).
- [ ] `scripts/smoke/task-016.ps1` (API **and** Workers; Part A1 rules): smoke Manager + Staff; smoke product, import 5;
      map it on `website` (initial sync task) → task `Succeeded` → list `?taskType=StockSync&channelId=<website>` contains
      it with `productSku` = the smoke SKU, detail too; an order on `website` → its `OrderProcessing` task has
      `productSku` null; Staff `GET /api/dashboard/summary` 200; Staff retry of a task → 403; lock both smoke users;
      `npm run build`; `npm test`. Run it and paste the table. **Deactivate the smoke mapping at the end** (never touch
      the real channels).
- [ ] Real-browser check (as in 013; Workers running; smoke data only; Staff account for the pages, Manager only for
      retry; Admin only via `ui-check-61563` if needed, locked again afterwards):
      - two tabs: dashboard + order entry → an order updates the dashboard without reload within ~2 s;
      - the hub uses **WebSockets through the Vite proxy** (network panel: 101 on `/api/hubs/notifications`), the token
        appears in no console line;
      - stop the API → indicator *reconnecting*; start it → *live* and one refetch of the visible queries;
      - logout → the hub connection is closed (no further hub requests);
      - a failed stock-sync task: map a smoke product with external SKU `SIM-PERM-…` on `website` (Development simulator,
        README section 7) → task `Failed` appears in the list without reload; filter by channel; detail with attempts;
        retry as Manager → `Pending` → `Failed` again (the rule is permanent) — then deactivate that mapping;
      - order entry outcome with Workers running (push, no polling requests in the network panel) and with Workers
        stopped for > 60 s ("still processing", then start the Workers and "Check again");
      - VI/EN measurements: dashboard, processing list at 1280 px, orders list at 1280 px (Part A4), indicator.
- [ ] Manual check for the user documented (short: two tabs, an order, the dashboard moves).

## Out of scope
Channel product mapping UI and "never synced" (016b), alerts and `low_stock.created` (017/018), logs/audit page (018),
charts and exports (021), per-role hints, refresh tokens, cancelling/editing orders, a SignalR backplane, notification
toasts for failures, any change to the hub or the hint contract.

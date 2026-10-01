# Task 013 — Frontend: Order entry, Orders list and detail (+ order links in the movement history)

Status: Open
Written by: Cowork, 2026-10-01
Depends on: 012 (accepted; user's manual check pending)

## Context
Fourth frontend task and the first end-to-end flow a user can see: an order typed into one of the three channel panels
(doc §3.1) goes `POST /api/orders/{slug}` → outbox → RabbitMQ → Order Worker (reservation) → order visible in the list and
detail, with its processing task and attempts (doc §3.2 "Nhập đơn", "Đơn hàng"; doc §16 "Tạo được order từ 3 component",
"tra một order và biết task, worker, instance, attempt, lỗi").
Backend endpoints exist since tasks 004 and 008: `POST /api/orders/{slug}` (202 + `taskId`), `GET /api/orders`,
`GET /api/orders/{id}`, `GET /api/processing/tasks/{id}`, `GET /api/channels`, `GET /api/customers[/{id}]`.
**No backend change is planned.** If one turns out to be needed (other than a bug fix with a test), stop and report
(`Blocked`).

Reuse the 010–012 patterns exactly: RTK Query endpoints per feature, list state in the address (`useListQueryState`,
`useAddressText`), `applyProblemToForm` / `FormErrors`, translated `errorCode`, layout constants (fixed widths for anything
whose text differs per language), no hard-coded text, language switch changes text only, no client-side business rules,
actions a role may not use are hidden (all three pages are AnyStaff, so nothing is hidden here).

Read first: review 012 (`docs/tasks/012-frontend-inventory-pages.review.md`), implementation log "Task 012 result" and
"Task 013 plan", `StockImportForm.tsx` (line form, list-level errors, result view), `MovementsPage.tsx` (address filters,
date range, inline alert), `ProductSelect.tsx`.

## Part A — Follow-ups from review 012 (separate commits)
1. **Full labels in the pickers.** A `SKU — name` option cut by the dropdown width, and the selected value cut by the box,
   show the full text on hover (option and selected value). Same for `CustomerSelect` (Part B). Test: for a long label the
   full text is available on the option and on the selected value (jsdom cannot measure ellipsis; test what the user can
   reach, e.g. the tooltip/title text).
2. **Deterministic session-expiry test.** `auth.test.tsx` › "logs out when the access token expires…" must not depend on a
   real 400 ms session. Use fake timers or a controlled clock (mind AntD animations; see `renderApp.tsx`). Run that file
   10 times in a row and report the result.
3. **Main chunk growth.** Find out what moved into the main chunk between 011 (153 kB) and 012 (223 kB). No new
   dependency in `package.json` (a one-off `npx` tool or Rollup/Vite output is fine). If page-only code leaked into the
   shell, move it out. Report before/after. From now on the report states the main chunk size and explains any growth
   above 10 kB.

## Part B — Pages

### Shared pieces
- `features/orders/ordersApi.ts`: `submitOrder` (`POST orders/{slug}`), `listOrders`, `getOrder`; tag type `Order`.
  Types mirror the API (`OrderSummaryResponse`, `OrderDetailResponse`, `OrderItemResponse`, `OrderTaskResponse`,
  `SubmitOrderResponse`); enums `ORDER_STATUSES` = `Pending`, `Reserved`, `InsufficientStock`, `Cancelled`, `Completed`.
  Add the error texts `order_conflict`, `order_not_found`, `task_not_found` (vi/en; `channel_not_found` exists).
- `features/processing/processingApi.ts` with **only** `getProcessingTask` (`GET processing/tasks/{id}`, tag
  `ProcessingTask` by id) and the task/attempt/message types; task 016 extends this file. Enums: task types
  `OrderProcessing`, `StockSync`, `Invoice`, `Email`; task statuses `Pending`, `InProgress`, `RetryScheduled`,
  `Succeeded`, `Failed`; attempt statuses `Running`, `Succeeded`, `Failed`; message statuses `Pending`, `Published`, `Failed`.
- `features/processing/components/TaskStatusTag.tsx` and `AttemptsTable.tsx` (reused by 016). One status colour per value
  (task: Pending default, InProgress processing, RetryScheduled warning, Succeeded success, Failed error; attempt: Running
  processing, Succeeded success, Failed error).
- `features/orders/components/OrderStatusTag.tsx`: Pending default, Reserved processing, InsufficientStock warning,
  Cancelled error, Completed success.
- `features/customers/components/CustomerSelect.tsx`: behaves like `ProductSelect` (nothing loaded until opened, debounced
  server search `GET /api/customers?search=&pageSize=20`, label of a value set from outside via `getCustomer` =
  `GET /api/customers/{id}`, added to `customersApi` with tag `{ type: 'Customer', id }`). Option text: name, with phone or
  e-mail as secondary text when present. If `CustomerSelect` and `ProductSelect` would duplicate more than the query hooks
  and the option rendering, extract the shared behaviour into one hook in `shared/hooks`; no generic select framework.
- `shared/utils/dateRange.ts`: move the local-day handling out of `MovementsPage` (parse `YYYY-MM-DD` from the address,
  ignore invalid/impossible dates; local start of `from` and local end of `to` as ISO 8601 with offset). Movements and
  orders use it; the movement tests stay green unchanged.
- Routes: `/order-entry` and `/orders` are menu items already (placeholders today). `/orders/:orderId` is a page outside
  the menu (`pagesOutsideMenu`; the menu highlights "Đơn hàng"). Paths in `features/orders/paths.ts` (`orderPath(id)`).

### Order entry — `/order-entry` (every role)
- **Three panels on one page** (AntD `Card`s, stacked), one per seeded channel slug, in this order: `digital-platform`
  ("Digital Platform Online"), `website` ("Website"), `pos` ("POS"). Component names are translated texts; each panel also
  shows the channel's name from `GET /api/channels`. A channel that is `Inactive` gets a warning tag, one that is missing
  from the list a "not configured" warning; **the form stays usable** — the API decides and its 404 `channel_not_found` is
  shown as an alert in that panel. Each panel is one `OrderEntryPanel` with its own form, request and outcome; using one
  panel never changes the input of another.
- **Form** (per panel): external order id (required), customer (`CustomerSelect`, required), lines (`Form.List` `items`):
  product (`ProductSelect`, products on other lines of the same panel disabled) + quantity (whole number, ≥ 1; every other
  limit is the API's). Add line / remove line (the last line cannot be removed). Submit "Create order", loading while the
  request runs. No confirmation dialog (idempotency protects a double submit). No price or total in the form.
- **External order id:** generated when the panel opens: prefix `DP-` / `WEB-` / `POS-`, local date `YYYYMMDD`, `-`, six
  random characters `[A-Z0-9]` from `crypto.getRandomValues` (e.g. `WEB-20261001-7KQ2ZD`). Editable (to try duplicates and
  conflicts); an icon button "new number" next to it. **Kept after a failed request** (a resend is the same order) and
  replaced only by "New order" after a 202 or by the button. A language switch never changes it.
- **Errors:** field errors on `externalOrderId`, `customerId`, `items[n].productId`, `items[n].stock`; list-level `items`
  errors under the list; 409 `order_conflict` on the external order id field with a translated text saying this number was
  already used on this channel with different content and a new number is needed; 404 `channel_not_found` and anything
  else form-level in the panel's alert.
- **Outcome (after 202)** replaces the panel's form:
  - external order id, channel, task status (`TaskStatusTag`); `duplicate: true` → an info line that the order had already
    been accepted and no new processing was started (the existing task is shown).
  - **Polling:** `getProcessingTask(taskId)` every 1.5 s while the task is not terminal (`Succeeded` / `Failed`), at most
    60 s from the submit, paused while the document is hidden. Use RTK Query `pollingInterval` +
    `skipPollingIfUnfocused`; that needs `setupListeners(store.dispatch)`, which the store does not call yet — add it and
    verify it changes nothing else (`refetchOnFocus` / `refetchOnReconnect` are not set anywhere). Polling stops when the
    page is left; a language switch does not restart it.
  - While not terminal: `Pending` with the message not yet published → "waiting to be handed to the queue"; message
    `Failed` → "could not be sent" (error); `RetryScheduled` → "retrying" with the last attempt's `errorCode`. Never show the
    message's `lastError` (not sanitised for the UI); attempt `errorMessage` is sanitised by the API and may be shown.
  - `Succeeded` with `orderId` → `getOrder(orderId)` once: `Reserved` → success ("stock reserved"), `InsufficientStock` →
    warning ("not enough stock; the order was recorded without a reservation"); total amount from the API; link
    "Open order" → `/orders/{orderId}`.
  - `Failed` → error with the last attempt's error code and message and the task's correlation id (copyable).
  - 60 s without a terminal state → info "still processing" + "Check again" (one refetch, restarts the 60 s window).
  - When the task first reaches a terminal state, invalidate `Order`, `Inventory`, `InventoryMovement`, `Product` once
    (`baseApi.util.invalidateTags`), not on every poll.
  - "New order" → empty form with a new external order id.
- Leaving the page discards the panels' state (no persistence, no unsaved-changes guard in this task).

### Orders — `/orders` (every role)
- Table: created at (`DD/MM/YYYY HH:mm`), external order id (link to the detail), channel (name), customer (name), status
  (`OrderStatusTag`), items (`itemCount`, right-aligned), total (`formatMoney`, right-aligned), action "Detail". Server
  paging; sort `createdAt`, `externalOrderId`, `totalAmount` (whitelist of the API). The API's default order is newest
  first: with nothing in the address the `createdAt` column shows a descending sort indicator, and the indicator always
  matches the order the API actually used.
- Debounced search (external order id or customer name, as the API defines it).
- Filters in the address, each resetting to page 1: channel (`channel=<slug>`, options from `GET /api/channels`, label =
  name), status (`status`), customer (`CustomerSelect`, `customerId`), date range (`from` / `to` local days, via
  `dateRange.ts`). Invalid values are ignored and never sent (unknown status, non-GUID customer id, bad dates); `channel` is
  sent as given (the API matches an unknown slug to nothing). "Clear filters", "Refresh".
- A 4xx answer shows the inline alert with the server's messages (as on the movement history).
- `refetchOnMountOrArgChange: true` (orders are created by the Order Worker in the background).
- Links to the detail carry the list's current address (router `state`), so "Back" on the detail returns to the same
  page, sort and filters.

### Order detail — `/orders/:orderId` (every role)
- A non-GUID id shows the not-found view without a request; 404 `order_not_found` shows a not-found view with a link to
  the list.
- Header: title with the external order id; actions "Back" (to the list address it came from, otherwise `/orders`),
  "Refresh", "Movements of this order" → `/inventory/movements?referenceType=Order&referenceId=<orderId>`.
- Summary (`Descriptions`): external order id, status, channel (name and slug), customer (name), items, total amount,
  created at, order id (copyable).
- Lines: SKU, product name, quantity, unit price, line total — all from the API, no client arithmetic, no paging.
- Processing: one block per task: type (translated), status, retry count, correlation id (copyable), message id, created /
  updated; its attempts in `AttemptsTable`: no., status, worker name, worker instance, error code, error message
  (truncated with tooltip), started, finished. A task without attempts says so.
- No retry button (only `OrderProcessing` tasks are order-bound today, and they are `Succeeded` whenever an order exists;
  manual retry comes with the processing page in 016).
- `refetchOnMountOrArgChange: true`.

### Movement history
- A movement with reference type `Order` and a GUID `referenceId` links to `/orders/{referenceId}`; any other value stays
  plain text (as today).

## Tests (required)
Frontend (mocked API, as before; use fake timers only where needed and say where):
1. Order entry:
   - three panels in the given order with the channel names; inactive and missing channel warnings with a usable form;
   - happy path: `POST /api/orders/website` with the generated id (prefix, date, pattern), customer and lines; polling until
     `Succeeded`; one `GET /api/orders/{id}`; `Reserved` outcome with the link; tags invalidated exactly once;
   - `duplicate: true` notice; `InsufficientStock` outcome; `Failed` outcome with error code, message, correlation id;
     message `Pending` / `Failed` texts; `lastError` never rendered;
   - polling stops at a terminal state, stops after 60 s with "still processing", "Check again" restarts it; no polling
     request while the document is hidden; none after leaving the page;
   - errors: `customerId`, `items[0].productId`, `items[1].stock` on their fields; list-level `items`; 409 `order_conflict`
     on the external order id; 404 `channel_not_found` in the panel alert; the external order id is unchanged after each
     failure and the resend uses it; "New order" gives a new id;
   - panels are independent (submit in one, the others keep their input).
2. `CustomerSelect`: nothing loaded before opening; debounced search; pre-set value shows its label.
3. Orders list: every filter and the sort round-trip through the address (reload keeps them) and reset the page; invalid
   values are not sent; date range sends local start/end of day with offset; default sort indicator; 400 inline alert;
   detail link carries the list address.
4. Order detail: summary, lines, tasks and attempts from the response; non-GUID id → not-found without a request; 404 →
   not-found; "Movements of this order" link; "Back" returns to the list address (and to `/orders` without one).
5. Movement history: `Order` + GUID reference links to the detail; non-GUID stays text. Existing movement tests unchanged.
6. Language switch on all three pages keeps route, filters, table state, a panel's form with its lines, an outcome view
   while polling (no extra requests, polling not restarted) — `expectLanguageSwitchChangesTextOnly`.
7. Staff can open and use all three pages.
8. Part A 1 and 2 as listed there.

## Acceptance criteria
- [ ] Frontend typecheck, lint, test, build pass (report outputs, chunk sizes, main chunk explained per Part A3). Backend
      untouched (state it; if anything changed: build 0 warnings, all tests pass).
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no client-side business rules (no totals, no stock checks, no
      channel-status enforcement).
- [ ] `scripts/smoke/task-013.ps1` (needs API **and** Workers; if the first order does not reach a terminal state within
      60 s the script fails with a message that the Workers host is probably not running). With a smoke Manager: smoke
      customer, smoke product, import 5. With a smoke **Staff**: `POST /api/orders/website` qty 3 → 202
      `duplicate=false`; same body again → 202 `duplicate=true`, same `taskId`; same id with qty 2 → 409 `order_conflict`;
      unknown slug → 404 `channel_not_found`; task → `Succeeded` with `orderId`; order `Reserved`, total = price × 3 as
      computed by the server; movements `referenceType=Order&referenceId=<orderId>` → one `Reserve` of 3; second order on
      `pos` qty 3 → order `InsufficientStock`, task `Succeeded`; `GET /api/orders?search=<id>` → 1;
      `?channel=pos&status=InsufficientStock&customerId=<id>` contains the second order; lock both smoke users;
      `npm run build`; `npm test`. Run it and paste the table. Never deactivate the real channels.
- [ ] Real-browser check like 012, with Workers running and only smoke data (smoke customer and products, not `TEST8A` or
      other existing products): an order from each panel; the outcome moving from pending to reserved; a duplicate (new
      order, type the previous number) and a conflict (same number, other quantity); an `InsufficientStock` outcome; with
      the Workers stopped: "waiting/still processing" after 60 s, then start the Workers and "Check again"; orders list
      filters + reload; detail with task and attempts; detail → movements → back to the order through the order link;
      VI/EN measurements on the three pages (a panel with three lines, an outcome view, the list, the detail). Staff
      account for the order pages; Admin only via `ui-check-61563` if needed (lock again afterwards).
- [ ] Manual check for the user documented (short).

## Out of scope
Cancelling or editing orders, manual retry (016), processing page (016), realtime push (016), unsaved-changes guard and
keeping panel state across navigation, price/total preview in the form, quick-create of a customer from the order entry,
webhooks (023), exports (020/021), the EF Core error logging of expected 409s (014 Part A).

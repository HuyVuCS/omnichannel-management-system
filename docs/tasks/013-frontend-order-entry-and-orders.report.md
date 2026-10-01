# Report 013 — Frontend: Order entry, Orders list and detail (+ order links in the movement history)

Status: Done
Commits: `bb61ea5` (A2 deterministic expiry test), `4398d43` (A3 inventory API out of the main chunk), `44d501e` (A1 picker
labels + shared search hook), `abcb192` (pickers offer no stale option — found in the browser), `2fa3d5a` (Part B pages,
smoke script, README)

## Summary
- **Part B:** Order entry (three channel panels), Orders list, Order detail, order links in the movement history,
  `CustomerSelect`, the processing API slice with `TaskStatusTag` / `AttemptsTable`, `dateRange.ts`.
- **Part A:** A1 full labels (with a correction of the 012 finding, see below), A2 deterministic expiry test (10/10 runs),
  A3 main chunk explained (and the metric itself turns out to be unstable — see "Chunk sizes").
- **Checked in a real browser against the real API and Workers.** It found four defects the mocked tests could not see;
  all fixed and covered. The most important one: **a picker could put the wrong product on a line** (Enter before the
  server search answered chose the first option of the old list). That defect has been in `ProductSelect` since 012.
- Backend **untouched** (no file under `backend/` changed; `dotnet build` / `dotnet test` were not run).
- Frontend: `typecheck`, `lint`, `test` (**167/167** in 17 files) and `build` pass. Main chunk 254.46 kB (explained below).
- `scripts/smoke/task-013.ps1`: all 31 checks passed (run twice; the "Workers not running" path was run once on purpose).
- 38 mutations of the new code, each caught by a test.

## Part A
### A1 — Full labels in the pickers (`44d501e`)
- **The 012 finding was wrong, and it was mine.** AntD 6 already puts the full label into the native `title` of every
  option and of the selected value. Native tooltips never show up in automated screenshots, which is why the 012 report
  said "without a tooltip". Verified now in the real DOM: a cut option and a cut selected value both carry the full text
  as `title` (`cut: true`, `title: "SMOKE-013-UI-C-99189 — Smoke UI product C … product picker"`).
- What changed: `ProductSelect` sets the option `title` explicitly instead of relying on the library's fallback, and
  `CustomerSelect` sets it to name **and** contact (`name — phone/e-mail`), because both parts can be cut.
- Tests: long label on the option, on the chosen value and on a value that came from the address — for both pickers
  (`stockImport.test.tsx` › "product picker", `orders.test.tsx` › "customer picker").

### A2 — Deterministic session-expiry test (`bb61ea5`)
`auth.test.tsx` › "logs out when the access token expires…" runs on a controlled clock
(`vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ['setTimeout', 'clearTimeout', 'Date'] })`): a 30-minute session,
a jump to one minute before the expiry (still signed in), a jump over the expiry (signed out). No real waiting.
- 10 runs in a row: 10 × `Tests 15 passed (15)`.
- Mutation (the expiry does not sign out): the test **failed**.

### A3 — Main chunk growth (`4398d43`)
Method: a one-off Vite config with a `generateBundle` plugin that lists the modules of every chunk (not committed, no
new dependency); the commits `29ab04e` (011), `5c63867`, `95d42dc`, `884aac8` (012) were built from `git archive` copies.

| Build | Main chunk | First-load set (entry + everything it imports statically) |
| --- | --- | --- |
| 011 (`29ab04e`) | 153.43 kB | 960.02 kB |
| 012 A3 / A4 (`5c63867`, `95d42dc`) | 155.54 / 155.61 kB | — |
| 012 (`884aac8`) | 222.59 kB | 970.76 kB |
| 013 after A3 (`4398d43`) | 221.85 kB | 969.97 kB |
| 013 final (`2fa3d5a`) | **254.46 kB** (gzip 81.98 kB) | **978.50 kB** |

- **012, +67 kB:** not new code. Up to 011 the shell's AntD code (`App`: modal, notification, message; menu, layout, spin)
  was a shared chunk `modal-*.js` (100 kB) that the main chunk imported statically. In the 012 pages commit the bundler
  dissolved that chunk: modal / notification / message / skeleton / dialog went into the main chunk, the rest into `spin`.
  The browser loaded the same code before and after. The only page code that had really leaked into the shell was
  `inventoryApi.ts` (1.2 kB, the route table imported `STOCK_IMPORT_ROLES` from it) and `paths.ts` (0.6 kB).
  **Fixed:** the role list lives in `features/inventory/paths.ts` now; `inventoryApi` is a 0.80 kB chunk of its own again.
- **013, +33 kB:** the same effect again. `antd/menu` and `antd/layout` (shell code) moved from the `spin` chunk
  (46.9 → 16.1 kB) into the main chunk. Application code that entered the first-load set: `app/tabVisibility.ts` (0.6 kB)
  and `features/orders/paths.ts` (0.4 kB).
- **What really grows per task is the translations:** `vi.json` + `en.json` are loaded up front, both languages:
  2 × 7.0 kB (011) → 2 × 9.9 kB (012) → 2 × 14.3 kB (013), before minification. That is the +7.7 kB of the first-load set.
- **Consequence for the rule "report the main chunk and explain growth above 10 kB":** the main chunk size depends on how
  the bundler groups shared vendor code and jumps by tens of kB without any code change. The first-load set is the stable
  number. Question for Cowork below.

## Part B — what exists
| Page | Notes |
| --- | --- |
| Order entry `/order-entry` | Three stacked panels (`digital-platform`, `website`, `pos`), each its own form, request and outcome. Channel name and status from `GET /api/channels`; inactive / missing channel = warning tag, form usable, the API's 404 shown in the panel. |
| Orders `/orders` | Server paging, sort `createdAt` / `externalOrderId` / `totalAmount`, debounced search, filters `channel`, `status`, `customerId`, `from` / `to`; inline alert for a 4xx; "Clear filters", "Refresh". |
| Order detail `/orders/:orderId` | Summary, lines, one block per task with `AttemptsTable`; "Back" (to the list address it came from), "Refresh", "Movements of this order". Not-found view for a non-GUID id (no request) and for 404. |
| Movement history | `Order` + GUID reference → link to the order; anything else stays text. |

- **External order id:** `DP-` / `WEB-` / `POS-` + local `YYYYMMDD` + six characters from `crypto.getRandomValues`
  (rejection sampling, no bias). Editable; "new number" button; kept after every failed request; replaced only by
  "New order" or the button.
- **Outcome:** `getProcessingTask` every 1.5 s while not `Succeeded` / `Failed`, at most 60 s, not while the tab is hidden;
  "Check again" = one request + another 60 s; then `getOrder` once. Messages for: not handed to the queue yet, could not be
  sent, queued, in progress, retrying (with the attempt's error code), failed (error code, sanitised message, copyable
  correlation id), reserved, insufficient stock. The message's `lastError` is never rendered.
- **Cache:** when a task is first seen finished, `Order` (list), `Inventory`, `InventoryMovement`, `Product` are invalidated
  once. Orders list and detail use `refetchOnMountOrArgChange`.
- **Errors:** field errors on `externalOrderId`, `customerId`, `items[n].productId`, `items[n].stock`; list-level `items`
  under the list; 409 `order_conflict` on the id field; 404 `channel_not_found` and everything else in the panel's alert.
- **Rules kept:** no price or total in the form, no stock check, no channel-status enforcement, no client arithmetic on
  the detail page (line total and order total are the API's); no hard-coded text; `vi.json` / `en.json` have identical keys.
- **`app/tabVisibility.ts`** keeps RTK Query's "focused" flag equal to the tab's visibility (see deviation 1).

## Real-browser check (dev server + real API + Workers, 1280×800)
Account: one smoke Staff (`smoke-013-ui-staff-99189`, random password, locked again). Data prepared by a smoke Manager
through the API (locked again). No Admin session was used in the browser; `ui-check-61563` was not touched. Only smoke
data: products `SMOKE-013-UI-A/B/C-99189`, customer `Smoke UI customer 013-99189`.

Functional, all as expected:
- Website, 3 × A: 202 → "waiting to be handed to the queue" → `Succeeded` → order loaded once → "Reserved", total
  `45.000 ₫` (2 task requests, 1 order request).
- Digital Platform, three lines (A, B, C): waiting → queued → reserved, total `158.456 ₫` (the server's sum).
- Duplicate ("New order", the previous number, same content): notice "already accepted", the existing task and order.
- Conflict (same number, quantity 2): the conflict text under the id field, id unchanged, no outcome.
- POS, 3 × C with 1 available: task `Succeeded`, order "Không đủ hàng", warning, total `370.368 ₫`.
- **Workers stopped:** "waiting to be handed to the queue"; after 60.04 s "still processing" + "Check again" (39 polls,
  then none). Workers started again → "Check again" → reserved.
- Orders list after a reload with `?search=…&sortBy=totalAmount&sortDir=desc&status=Reserved`: same rows, sort indicator on
  "Tổng tiền"; `?channel=pos&status=InsufficientStock&from=2026-10-02&to=2026-10-02` sent
  `from=2026-10-02T00:00:00+07:00&to=2026-10-02T23:59:59.999+07:00`.
- Detail from the list (router state = the list address), task with its attempt (`OrderWorker`, instance, times),
  "Movements of this order" → one `Reserve 3` → the order link in that row → back on the detail.

VI → EN → VI, measured with `getBoundingClientRect` on every card, input, select, button, summary label / value, alert,
table column and row:

| | Order entry (form with 3 lines + an outcome view + a form) | Orders list | Order detail |
| --- | --- | --- | --- |
| Sider / header / user block / language switch | 240 / 56 / 320 / 96×32 | same | same |
| Cards | 976.8 wide; 481.4 / 405.4 / 369.4 high | — | 140.2 / 326.5 high |
| Fields | id 360, customer 360, product 360, quantity 240, remove 32×32, add line 360×32, submit 176×32 | search 280, channel 180, status 180, customer 360, range 280, clear 136, refresh 136 | Back 136, Refresh 136, Movements 280 |
| Summary labels | 220 | — | 220 (order), 160 (task) |
| Columns | — | 170/240/200/240/160/110/170/88, row 64.8 | lines 180/300/120/170/170; attempts 80/130/160/220/170/280/160/160 |
| Differences VI vs EN | only the width of the "Open order" text link at the end of its row | only the width of the status tags inside their cells | none |
| VI again vs first VI | identical | identical | identical |

Defects found in the browser, all fixed:
1. **A picker chose a product that was not searched for** (`abcb192`). Typing `SMOKE-012` and pressing Enter at once put
   `SMOKE-011-14623` on the line: the options still were the answer to the previous (empty) search, and Enter takes the
   first one. Both pickers now offer no option while the typed text is not what the options answer (debounce pending or
   request running). Enter then chooses nothing. Test with a slow server answer; mutation caught.
2. **A green "processed" flashed for ~85 ms** before the order arrived — also before "not enough stock". While the order
   is loading there is no message now. Test with a delayed order response.
3. **The lines table of the detail was 7 px wider than its card**: a horizontal scrollbar appeared after the first
   re-render and pushed everything below down by 15 px (it looked like a language-switch shift). The table fits now.
4. **GUIDs wrapped onto two lines** in the task summary (correlation id, message id). The task summary has a 160 px
   label column now (`SHORT_SUMMARY_LABEL_WIDTH`); the ids are on one line.

Found by the tests while writing them (not in the browser): **an API error of the list as a whole stayed under the lines
after the next submit**, because the list has no rule that re-validates it. The order form clears it on submit.

## Verification (actually executed)
From `frontend/`, Node 24.16.0. Backend not built or tested (no backend change).

| Command | Result |
| --- | --- |
| `npm run typecheck` | exit 0, no output |
| `npm run lint` | ESLint: no findings; `All matched files use Prettier code style!` |
| `npm test` | `Test Files 17 passed (17)`, `Tests 167 passed (167)`, about 100 s |
| `npm run build` | `3230 modules transformed`, no size warning |
| `auth.test.tsx` × 10 | 10 × `Tests 15 passed (15)` |
| `scripts/smoke/task-013.ps1` | `PASSED: all 31 checks.` (twice) |
| `git status --short backend` | empty |

The full frontend suite was run three times after the last test was added (once by the smoke script), all green. The
first smoke run was made before the browser check; the table below is the second run, on the final code.

### Chunk sizes (final build)
| Chunk (largest first) | Size | gzip |
| --- | --- | --- |
| `jsx-runtime` (React and shared vendor code) | 319.39 kB | 106.33 kB |
| **`index` (main chunk)** | **254.46 kB** | **81.98 kB** |
| shared form/table code (named `TruncatedText` by the bundler) | 194.34 kB | 60.05 kB |
| `layout` (incl. both translation files) | 182.97 kB | 60.51 kB |
| `typography` / `date-picker` / `select` | 147.17 / 102.51 / 73.95 kB | 48.43 / 32.26 / 25.37 kB |
| `card` / `result` / `form` / `button` | 43.99 / 34.51 / 24.24 / 23.52 kB | 13.44 / 10.81 / 9.48 / 7.62 kB |
| `OrderEntryPage` / `OrderDetailPage` / `OrdersPage` | 9.34 / 7.22 / 5.19 kB | 3.45 / 2.56 / 2.06 kB |
| `UsersPage` / `InventoryPage` / `StockImportPage` / `ProductsPage` / `MovementsPage` | 12.94 / 12.70 / 6.77 / 5.93 / 5.17 kB | 4.33 / 4.58 / 2.46 / 2.35 / 2.03 kB |

### Smoke script (`scripts/smoke/task-013.ps1`, run by Claude Code, API and Workers running)
```
Step                                                                                         Expected                               Actual   OK
----                                                                                         --------                               ------   --
01 create smoke manager                                                                           201                                  201 True
02 create smoke customer                                                                          201                                  201 True
03 create smoke product (price 15000)                                                             201                                  201 True
04 import 5 units                                                                                 200                                  200 True
05 create smoke staff                                                                             201                                  201 True
06 submit order (website, 3 units)                                                                202                                  202 True
07 ... not a duplicate                                                                          False                                False True
08 same order again                                                                               202                                  202 True
09 ... is a duplicate                                                                            True                                 True True
10 ... of the same task                                          01a0f978-cb2d-7be8-8b8c-1cbdbe6af1ee 01a0f978-cb2d-7be8-8b8c-1cbdbe6af1ee True
11 same id, other quantity                                                                        409                                  409 True
12 ... errorCode                                                                       order_conflict                       order_conflict True
13 unknown channel slug                                                                           404                                  404 True
14 ... errorCode                                                                    channel_not_found                    channel_not_found True
15 task finished within 60 s                                                                Succeeded                            Succeeded True
16 task has an order id                                                                          True                                 True True
17 order status                                                                              Reserved                             Reserved True
18 order total computed by the server (price x 3)                                               45000                                45000 True
19 order has the task with an attempt                                                            True                                 True True
20 movements of the order: one                                                                      1                                    1 True
21 ... a reservation of 3                                                                   Reserve 3                            Reserve 3 True
22 submit order (pos, 3 units, 2 left)                                                            202                                  202 True
23 second task                                                                              Succeeded                            Succeeded True
24 second order status                                                              InsufficientStock                    InsufficientStock True
25 stock: 3 reserved, 2 available                                                                 3 2                                  3 2 True
26 orders: search by external order id                                                              1                                    1 True
27 orders: channel + status + customer contains the second order                                 True                                 True True
29 npm run build                                                                                    0                                    0 True
30 npm test                                                                                         0                                    0 True
31 lock the smoke manager                                                                         200                                  200 True
32 lock the smoke staff                                                                           200                                  200 True

PASSED: all 31 checks.
```
- Step 28 (`npm ci`) only runs when `frontend/node_modules` is missing. Steps 31 / 32 run in a `finally` block: the smoke
  accounts are locked again also when a step fails.
- **With the Workers host stopped** (run once on purpose): steps 01–14 passed, then
  `unexpected error: The first order did not reach a terminal state within 60 s. The Workers host is probably not running (README section 6).`,
  both accounts locked, `FAILED: 1 of 17 checks.`, exit code 1. The order that was left waiting was processed when the
  Workers were started again (`SMOKE-013-WEB-11453 processed: Reserved`).

### Mutation checks (each restored byte for byte; a hash of every changed file was compared before and after)
38 of 38 detected on the first run.

| Area | Mutations |
| --- | --- |
| Outcome | lists invalidated on every poll / never; every order invalidated instead of the list (the order is then loaded twice); polling window never ends; polls while the tab is hidden; `lastError` shown; a finished task still polled; "Check again" without a new window; "processed" before the order arrived; duplicate notice missing |
| Order form / panel | new external order id after a failed request; `order_conflict` not on the id field; old list error stays; id without the date; missing channel not flagged; form of an inactive channel hidden |
| Tab visibility | flag not corrected after `resetApiState` |
| Orders list | unknown status sent; customer id not checked; plain dates sent; a filter keeps the page; detail link without the list address; cached list on return; default order not shown; no inline alert; paging writes the default order into the address |
| Order detail | non-GUID id sent; "Back" ignores the list address; line total computed in the client; cached order on return |
| Movements | every order reference a link; no order link |
| Pickers | options of an older search offered; no title on product options; customer title without the contact; customer list loaded before opening; search on every key |
| Date range | end of range = start of day |

### Required tests → where
| # | Requirement | Test |
| --- | --- | --- |
| 1 | Order entry: panels, happy path, duplicate, insufficient stock, failed, message states, polling, errors, independence | `orderEntry.test.tsx` › "order entry" (11), "polling window" (3) |
| 2 | `CustomerSelect` | `orders.test.tsx` › "customer picker" (3) |
| 3 | Orders list | `orders.test.tsx` › "orders list" (8) |
| 4 | Order detail | `orderDetail.test.tsx` › "order detail" (7) |
| 5 | Movement history: order link | `movements.test.tsx` › "an order reference leads to the order…" |
| 6 | Language switch on the three pages (form with lines; outcome while polling) | "… : language switch" in the three files (4 tests) |
| 7 | Staff can use the pages | every test above runs as Staff unless it names a role |
| 8 | A1, A2 | `stockImport.test.tsx` › "product picker" (two long-label tests); `orders.test.tsx` › "customer picker"; `auth.test.tsx` |
| — | Stale picker options (browser finding) | `stockImport.test.tsx` › "offers no option while a search is pending…" |
| — | Helpers | `dateRange.test.ts` (3), `tabVisibility.test.ts` (4) |

Fake timers are used in exactly two places: `auth.test.tsx` (the expiry test) and `orderEntry.test.tsx` › "polling window"
(the 60 s window is jumped over). Both fake `setTimeout`, `clearTimeout` and `Date` with `shouldAdvanceTime`.

## Deviations from the task
1. **`setupListeners` is called with a custom handler, from the mounted application, not from the store**
   (`app/tabVisibility.ts`, used in `App.tsx`). Two facts found while verifying "it changes nothing else":
   - RTK's default handler registers listeners once per page load, for the first store only (a module-level flag);
   - the "focused" flag is part of the API state, and `resetApiState` (our logout) puts it back to the value it had when
     the page was loaded. In a tab that was opened in the background, a logout + login would leave polling switched off.
   The handler registers only `visibilitychange` (no focus / online / offline listeners at all) and corrects the flag
   whenever it differs from the real visibility. Verified: no query sets `refetchOnFocus` / `refetchOnReconnect`; a test
   shows a cached query is not fetched again when the tab is shown.
2. **A finished task invalidates the order *list* tag, not every `Order` tag.** With the whole type invalidated, the order
   the outcome is loading at that moment is fetched twice (RTK delays the invalidation until the request is done), which
   contradicts "`getOrder` once". The detail page refetches on open anyway.
3. **`useListQueryState` got an option `defaultSortDir`** (shared hook). Needed for "the `createdAt` column shows a
   descending indicator": AntD reports a shown default order like a chosen one, so every page change wrote `sortDir=desc`
   into the address. Pages that do not pass the option behave as before.
4. **One existing movement test was changed** ("Existing movement tests unchanged" could not hold): it asserted that an
   order reference is *not* a link. It now asserts that for a manual adjustment; the order link has its own test.
5. **Both pickers offer no option while a search is pending** — a behaviour change of the 012 `ProductSelect` (see
   "Real-browser check", defect 1).
6. **A1 added no tooltip component** (see A1): the native `title` was already there; it is explicit and tested now.
7. **"Clear filters" keeps the search text** (the search box has its own clear button), as on the other lists.
8. **Type names** follow the existing features (`OrderSummary`, `OrderDetail`, `OrderItem`, `OrderTask`,
   `ProcessingTaskDetail`) instead of the API's `…Response` names.
9. **Small UI choices not in the task:** the last line cannot be removed and the quantity input does not accept values
   below 1 (as in the import form); the outcome also shows the order's status tag and total as summary rows; the outcome
   asks for the task again when it opens (a resubmitted order shows the task as it is now, not a cached copy).
10. **Touched outside the listed pieces:** `StockImportForm.tsx` (the quantity width moved to `layout.ts` as
    `QUANTITY_INPUT_WIDTH`, no behaviour change); `customersApi.updateCustomer` also invalidates that customer's tag (the
    picker label of a renamed customer would be stale otherwise).

## Blocked / questions for Cowork
None blocking. One decision:
- **Which size does the report track?** The main chunk moves by tens of kB when the bundler regroups shared code (012:
  +67 kB, 013: +33 kB, neither was new code). Options: (a) track the first-load set (960 → 971 → 979 kB) and explain its
  growth; (b) pin the vendor groups with `manualChunks` so the main chunk becomes stable (chunk tuning, was out of scope);
  (c) keep the rule and run the module analysis every time. I recommend (a) now and (b) with 025.

## Out of scope / follow-ups
- **`StockImportForm` has the same stale list error** the order form had (an `items` error of the API stays under the
  lines after the next submit). One line (`form.setFields([{ name: 'items', errors: [] }])` on submit) plus a test; not
  done because the import form is not part of this task.
- **Translations are loaded up front in both languages** and grow with every page (2 × 14.3 kB now). Loading the active
  language only would take about half of it out of the first load.
- **Orders table at 1280 px:** the columns need 1,378 px, the content area is 992 px, so status, lines, total and the
  action scroll inside the table — the same pattern as the other lists, but here the status is the column users scan.
- **Panel title repeats itself** when the component name equals the channel name ("Website Website", "POS (tại quầy) POS").
- **Enter with no option on offer** closes the dropdown and clears the typed text in the browser (AntD behaviour); the
  user types again. Safe, slightly inconvenient for scanner-like input.
- **The 60 s window is wall-clock** from the 202: a tab hidden for a minute comes back to "still processing".
- **A message that could not be published (`Failed`) is not a terminal task state**, so it is polled for the 60 s like any
  other (as specified).
- Starting API and Workers at the same moment with `dotnet run` makes one build fail on locked files; start one after
  the other (already a gotcha in the handoff, seen again).
- Still open from earlier reports: unsaved-changes guard on the forms, `OrderEndToEndTests` reusing
  `RabbitMqBrokerFixture`, the EF Core error logging of expected 409s (014 Part A).

## Manual steps for the user
1. Start the API and, in its own terminal, the Workers (README sections 5 and 6); then `cd frontend; npm run dev`.
   No migration, no new packages.
2. Manual check (http://localhost:5173, any role):
   - **Nhập đơn hàng**: in "Website" pick a customer and a product, quantity 1, "Tạo đơn hàng". The outcome goes from
     "đang chờ" to "Đã giữ hàng" with the total; "Mở đơn hàng" opens the detail with the task and its attempt.
   - "Đơn hàng mới", type the previous number with the same content: the "already accepted" notice. Again with another
     quantity: the conflict text under the number.
   - Order more than is available: "Không đủ tồn kho…".
   - Stop the Workers, create an order, wait a minute: "vẫn đang được xử lý" + "Kiểm tra lại". Start the Workers, press it.
   - **Đơn hàng**: filter by channel and status, reload: filters and sort are still there. Switch `VI | EN`: only texts change.
   - On an order: "Biến động của đơn này", then the order link in that row leads back.
   - In a product picker type a SKU and press Enter immediately: nothing is chosen (before: the first product of the list).
3. Test data left in the development database: products `SMOKE-013-18209`, `SMOKE-013-11453` and one more `SMOKE-013-*`
   (three smoke runs), `SMOKE-013-UI-A/B/C-99189`; customers `Smoke customer 013-*` (three), `Smoke UI customer 013-99189`
   (two); imports `SMOKE-013-GRN-*`, `SMOKE-013-UI-GRN-99189`; orders `SMOKE-013-WEB-*`, `SMOKE-013-POS-*`,
   `WEB-20261002-0M2G3R`, `DP-20261002-10XR08`, `DP-20261002-EKWBCU`, `POS-20261002-WK2RH1`; locked users
   `smoke-013-mgr-*`, `smoke-013-staff-*` (three each), `smoke-013-ui-mgr-99189`, `smoke-013-ui-staff-99189`.
4. API and Workers were started by Claude Code for the checks and are stopped again.

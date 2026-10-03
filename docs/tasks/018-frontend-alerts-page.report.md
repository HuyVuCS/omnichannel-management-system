# Report 018 — Frontend: Alerts page, alert tiles on the dashboard + Part A (form-dialog reset, follow-ups)

Status: Done — with one finding that needs the user: **the development database does not have the `Alerts` table**
(migration `20261002215102_Alerts` of task 017 is not applied there, although the implementation log says it was). The
smoke script and the real-browser check of this task were therefore run on a throwaway environment, not on the
development database (question 1, manual step 1).
Commit: `0cf52b2` (A1), `eadacd4` (A2), `b3d3bff` (A3), `8b9866a` (A4), `4252f7f` (A5), `81030d0` (A6), `f347a64`
(Parts B and C), `0e39d97` (browser finding), `7aa26e4` + `0c6e7d9` (smoke script), `75e28cd` (radio-group names,
deviation 5), `ebfb38d` (the alerts test asserts the page in the address); the README and this report are the first
commit of task 018b ("task 018: report", review 018 P2).

## Summary
- **A1 (P1):** the seven form dialogs reset from inside their `<Form>` (`ResetFormOnOpen`, a layout effect) on every
  opening, counted by `useOpenCount` in the dialog component; `useResetFormOnOpen` and its "is the form mounted"
  heuristic are gone. The 016b test: old hook **3 failures in 30 runs**, fix **0 in 50**. Production build: 17 of 17
  reopen-while-closing rounds correct.
- **A2:** audit result - no product change needed. `AppLayout` already keys its `Suspense` by the path, so a detail
  page is remounted when its route parameter changes (the mechanism is "remount by key", the same for all three
  pages). Documented in the code and pinned by one test per page; each of the three fails without the key.
- **A3:** it was the bad case - a 400 on `phone` / `address` stayed after the next submit. Cleared before each submit,
  as in the mapping dialog.
- **A4:** the products table fits a 1280 px window in vi and en (measured: no horizontal scrolling, no cut header).
- **A5:** the smoke table starts with `OK` and wraps long values inside fixed columns; self-test 55/55.
- **A6:** `Active` → `Inactive` of a mapping resolves the pair's `StockSyncFailed` alert in the same transaction; five
  integration tests, 5 of 5 mutants killed.
- **Parts B, C:** the Alerts page (`/alerts`), the alert hint mappings, the dashboard card "Unresolved alerts".
- Backend 825/825 (820 + 5), 0 warnings. Frontend 338 tests; typecheck, lint, build clean. First-load set 968.23 →
  969.15 kB raw (317.42 → 317.69 kB gzip); the alerts page is a lazy chunk of 10.23 kB.

## Files changed
Frontend
- `shared/components/ResetFormOnOpen.tsx`, `shared/hooks/useOpenCount.ts` (new); `shared/hooks/useResetFormOnOpen.ts`
  (deleted); the seven dialogs: `ChannelFormModal`, `ChannelProductFormModal`, `CustomerFormModal` (also A3),
  `ProductFormModal`, `ChangeRoleModal`, `CreateUserModal`, `ResetPasswordModal`.
- `app/AppLayout.tsx` — comment only (A2: what the key of the `Suspense` also guarantees).
- `features/products/ProductsPage.tsx` — column widths, medium table size, small icon buttons (A4).
- `features/alerts/` (new): `alertsApi.ts` (`listAlerts`, `acknowledgeAlert`, types as `AlertResponse` /
  `AlertListQuery`), `paths.ts` (`ALERTS_PATH`, `ACKNOWLEDGE_ROLES`, `alertsListPath`), `AlertsPage.tsx`,
  `components/AlertTypeTag.tsx`, `AlertStatusTag.tsx`, `AlertDetails.tsx`, `AcknowledgeAlertButton.tsx`.
- `app/navigation.tsx` (alerts built, logs → `018b`), `app/router.tsx` (lazy `AlertsPage`).
- `shared/api/baseApi.ts` (tag type `Alert`), `features/realtime/hintTags.ts`, `hub.ts` (`alertId`, `alertType`),
  `features/products/productsApi.ts`, `features/channels/channelsApi.ts` (update → `{ Alert, LIST }`).
- `features/dashboard/DashboardPage.tsx`, `dashboardApi.ts` — card and `alerts` section of the summary type.
- `shared/hooks/useListQueryState.ts` — `patchParams` accepts a list of values for one key; `shared/utils/address.ts`
  — `someOf`; `shared/components/layout.ts` — `MULTI_FILTER_WIDTH`, comment of `SINGLE_ACTION_COLUMN_WIDTH`.
- `shared/i18n/vi.json`, `en.json` — `alerts.*`, `dashboard.alerts.*`, `errors.alert_not_found`,
  `errors.alert_resolved`, `common.info` (43 keys each).
- Tests: `features/alerts/alerts.test.tsx` (new, 31), `test/alerts.ts` (new fixtures); `products.test.tsx` (+3),
  `channelDetail.test.tsx` (+2), `orderDetail.test.tsx` (+1), `processingTask.test.tsx` (+1), `customers.test.tsx`
  (+1), `dashboard.test.tsx` (+5), `hintTags.test.ts` (+2 and changed assertions), `realtimeConnection.test.ts`,
  `realtime.test.tsx`, `auth.test.tsx`, `codeSplitting.test.tsx`, `test/dashboard.ts`, `test/server.ts` (deviations
  4 and 5).

Backend
- `Omnichannel.Infrastructure/Persistence/Channels/ChannelService.cs` — `UpdateProductAsync` in an explicit
  transaction, resolve on `Active` → `Inactive`.
- `Omnichannel.Tests/Integration/Workers/AlertLifecycleTests.cs` — five tests and their helpers.

Other
- `scripts/smoke/_common.ps1` (`Format-SmokeTable`), `_assert-selftest.ps1` (+4 cases), `task-018.ps1` (new).
- `README.md` — "Frontend" (form dialogs, remount by path, pages, event → tag map, dashboard card, alerts page),
  "Alerts" (resolution table, deactivated mapping, scripted checks).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend, final state) | **825 passed, 0 failed** (820 before; 1 m 33 s) |
| the five A6 tests alone, 5 mutants of `UpdateProductAsync` | 5 passed; **5 of 5 mutants killed** (table below) |
| `npm run typecheck`, `npm run lint` (frontend, final state) | clean |
| `npm run build`, `npm run measure` | built; first-load set 969.15 kB raw / 317.69 kB gzip (before: 968.23 / 317.42) |
| `npm test` × 5 consecutive (final state, PostgreSQL and RabbitMQ running, no API / Workers) | **5 of 5 green**, 338 passed in each (27 files); wall time 292 / 294 / 435 / 391 / 376 s (run on 2026-10-03 19:38 - 20:08 +07:00 as Part A1 of task 018b, details below) |
| `products.test.tsx -t "reopened before its close animation"` × 30, **old hook** (separate processes) | **3 failed of 30** (runs 3, 5, 27: "Trà xanh" where "Cà phê" is expected) |
| the same × 50, **fix** (the 016b test and the new same-record test in each run) | **0 failed of 50** (2 passed in each run) |
| dialog tests of the five pages on the A1 commit alone (rest of the work stashed) | 83 passed |
| mutant "the open count never changes" | all 6 reopen tests fail |
| A2: the three "another … on the mounted page" tests | pass; with `key={location.pathname}` removed from `AppLayout` all 3 fail |
| A3: "drops the API errors of the optional fields …" | fails before the fix and without the `phone` clearing; passes with it |
| `scripts\smoke\_assert-selftest.ps1` | PASSED: all 55 cases |
| `scripts\smoke\task-018.ps1` (throwaway environment) | **PASSED: all 35 checks**, incl. `npm run build` and `npm test` (338 passed, 421 s next to the running hosts). A first run had 4 red checks: three wrong counts in the script (deviation 14) and the flaky dashboard test (deviation 5) |
| `scripts\smoke\task-017.ps1` (throwaway environment; the older script with long values, for the table format) | **PASSED: all 53 checks** |
| Real-browser check, production build (`npm run build` + `vite preview`, throwaway environment, 1280 × 800) | done, see below |

Not executed: `task-018.ps1` and the browser check on the **development** database (it has no `Alerts` table, see
question 1); a keyboard-driven dialog check (see deviation 2).

### A1 — repetitions
```
old hook (commit 0afcad8 state), 30 separate runs:  1 ok 2 ok 3 FAILED 4 ok 5 FAILED 6 ok … 26 ok 27 FAILED 28 ok 29 ok 30 ok
RESULT old-hook-30: 3 failed of 30
fix, 50 separate runs:
RESULT fix-50: 0 failed of 50
```
Every failure of the old hook is the defect itself (`expect(element).toHaveValue(Cà phê)` … `Received: Trà xanh`), not
a timeout. An earlier, interrupted series with the old hook had 3 failures in its 8 completed runs. The probe of the
017 report was not rebuilt: the committed test is the measurement.

### A6 — mutants of `ChannelService.UpdateProductAsync`
| Mutant | Result | Killed by |
| --- | --- | --- |
| the alert is never resolved | killed | resolves-with-hint test, rolled-back-deactivation test |
| every update resolves the alert | killed | keeps-active test, inactive-already test |
| resolved whenever the mapping ends `Inactive` (also `Inactive` → `Inactive`) | killed | inactive-already test |
| resolved whenever the mapping was `Active` (also `Active` → `Active`) | killed | keeps-active test |
| the key of another pair (channel and product swapped) | killed | resolves-with-hint test, rolled-back-deactivation test |

### Full frontend runs
Five consecutive `npm test` runs (one script, each run a new process; `maxWorkers: 4`, `testTimeout` 20 s), executed on
2026-10-03 as Part A1 of task 018b on the frontend of `ebfb38d` (HEAD `c456656` only adds documents) with the README
changes of this task in the working tree. PostgreSQL and RabbitMQ were running (Docker), no API, no Workers host, no
dev server, no browser session, no `dotnet` build or test.

| Run | Result | Wall time | vitest `Duration` |
| --- | --- | --- | --- |
| 1 | 27 files, 338 passed, exit 0 | 292 s | 286.73 s |
| 2 | 27 files, 338 passed, exit 0 | 294 s | 292.42 s |
| 3 | 27 files, 338 passed, exit 0 | 435 s | 432.30 s |
| 4 | 27 files, 338 passed, exit 0 | 391 s | 388.47 s |
| 5 | 27 files, 338 passed, exit 0 | 376 s | 373.49 s |

No red run, so nothing to name. Two honest remarks about the conditions:
- The machine had about 1 GB of its 7.4 GB free when the series started (Docker's VM, the desktop applications). The
  runs are slower than the 178 - 207 s measured in task 017 for 292 tests; the suite is memory-bound.
- "Nothing else" was not strictly kept: while runs 1 - 5 were going I (Claude Code) read repository files and wrote
  backend source files, the smoke script and scratch files of task 018b - no build, no test run, no server, and no file
  under `frontend/` (the frontend files of 018b were written outside the repository until the series had finished).
  Runs 3 - 5 are the slower ones; the series was not repeated, since every run is green and extra load can only make a
  run slower or red, not greener.

### Bundle (`npm run measure`)
| | first-load set raw | gzip | main chunk raw / gzip |
| --- | --- | --- | --- |
| before (build of the state before this task) | 968.23 kB | 317.42 kB | 169.07 / 54.55 kB |
| after | 969.15 kB | 317.69 kB | 169.98 / 54.82 kB |

+0.92 kB raw: the route and menu entry, the `Alert` tag type and the two hint mappings. `AlertsPage` is its own
chunk (10.23 kB raw / 4.07 kB gzip), loaded when `/alerts` is opened; the dashboard chunk is 15.41 kB with the card.

### Smoke tables (Part A5 format: `OK` first, long values wrapped inside their column)
`scripts\smoke\task-018.ps1`, throwaway environment:
```
OK    Step                                                             Expected                           Actual
--    ----                                                             --------                           ------
True  01 create smoke manager                                          201                                201
True  02 create smoke staff                                            201                                201
True  03 create smoke product (threshold 5)                            201                                201
True  04 default list of the product: one LowStock alert               1                                  1
True  05 ... which is Open                                             Open                               Open
True  06 ... with the current stock and the threshold                  0 5                                0 5
True  07 import 10 units (above the threshold)                         200                                200
True  08 the alert left the default list                               0                                  0
True  09 ... and is Resolved                                           Resolved                           Resolved
True  10 the website channel exists                                    True                               True
True  11 map the product on website (SIM-PERM-)                        201                                201
True  12 its sync task (SkuRules must be on)                           Failed                             Failed
True  13 default list: one StockSyncFailed alert                       1                                  1
True  14 ... which is Open                                             Open                               Open
True  15 ... names the channel                                         0199a1b0-0000-7000-8000-0000000000 0199a1b0-0000-7000-8000-0000000000
                                                                       02                                 02
True  16 ... the failed task                                           01a100ed-7584-70e6-b360-f8009594a7 01a100ed-7584-70e6-b360-f8009594a7
                                                                       a3                                 a3
True  17 ... and its error code                                        channel_rejected                   channel_rejected
True  18 staff cannot change the mapping                               403                                403
True  19 ... the alert is still Open                                   Open                               Open
True  20 manager sets the mapping Inactive                             200                                200
True  21 ... no stock sync was started                                 True                               True
True  22 ... the StockSyncFailed alert is Resolved                     Resolved                           Resolved
True  23 ... with a resolution time                                    True                               True
True  24 ... and left the default list                                 0                                  0
True  25 re-activate with a normal SKU                                 200                                200
True  26 ... a stock sync was started                                  True                               True
True  27 the new sync task                                             Succeeded                          Succeeded
True  28 no unresolved StockSyncFailed alert for the pair              0                                  0
True  29 the resolved alert is kept, and it is the only one            1                                  1
True  30 ... still Resolved                                            Resolved                           Resolved
True  31 set the smoke mapping Inactive again                          200                                200
True  33 lock the smoke manager                                        200                                200
True  34 lock the smoke staff                                          200                                200
True  36 npm run build                                                 0                                  0
True  37 npm test                                                      0                                  0

PASSED: all 35 checks.
```
`scripts\smoke\task-017.ps1`, throwaway environment (ids and the summary's type list are the long values):
```
OK    Step                                                             Expected                           Actual
--    ----                                                             --------                           ------
True  01 create smoke manager                                          201                                201
True  02 create smoke staff                                            201                                201
True  03 create smoke customer                                         201                                201
True  04 create smoke product (threshold 5)                            201                                201
True  05 new product: one LowStock alert                               1                                  1
True  06 ... which is Open                                             Open                               Open
True  07 ... with the current stock and the threshold                  0 5                                0 5
True  08 import 10 units (above the threshold)                         200                                200
True  09 the LowStock alert is Resolved                                Resolved                           Resolved
True  10 staff submits an order (website, 6 units)                     202                                202
True  11 order task                                                    Succeeded                          Succeeded
True  12 the reservation opened a second LowStock alert                2                                  2
True  13 ... which is Open                                             Open                               Open
True  14 ... and is not the first alert again                          True                               True
True  15 staff cannot acknowledge                                      403                                403
True  16 ... the alert is still Open                                   Open                               Open
True  17 manager acknowledges                                          200                                200
True  18 ... the alert is Acknowledged                                 Acknowledged                       Acknowledged
True  19 ... by the smoke manager                                      smoke-017-mgr-59083@example.com    smoke-017-mgr-59083@example.com
True  20 acknowledge again                                             200                                200
True  21 ... changes nothing (same acknowledgedAt)                     True                               True
True  22 summary: alerts.acknowledged = acknowledged alerts in the     1                                  1
      list
True  23 ... and counts the smoke alert                                True                               True
True  24 summary: alerts.open = open alerts in the list                3                                  3
True  25 summary: every alert type listed                              LowStock,InsufficientStock,StockSy LowStock,InsufficientStock,StockSy
                                                                       ncFailed,ProcessingFailed          ncFailed,ProcessingFailed
True  26 import 10 more units                                          200                                200
True  27 the acknowledged alert is Resolved                            Resolved                           Resolved
True  28 acknowledge a resolved alert                                  409                                409
True  29 ... errorCode                                                 alert_resolved                     alert_resolved
True  30 staff submits an order of 100 units                           202                                202
True  31 order task                                                    Succeeded                          Succeeded
True  32 ... order recorded as InsufficientStock                       InsufficientStock                  InsufficientStock
True  33 InsufficientStock alert                                       Open                               Open
True  34 ... names the order                                           01a100f4-b090-7396-bc9b-b6441c5d62 01a100f4-b090-7396-bc9b-b6441c5d62
                                                                       55                                 55
True  35 import of the product                                         200                                200
True  36 ... resolves the InsufficientStock alert                      Resolved                           Resolved
True  37 the website channel exists                                    True                               True
True  38 map the product on website (SIM-PERM-)                        201                                201
True  39 its sync task (SkuRules must be on)                           Failed                             Failed
True  40 StockSyncFailed alert                                         Open                               Open
True  41 ... names the channel                                         0199a1b0-0000-7000-8000-0000000000 0199a1b0-0000-7000-8000-0000000000
                                                                       02                                 02
True  42 ... the failed task                                           01a100f4-b32a-7973-ab58-059fe7fab3 01a100f4-b32a-7973-ab58-059fe7fab3
                                                                       58                                 58
True  43 ... and its error code                                        channel_rejected                   channel_rejected
True  44 correct the external SKU                                      200                                200
True  45 the new sync task                                             Succeeded                          Succeeded
True  46 ... resolves the StockSyncFailed alert                        Resolved                           Resolved
True  47 default list of the product: resolved alerts hidden           0                                  0
True  48 ... and listed when asked for                                 4                                  4
True  49 unknown alert                                                 404                                404
True  50 ... errorCode                                                 alert_not_found                    alert_not_found
True  51 deactivate the smoke mapping                                  200                                200
True  53 lock the smoke manager                                        200                                200
True  54 lock the smoke staff                                          200                                200

PASSED: all 53 checks.
```

### Real-browser check (production build, throwaway environment, smoke Manager and smoke Staff, 1280 × 800)
- **A1, products edit dialog, "open A, type, Cancel, open B"** with 0 / 0 / 0 / 20 / 20 / 50 / 50 / 100 / 150 / 200 ms
  between Cancel and the next click: **10 of 10** show B (SKU, name, price 22.000, threshold 7, empty expiry date;
  A has an expiry date and had an unsaved name). In 7 rounds the dialog was in its close animation, in the three 0 ms
  rounds its form was still mounted.
- **A1, same record** ("open A, change name and threshold, Cancel, open A"), 0 / 20 / 50 / 100 / 200 ms: **5 of 5**
  show A's stored values. **Customers dialog:** A → B and A → A, 50 ms: both correct (B without phone and e-mail).
- **A4, products at 1280 px:** vi and en - table 992 px in a 992 px area, no horizontal scrollbar, no header or cell
  cut except long smoke SKUs (ellipsis + tooltip), column widths identical in both languages.
- **Alerts page at 1280 px:** vi and en - type / status / product / channel / details / last occurred at 0 - 888 px,
  action column fixed at 904 - 992 px, no page scrollbar; tags "Sắp hết hàng" 105 px and "Đã giải quyết" 105 px in
  their 150 / 126 px columns ("Low stock" 87 px, "Resolved" 82 px); "Available 4 / threshold 5" on one line; filters
  360 × 32 px with three values chosen in both languages; the table starts at the same height in both.
- **Live low stock:** alerts page filtered by a smoke product (stock 10, threshold 5), empty. A Staff order of 6 units
  finished at 15:16:26.743; the list was fetched again at 15:16:27.332 and the row "Sắp hết hàng | Đang mở | … | Khả
  dụng 4 / ngưỡng 5" was on screen at **15:16:27.485**, without a reload and without a toast.
- **Dashboard card:** "Đang mở 3" → "Đang mở 4" at 15:17:49.578 after an order of another smoke product finished at
  15:17:49.271; no reload.
- **Acknowledge (Manager):** confirmation with both texts, then "Đã ghi nhận cảnh báo.", the row shows "Đã ghi nhận"
  and "smoke-…@example.com · 03/10/2026 15:18"; the acknowledge action is gone, "Nhập kho" stays.
- **Resolved live:** an import of 10 units at 15:18:44.020; the row left the default list at **15:18:44.421**, no
  reload. With the status filter set to all three it is listed as "Đã giải quyết".
- **Staff:** ten columns, no "Thao tác" column, no button in any row; the six alerts are visible.
- No mapping was created in the browser; the smoke users, products and customers disappeared with the environment.

## What was built (answers the report items of the task)
### A1 — how an opening is identified
- `useOpenCount(opening)` lives in the **dialog component** (not its owner page): a counter that grows whenever
  `opening` (the edited record / state object / `true`) becomes a different truthy value. The dialog component renders
  on every change of what it is open for, also for the closed state between two openings; the dialog's memoised
  content may never render that closed state, which is why the count cannot be taken inside the form. The owners
  (pages) are unchanged.
- `<ResetFormOnOpen openCount={…} />` is the last child of each `<Form>`; it reads the instance with
  `Form.useFormInstance()` and calls `resetFields()` in a **layout** effect keyed on the count. It renders in the same
  commit as the `Form` that just received the new `initialValues`, so the reset always uses them, and the previous
  values are never painted.
- It also resets at the first mount after a complete close. That is harmless (the form was just given its
  `initialValues`) and removes the need to know whether the form is mounted: the heuristic
  `getFieldsError().length > 0` is gone. `clearOnDestroy` stays on every dialog `Form`.
- Mutation check: with a constant count all six "reopened before its close animation has finished" tests fail (the
  four existing ones and the two new ones).

### A2 — which mechanism
Remount by key, and it already existed: `AppLayout` renders `<Suspense key={location.pathname}>` around the page
(task 011, for the loading fallback). A changed route parameter is a changed path, so `OrderDetailPage`,
`ProcessingTaskPage` and `ChannelDetailPage` start as fresh mounts: no previous `data`, and no page state of the
previous record either (the channel page's "result of the last save" would not be covered by `currentData`). I added
no second mechanism; the comment in `AppLayout` now names this guarantee and the three tests pin it (each fails when
the key is removed, checked).

### A3 — which case
The bad one: after a 400 on `phone` and `address`, clearing the fields and submitting again left both API errors on
the fields (fields without a rule are not validated again on submit). `CustomerFormModal` now clears the errors of
`phone` and `address` before each submit, the mechanism of the mapping dialog. `email` has a rule and was not
affected. The test fails without the fix.

### A4 — products table
Widths 124 / 180 / 118 / 112 / 104 / 104 / 144 + 88 for the two actions = 974 px; the table uses AntD's medium size
(the default cell padding costs 16 px more per column) and small icon buttons. Measured in the production build at
1280 px, vi and en: table 992 px in a 992 px area, no horizontal scrolling, no header cut, same column widths in both
languages. Long SKUs are truncated with a tooltip, as before.

### A6 — backend
`UpdateProductAsync` opens a transaction, saves the mapping (the existing 23505 mapping is unchanged and now rolls the
transaction back), and resolves `stock_sync_failed:{channel}:{product}` only when the mapping was `Active` and is
`Inactive` now. `ResolveAsync` sends `alert.changed` only when a row changed.

### Parts B and C
- Address: `?status=Open&status=Resolved&type=LowStock&productId=…&channelId=…&from=…&to=…&sortBy=…&sortDir=…&page=…`
  - one entry per value, as the API reads them, always written in the order of the API's enums.
- No status chosen → no `status` in the request; the status control shows "Chưa giải quyết (đang mở + đã ghi nhận)" /
  "Unresolved (open + acknowledged)" as its placeholder.
- Columns: type, status, product, channel, details, last occurred | count, first occurred, acknowledged by, resolved
  at | actions (fixed to the right edge, Manager/Admin only). The first six plus the action column fit 1280 px.
- No `getAlert` query, no detail route, no toast / sound / badge, no client rule about which alerts exist or resolve.
- Fake timers are used nowhere in the new tests; the realtime tests wait real time for the one-second flush window
  (as the existing ones do).

## Deviations from the task
1. **A2 changed no product code.** The audit found the guarantee already in place (the keyed `Suspense` of
   `AppLayout`); adding `currentData` or a second key would have duplicated it. Delivered: the comment, three tests,
   the README sentence.
2. **A1 browser check was driven by script, not by the keyboard.** In the Browser pane Escape does not close a dialog
   and Tab stays inside it, so "Cancel, then the next row's edit button" was done with DOM clicks 0 - 200 ms apart
   (the dialog carried its `leave` animation class in 13 of 17 rounds; in the four 0 ms rounds the form was still
   mounted and the class not yet set). A human keyboard pass is part of the manual check below.
3. **A4 uses AntD's medium table size and small icon buttons** on the products page: with the default padding the
   eight columns do not fit 976 px without cutting the creation time. Rows are 49 px high instead of 55 px.
4. **Existing tests changed on purpose:**
   - `/alerts` was the "page without requests" of `auth.test.tsx` (3 tests), `codeSplitting.test.tsx`,
     `dashboard.test.tsx` and `realtime.test.tsx`; they use `/reports` (task 021) now - not `/logs`, which 018b builds.
   - `hintTags.test.ts` and `realtimeConnection.test.ts`: `inventory.changed` now also yields `{ Alert, LIST }`,
     the resync list has `Alert`, and `low_stock.created` is no longer the example of an unknown event.
   - `dashboard.test.tsx` "takes a custom range …" now ends after its last request was answered.
5. **A pre-existing flaky dashboard test was fixed in product code (two `name` props, commit `75e28cd`).**
   `dashboard.test.tsx` "restores a custom range …" failed in the first smoke run's `npm test` and, run alone with its
   predecessor, 4 times out of 4 - also on the commit before this task (1 of 2 there). Cause: under test every
   generated radio-group name is `test-id`, so the language switch (VI / EN) and the range presets were **one** native
   radio group, and a re-render of the header took the "checked" state away from the preset. `LanguageSwitch` and the
   presets now have explicit names (`language`, `dashboard-range`); in the browser nothing changes (the generated
   names were unique there). After the fix: the pair 4 of 4, the file 5 of 5.
6. **`useListQueryState.patchParams` accepts a list of values** for one key (one address entry per value), and
   `shared/utils/address.ts` has `someOf` next to `oneOf`: the status and type filters take several values and the
   address mirrors the API (`?status=Open&status=Resolved`).
7. **Alerts table: the action column is fixed to the right edge**, and the history columns (count, first occurred,
   acknowledged by, resolved at) scroll. Eleven columns do not fit 992 px; "fit" is met for what an alert is and what
   can be done about it (measured: first six columns 0 - 888 px, actions 904 - 992 px, vi and en identical).
8. **Short type labels** so that the tag fits its column in both languages: "Thiếu hàng" / "Stock shortage",
   "Lỗi đồng bộ kho" / "Sync failed", "Lỗi xử lý" / "Task failed" (the filter options use the same labels).
9. **Browser finding fixed (`0e39d97`):** with three statuses chosen the 280 px status filter wrapped onto a second
   line and its placeholder was cut; both multi-value filters are 360 px (`MULTI_FILTER_WIDTH`).
10. **A6 has one test more than listed** (an update of a mapping that is `Inactive` already leaves a late alert open):
    without it the mutant "resolve whenever the mapping ends Inactive" survived. The tests use the simulated channel
    with `ChannelSimulation:SkuRules` (`SIM-PERM-` SKUs), as the task words it.
11. **A5 self-test has four more cases** (55); `Format-SmokeTable` is a function of its own so that it can be tested.
12. **The dashboard card** sits after "Inventory now" (both are "now") and shows the type as the tag of the alerts
    page; an information icon (`common.info`) carries the tooltip.
13. **Smoke scripts and browser check ran on a throwaway environment** (own PostgreSQL / RabbitMQ containers, all
    eight migrations, API and Workers from the Debug build in environment `Smoke`, role `omnichannel_app`), because
    the development database has no `Alerts` table (question 1). There a script's frontend steps run next to the
    hosts (it only stops hosts it started itself).
14. **`task-018.ps1`: the first run had three wrong checks of mine** (`@(Get-ProductAlerts …).Count` counts an empty
    list as one item; steps 08, 24, 28) - corrected before the run reported below.

## Blocked / questions for Cowork
1. **The development database has no `Alerts` table.** `GET /api/alerts`, `GET /api/dashboard/summary` and
   `POST /api/products` answer 500 there; the API log says `42P01: relation "Alerts" does not exist`. The log of 017
   records the migration as applied by the user on 2026-10-03 - on this machine's development database it is not.
   Until it is applied (manual step 1) the development environment cannot create products, import stock, reserve
   orders or show the dashboard with the code of 017 / 018. Is the log entry about another database, or was the
   volume re-created since? (My probe created two smoke users, both locked again, and two "Smoke UI KH" customers
   there; nothing else was written.)
2. **Radio-group names under test (deviation 5).** Any future segmented control needs an explicit `name`; should that
   become a rule in the log? A lint rule is not worth it for two controls.
3. **Medium table size on the products page (deviation 3).** Accept, or keep the default size and let the least
   important column (creation time) scroll, as on the orders page?
4. **Other tests that end with a request in flight.** I fixed the one this task touched. A systematic pass (every test
   whose last action triggers a request) is about an hour; worth a Part A item in 018b?
5. **`createProduct` does not invalidate the alert lists** (the task names product *update* and channel update only).
   A new product opens a low-stock alert; the `alert.changed` hint refreshes a mounted list, and a list opened later
   fetches anyway. Leave as is?

## Manual steps for the user
1. **Apply the migration `Alerts` to the development database** (README section 4, migrator role), then restart the
   API and the Workers host. From `backend/`, with `OMNICHANNEL_MIGRATOR_CONNECTION` set:
   `dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api`
2. Then run `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-018.ps1` on the development environment and
   paste the table (it starts and stops the hosts itself and ends with the frontend build and tests).
3. Manual check (5 minutes, a Manager account, API + Workers + `npm run dev`):
   - Products: create a product with threshold 5 and import 10 units. Open **Alerts** filtered by that product: empty.
   - In a second tab enter an order of 6 units (Order entry, Website). The first tab shows a "low stock" row within
     about a second, without a reload; the dashboard card "Unresolved alerts" counts one more.
   - Acknowledge the row (the check mark, confirm): status "Acknowledged", your e-mail and the time in
     "Acknowledged by".
   - Import 10 units: the row leaves the list; with the status filter "Resolved" it is there with its resolution time.
   - Products: open "Edit" of one product, press Cancel and at once "Edit" of another one (keyboard: Tab, Enter) -
     the dialog shows the second product. Repeat with the same product after typing something: its stored values.
   - Sign in as Staff: the alerts page has no action column.

## Out of scope / follow-ups
- Logs / audit page and `GET /api/system-events` (018b).
- A systematic pass over tests that end with a request in flight (question 4).
- `IX_Alerts_TaskId`, `IX_Alerts_OrderId`, the e-mail lookups by text join (026, review 017).
- Purge or archive of resolved alerts (026); alert e-mails (024).
- The DLQ keeps one `SIM-PERM-` message per smoke run (known, review 016b).

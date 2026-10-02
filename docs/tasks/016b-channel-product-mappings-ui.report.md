# Report 016b — Frontend: channel product mappings on the Channels page

Status: Done (one deviation that needs Cowork's eyes: a pre-existing production-only defect of every form dialog was
found and fixed; one open question: the frontend suite is flaky under load)
Commit: `6fb1896` (A1), `4d67b5f` (A2), `253edbf` (A3), `f279fac` (Part B), `e6f9eb7` (form dialogs, see
"Deviations"), `af72fbf` (Parts C, D), `37c5f44` (browser finding), `6ebefe9` (smoke script), plus this report.

## Summary
- Part A1–A3, Part B (`stockSyncEnabled`), Part C (stock-sync column, name → detail) and Part D (channel detail
  `/channels/:channelId` with the mapping list, create / edit dialog and the sync-result alert) are implemented with
  tests. No migration, no new dependency, no new endpoint.
- **Found on the way (pre-existing, production only):** after a form dialog was closed completely, opening it for
  another record showed the **previous record's values** - saving would have written them onto the other record. The
  development server hides it (React StrictMode mounts every field twice, which happens to reset the values); a
  production build and the tests (no StrictMode) do not. Reproduced in `vite preview` with the product dialog, fixed
  for all seven dialogs (the six existing ones and the new mapping dialog), verified in the production build.
- The real-browser run found one flaw of the new code (the result alert showed the previous task's status for about
  150 ms after the next save); fixed with a test.

## Files changed
Backend
- `Omnichannel.Application/Channels/ChannelContracts.cs` — `ChannelResponse.StockSyncEnabled`.
- `Omnichannel.Infrastructure/Persistence/Channels/ChannelService.cs` — `WithStockSync` after the query (list, get,
  create, update); the SQL projection leaves the flag false.
- `Omnichannel.Infrastructure/Persistence/OmnichannelDbContextDesignTimeFactory.cs`,
  `Omnichannel.Infrastructure/RabbitMQ/RabbitMqOptions.cs`, `Omnichannel.Workers/appsettings.json` — `127.0.0.1` (A1).
- `Omnichannel.Tests/Integration/ChannelEndpointsTests.cs` — 6 new tests (seeded channels ×3, JSON member, API-created
  channel in create / list / get / update, Inactive seeded channel keeps `true`).

Frontend
- `features/realtime/hintTags.ts`, `realtimeConnection.ts` (comment) — the coalescer is a throttle (A2).
- `features/orders/components/OrderOutcome.tsx` — list invalidation only when not live (A3).
- `features/channels/paths.ts` (new), `channelsApi.ts` — `stockSyncEnabled`, `getChannel`, mapping types and the three
  mapping endpoints, tag `ChannelProduct`.
- `features/channels/ChannelsPage.tsx`, `components/StockSyncTag.tsx` (new) — Part C.
- `features/channels/ChannelDetailPage.tsx`, `components/ChannelProductFormModal.tsx`,
  `components/SyncResultAlert.tsx` (all new) — Part D.
- `app/router.tsx`, `shared/api/baseApi.ts` (tag type), `features/products/productsApi.ts` (a product update
  invalidates the mapping lists), `shared/i18n/vi.json` / `en.json` (46 new keys each).
- `shared/hooks/useResetFormOnOpen.ts` and the `Form` of `ChannelFormModal`, `CustomerFormModal`, `ProductFormModal`,
  `ChangeRoleModal`, `CreateUserModal`, `ResetPasswordModal` — the dialog fix.
- Tests: `channels/channelDetail.test.tsx` (new, 25), `channels/channels.test.tsx` (+4), `test/channels.ts` (new
  fixtures), `realtime/hintTags.test.ts`, `realtimeConnection.test.ts`, `realtime.test.tsx`,
  `dashboard/dashboard.test.tsx`, `processing/processing.test.tsx`, `orders/orderEntry.test.tsx`,
  `products/products.test.tsx`, `customers/customers.test.tsx`, `users/users.test.tsx`, `test/orders.ts`.

Other
- `README.md` — sections 3 (why `127.0.0.1`), 4, 6, "Frontend" (throttle, dialog convention, Channels, page list, order
  outcome), "API conventions" (`stockSyncEnabled`).
- `scripts/smoke/task-016b.ps1` (new).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend, after Part B) | 0 errors, 0 warnings |
| `dotnet test` (backend, after Part B; no backend change since) | 752 passed, 0 failed (746 + 6 new) |
| `npx tsc -b --noEmit`, `npm run lint` (final state) | clean |
| `npm run build` (final state) | ok |
| `npx vitest run` (full suite, final code) | 281 tests in 26 files (243 + 38). **3 of 5 full runs passed 281/281**; 2 runs failed, see below |
| `scripts\smoke\task-016b.ps1` (development environment, API and Workers already running) | 1st run: 24 of 25 (`npm test` exit code 1); 2nd run: **PASSED: all 25 checks** |

Full-suite runs on the final code, in order: (1) 279/281 - `processing.test.tsx` "writes each filter to the address…"
timed out at 20 s and the next test of that file failed after it; (2) 281/281; (3) inside the first smoke run: exit
code 1, output not kept (the script discarded it; it now writes npm's output to a log file and names it on failure);
(4) 281/281; (5) inside the second smoke run: exit code 0. The failing tests are existing ones that take 7.6 s alone
(`processing.test.tsx` alone: 12/12). An earlier run with 12 failures happened while I had started the API, the
Workers host, the dev server and a browser session next to it; it is not counted. See "Blocked / questions" 1.

Smoke table (second run):
```
Step                                      Expected    Actual   OK
01 create smoke manager                        201       201 True
02 create smoke staff                          201       201 True
03 website has stockSyncEnabled               True      True True
04 pos has stockSyncEnabled                   True      True True
05 digital-platform has stockSyncEnabled      True      True True
06 staff reads the website channel             200       200 True
07 ... with stockSyncEnabled                  True      True True
08 create smoke product                        201       201 True
09 import 5 units                              200       200 True
10 staff cannot map a product                  403       403 True
11 manager maps the product on website         201       201 True
12 ... with a sync task                       True      True True
13 sync task                             Succeeded Succeeded True
14 manager changes the external SKU            200       200 True
15 ... which starts a new sync task           True      True True
16 second sync task                      Succeeded Succeeded True
17 manager deactivates the mapping             200       200 True
18 ... answer has the member syncTaskId       True      True True
19 ... which is null                          True      True True
20 staff finds the mapping by its SKU            1         1 True
21 ... and it is Inactive                 Inactive  Inactive True
23 npm run build                                 0         0 True
24 npm test                                      0         0 True
26 lock the smoke manager                      200       200 True
27 lock the smoke staff                        200       200 True
PASSED: all 25 checks.
```
(22 = `npm ci`, 25 = clean-up of the mapping: both only run when needed.)

Tests shown to fail without their fix: the four "dialog opened again after it was closed completely" tests (4 of 4
fail with the old dialogs); "never shows the status of the task of the previous save…" (fails with `data`, passes with
`currentData`); the push test of A3 asserted 3 fetches per list before the change and asserts 2 now.

### Bundle (`npm run measure`)
| State | First-load set raw / gzip | Main chunk raw |
| --- | --- | --- |
| before (HEAD `a5ac20c`) | 967.48 kB / 317.24 kB (9 chunks) | 260.70 kB |
| after | 968.23 kB / 317.42 kB (9 chunks) | 169.07 kB |

+0.75 kB (route entry, path helpers). The main chunk shrank only because the bundler moved shared code into
`jsx-runtime-*.js` (343.79 → 436.16 kB); the set is what counts. New chunk `ChannelDetailPage-*.js`: 9.5 kB raw, not in
the first-load set. Translations: vi 17.91 → 20.22 kB, en 14.83 → 16.56 kB (loaded on demand).

## Real-browser check (real API + Workers in Development, 1280×800, smoke Staff / smoke Manager, both locked again)
The Browser pane was hidden for most of the run: the page got no animation frames, so dialogs only opened and closed
while a screenshot was taken. Every step below was still done with real clicks and keys; results were read from the
DOM. The steps after the first two were done in the **production build** (`npm run build` + `vite preview`).

Done:
- Channels list (Staff), vi and en: column "Đồng bộ tồn kho" / "Stock sync" with icon + text; the tooltip of "Not
  synced" on a leftover smoke channel; table 992 px in a 992 px content area, no horizontal scroll, nothing cut,
  identical column widths in both languages (246/188/223/168/168).
- Detail of an Inactive channel without a queue: both info alerts, no task link.
- Detail of `website` (Staff): no create / edit controls; the task link opened
  `/processing?taskType=StockSync&channelId=…`, where both filters were set and only website stock-sync tasks listed.
- Manager maps a smoke product: toast, list refreshed, alert "Đang chờ" → "Thành công" **without reload after 1.8 s**
  (connection indicator live).
- External SKU `SIM-PERM-24463`: alert shows "Thất bại" (error tag) after 2.9 s; "Mở tác vụ" opened the task detail
  with one attempt, `channel_rejected`, the simulator's message.
- Product already mapped: "Sản phẩm này đã được gắn trên kênh này." on the product field, dialog stays open, the
  previous result alert stays.
- Deactivate: no confirmation, alert "Không có đồng bộ tồn kho nào được bắt đầu…", row "Ngừng" with its update time.
  Re-activate: a new task, "Pending" → "Succeeded" after 2.8 s.
- Leaving the page and coming back: no result alert.
- VI/EN: every measured rectangle of the detail page (title, buttons, summary, label widths 160, result alert, search,
  table, column widths 132/152/126/126/98/134/134/89, page-size select) is identical in both languages; the result
  alert and the address survive the switch. The open edit dialog is 480×536 with identical field rectangles in both
  languages (the footer buttons are as wide as their text, like in every existing dialog).
- Dialog fix, production build: product A → Cancel → (dialog destroyed) → product B showed **A's SKU, name, price**
  before the fix and B's after it. The create dialog of the mapping page opened empty after a save.
- All smoke mappings of the run are Inactive (script output: `website: 2 smoke mapping(s), 0 active`).

Not done / limits:
- The Admin view of the channels list (with the action column, 976 px by the test) was not measured in the browser:
  no Admin session was opened.
- The "why?" tooltip of the "no stock sync" alert was not hovered in the browser (test only).
- A language switch while a dialog is open cannot be clicked in a real browser (the dialog's mask covers the header),
  so the dialog was measured once per language. The test does it with pointer checks off.

## Deviations from the task
1. **Form dialogs (commit `e6f9eb7`), outside the literal scope.** `useResetFormOnOpen` reset the form in the effect of
   the open, one render *before* the destroyed form is mounted again: the unmounted instance was reset to the previous
   record's `initialValues`, and the new form then kept them. Fix: the hook leaves an unmounted form alone and every
   dialog `Form` has `clearOnDestroy`. Applied to the six existing dialogs because the new dialog uses the same
   mechanism and the defect corrupts data (log rule of review 012: adopt the mechanism at the existing producers and
   report it). Regression tests for products, customers, channels, users.
2. A2 changes assertions of existing tests (the behaviour they pinned changed): `hintTags.test.ts` (coalescing section
   rewritten), `realtimeConnection.test.ts` (4 tests), `realtime.test.tsx`, `dashboard.test.tsx`,
   `processing.test.tsx` (one burst test each; they now count relative to the page's own requests, because the connect
   invalidation is immediate and only refetches a page that is already mounted). `cancel()` drops what is pending but
   keeps the running window, so the bound "one flush per window" also holds after it.
3. A3: the push test expects 2 fetches per list instead of 3; two tests added (task-only hint → no list request;
   connection lost → polling → exactly one).
4. `test/orders.ts` `channel()` and the fixture of `channels.test.tsx` got `stockSyncEnabled: true`; the language test
   of the channels list got two more texts.
5. "Back" goes to `/channels` (the list has no address state to keep).
6. The result alert names the product ("… cho TEA-01"); the mapping table is `size="small"` with short column titles
   ("Mã trên kênh" / "External ID") so that eight columns fit 992 px; the task link sits in the summary next to the
   stock-sync tag; mapping status labels are short ("Hoạt động" / "Ngừng").
7. The mapping dialog removes the API errors of the two external-id fields before each submit (they have no client
   rule, so nothing else removes them) - the mechanism `OrderEntryForm` uses for its list.
8. The mapping list is requested only once the channel is loaded (a channel that does not exist gets one 404, not two).
9. `task-016b.ps1` keeps npm's output in a temp log and names it on failure; `stockSyncEnabled` is compared as text
   (PowerShell: `$true -eq 'missing'` is true).

## Blocked / questions for Cowork
1. **The frontend suite is flaky under its own load.** 2 of 5 full runs failed with whole-test timeouts (20 s) in long
   existing tests; the machine has 12 cores but 7.7 GB RAM with about 2.2 GB free while Docker runs. The suite grew
   from 243 to 281 tests. Options: `maxWorkers: 3`, split the long list tests, or a higher `testTimeout`. Not changed
   (the log fixes `maxWorkers: 4` until 025).
2. **StrictMode hid a production defect** (deviation 1). Tests run without StrictMode, the dev server with it. Should
   the browser check of every frontend task include one pass on `vite preview`?
3. Not checked, same class as the browser finding: other places that show `data` of an RTK Query hook whose argument
   can change while mounted.
4. Not checked: `CustomerFormModal` has optional fields without client rules; an API error on them probably stays
   after the next submit (what deviation 7 fixes for the mapping dialog).

## Out of scope / follow-ups
- The products table scrolls horizontally at 1280 px (seen in the browser; existing).
- Other smoke scripts use `Assert-Step … $true <value>`; a non-boolean value would pass.
- One message from the `SIM-PERM-24463` check is in `stock.sync.website.dlq`.

## Manual steps for the user
- **A1 - local connection settings** (placeholders, not values):
  ```powershell
  cd backend/src/Omnichannel.Api
  dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
  cd ../Omnichannel.Workers
  dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
  # only if RabbitMq:HostName is set in the user-secrets:
  dotnet user-secrets set "RabbitMq:HostName" "127.0.0.1"
  cd ../..
  $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
  ```
  If `backend/src/Omnichannel.Api/.env` or another local file holds a connection string with `Host=localhost`, change
  it there too.
- Restart the API and the Workers host (new build: `stockSyncEnabled`).
- Manual check: sign in as Manager → Channels → "Website" → "Gắn sản phẩm" → choose a smoke product, external SKU
  `WEB-<anything>` → the alert goes from "Đang chờ" to "Thành công" without a reload. Set the mapping to "Ngừng"
  afterwards.
- Test data left in the development database: products `SMOKE-016B-UI-24463-A/B/C` and two `SMOKE-016B-<n>`, their
  stock imports, six locked `smoke-016b-*` users, four Inactive website mappings.

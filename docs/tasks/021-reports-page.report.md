# Report 021 — Frontend: Reports & Excel export page; labels for exports; Part A (review 020, review 019)

Status: Done — one check is left to the user (the browser actually *saving* a downloaded file, see "Blocked / questions")
Commit: `896d27c` (Part B); Part A: `b492b86` (A1–A4), `076f518` (A5); Cowork's documents of the round: `3a94587`

## Summary
The Reports page `/reports` (Manager/Admin) is built: one request form per report (orders / inventory) with the
lists' filters, the browser's time zone and the UI language; "My exports" with live status (cache tag `ReportExport`,
polling only while not live and an export is unfinished) and a download that keeps the link nowhere; "Export to Excel"
on the Orders and Inventory pages; `ReportExport` and the two export event types have labels everywhere. Part A: the
sweep ends the alert of an export it expires, a retry of an expired export is refused, an export is `Expired` from its
`ExpiresAt` on, `SixLabors.Fonts` is pinned, smoke 020 step 17 reads the right way round, and a thumbnail whose link
expired asks for fresh links once. A0: the pending development steps of 019/020 were applied by Claude Code
(migrations `ProductImage` and `ReportExports` are on development). No migration, no new package in this task.

## Files changed
Backend (A1–A4, `b492b86`)
- `Omnichannel.Application/Reports/ReportContracts.cs` — `ReportExportExpiry` (`IsPastRetention`, `StatusOf`): the one rule for "over".
- `Omnichannel.Infrastructure/Persistence/Reports/ReportExportCleanup.cs` — `DeletedAt` and `AlertStore.ResolveAsync` of the same tasks in one transaction (A1).
- `Omnichannel.Infrastructure/Persistence/Processing/ProcessingTaskService.cs` — retry of a `ReportExport` task past its retention → 409 `export_expired`, nothing written (A1).
- `Omnichannel.Infrastructure/Persistence/Reports/ReportExportService.cs` — `Expired` by time in list / detail; download 409 `export_expired` before "not ready" (A2).
- `backend/Directory.Packages.props` — `CentralPackageTransitivePinningEnabled`, `SixLabors.Fonts` 1.0.0 (A3).
- `scripts/smoke/task-020.ps1` — step 17 (A4).
- Tests: `Reports/ReportExportExpiryTests.cs` (new), `Integration/ReportExportEndToEndTests.Retention.cs` (new),
  `Integration/ReportExportEndpointsTests.cs`, `ReportExportEndToEndTests.Worker.cs`, `ReportExportTesting.cs`.
- `README.md` — expiry by time, sweep + alert, refused retry, the pin.

Frontend A5 (`076f518`)
- `features/products/imageLinks.ts` (`linkHasExpired`), `components/ProductThumbnail.tsx` (`onLinkExpired`, asks once
  until an image has loaded), `ProductsPage.tsx` (one refetch per burst), `components/ProductImageField.tsx` (asks for
  its one product), `productsApi.ts`, `productImages.test.tsx`.

Frontend Part B (`896d27c`)
- `features/reports/` (new): `paths.ts`, `reportsApi.ts`, `exportRequest.ts`, `download.ts`, `ReportsPage.tsx`,
  `components/{OrdersExportForm,InventoryExportForm,ExportFileNote,ExportsTable,ExportFilterSummary,ExportStatusTag}.tsx`,
  `reports.test.tsx`.
- `app/navigation.tsx`, `app/router.tsx` — the item gets `REPORTS_ROLES`, the page is a lazy chunk.
- `features/orders/OrdersPage.tsx`, `features/inventory/InventoryPage.tsx` — "Export to Excel" (Manager/Admin).
- `features/realtime/hintTags.ts`, `shared/api/baseApi.ts` — tag type `ReportExport` (hint map, resync set).
- `features/processing/processingApi.ts` (`TASK_TYPES`), `features/logs/systemEventsApi.ts` + `eventSummary.ts`
  (two event types), `shared/utils/format.ts` (`formatFileSize`), `shared/i18n/{vi,en}.json`.
- Tests: `test/reports.ts` (new), `test/systemEvents.ts`, and the test files of orders, inventory, logs, alerts,
  dashboard, auth, realtime, `app/codeSplitting`, `hintTags`, `productImages`.
- `scripts/smoke/task-021.ps1` (new), `README.md` ("Frontend": *Reports & Excel export*, expired image links;
  "Reports and Excel export": *Reports page*, the client constant next to `MaxRangeDays`).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend, final state) | 0 errors, 0 warnings |
| `dotnet test` (backend, final state) | **1300 passed**, 0 failed (2 m 28 s) — 22 more than 020 |
| `npm run typecheck` / `npm run lint` | exit 0 / exit 0 |
| `npm test` × 5 consecutive | **5 of 5 green**, 452 tests, 30 files; wall 432 s, 259 s, 218 s, 234 s, 290 s |
| `npm test` (sixth run, inside smoke 021, after the last test change) | green |
| `npm run test:slow` (full) | run 1: 1 red; run 2: 7 red; **run 3: 452 / 452 green** (294 s) — see below |
| `npm run build` + `npm run measure` | first-load set **970.14 → 971.00 kB raw**, 318.10 → 318.40 kB gzip (+0.86 kB raw: paths, tag, labels); main chunk 170.96 → 171.80 kB; `ReportsPage` is its own chunk, 12.8 kB raw |
| `scripts\smoke\_assert-selftest.ps1` | PASSED: all 55 cases |
| `scripts\smoke\task-021.ps1` on development | **PASSED: all 25 checks** (table below) |
| `dotnet list package --include-transitive` before / after the pin | 286 lines, **0 differences** |

**Slow runs.** Run 1 (quiet machine): only *asks for the list once when twenty thumbnails fail…* (my A5 test) was red.
Run 2 (the Browser pane of the browser check still open, 1.9 GB free): seven red in three files — the same test, two
of `stockImport.test.tsx` (one "Test timed out in 20000ms"), `dashboard.test.tsx` *shows every number…*, and three of
`productImages.test.tsx` (the first test of the file could not find the list in time; the two after it carried on from
there). Cause of my test, fixed in the test: it looked twenty images up by role and name inside `waitFor`, which under
the slow mode and load took long enough to keep the answer of the refetch from being rendered before the wait gave up
(first attempt at a fix — firing the twenty failures in one `act` — was right but not sufficient); it now uses plain
DOM queries. The other six were not touched and were green in run 3 and in every normal run: load timeouts, no
application defect found. Run 3 (pane closed): green.

**A3.** nuget.org, package metadata: `SixLabors.Fonts` 1.0.0 `<license type="expression">Apache-2.0</license>`; 2.0.0
`<license type="file">LICENSE</license>`. Pin bites — `SixLabors.ImageSharp.Drawing` 2.1.0 added temporarily to
`Omnichannel.Infrastructure`, restore, both files put back:
```
error NU1109: Detected package downgrade: SixLabors.Fonts from 2.0.1 to centrally defined 1.0.0. Update the centrally managed package version to a higher version.
error NU1109:  Omnichannel.Infrastructure -> SixLabors.ImageSharp.Drawing 2.1.0 -> SixLabors.Fonts (>= 2.0.1)
```
(plus NU1902/NU1903 advisories of the probe package itself, irrelevant here).

**Smoke 021 on development** (steps 01–23 API, 25–26 frontend):
```
True  03 staff: the list of exports is forbidden                       403        403
True  05 manager: inventory export -> 202                              202        202
True  06 the Report Export Worker reached the object storage (…)       True       True
True  07 the export is built within 120 s (…)                          Succeeded  Succeeded
True  09 ... shows the export with status Succeeded                    Succeeded  Succeeded
True  10 ... as an inventory report with one row and a size            True       True
True  11 ... with the filters and the time zone of the request (…)     SMOKE-021-75878 False Asia/Ho_Chi_Minh  (same)
True  12 the filter reportType=Orders leaves it out                    0          0
True  13 download link -> 200                                          200        200
True  14 GET the link (no Authorization header) -> 200                 200        200
True  15 ... the bytes start with PK (an xlsx file)                    True       True
True  16 ... served as an attachment (the browser saves it, …)         True       True
True  17 retry of the succeeded export task -> 409                     409        409
True  18 ... task_not_failed                                           task_not_failed  task_not_failed
True  20 ... its processing section lists ReportExport                 True       True
True  21 report.export_requested names the requester and the report    smoke-021-mgr-75878@example.com Inventory  (same)
True  22/23 lock smoke account 1/2                                     200        200
True  25 npm run build                                                 0          0
True  26 npm test                                                      0          0
PASSED: all 25 checks.
```
(01, 02, 04, 08, 19: 201 / 201 / 201 / 200 / 200.)

**Browser check** — production build (`npm run build` + `vite preview`, port 4173) against the development API and
Workers, 1280 × 800, smoke users `smoke-021-ui-*` (locked afterwards):
- Manager, vi: Orders page with `channel=pos&search=…` → "Xuất Excel" → `/reports` (nothing in the address); the
  orders form shows channel POS, the search text, the default range 06/09–05/10/2026, "Asia/Bangkok" (the browser's
  IANA zone) and the language note; no horizontal page scroll.
- Submit → message; the row went **Đang chờ → Đang xử lý → Thành công in 2.6 s by push** (07:38:17.288 → 18.500 →
  19.855; four list requests: the mutation's invalidation and three hints; no 5-second poll). Row: 2 rows, 9 KB.
- Download click: one anchor, host `127.0.0.1:8333`, path `/omnichannel-dev/exports/{taskId}.xlsx`, signed,
  `rel="noreferrer"`, no `target`, removed again; afterwards no link in the DOM, `sessionStorage`, `localStorage` or
  the page text; the page stayed on `/reports`. A fetch of that link without credentials: 200, 9263 bytes starting
  `PK`, content type of an xlsx file. **The click was recorded and not followed** — see "Blocked / questions".
- Inventory export: Pending → Succeeded live (35 rows, 9,9 KB); the hidden orders form kept its values.
- en: every text of the page, "Excel export" on Sync & Processing (column and filter) and on the dashboard,
  "Export requested · Report: Orders · <requester>" on the Logs page.
- Staff: no "Báo cáo" and no "Nhật ký" menu item, `/reports` → no-access page with **no** request to `/api/reports`,
  no export button on the Orders page.
- Found there and fixed: the header "Dung lượng" was cut at 84 px and the table was 16 px wider than the area next
  to a scrollbar → columns now sum to 976 px (as the Logs table), size column 100 px.

## Deviations from the task
1. **A2 "one second before `ExpiresAt`"** is a unit test of `ReportExportExpiry` at the exact boundary (−1 s, 0, +1 s).
   The API tests share one host on the real clock (a second host is impossible: Serilog's bootstrap logger), so the
   integration test moves `ExpiresAt` to +30 s (Succeeded, link) and −1 s (Expired, 409 en/vi; pending stays Pending).
2. **Download of a non-terminal export past `ExpiresAt`** answers `export_expired` too (the task: "checked before not
   ready"); tested.
3. **Existing tests changed:** two 020 tests now expect `Expired` for a finished export past `ExpiresAt` that the sweep
   has not taken (A2's rule); `X-Amz-Expires=300` of 020 is now `300|301` — the signed lifetime depends on the
   sub-second of the request and the assertion was red once in a full run; tests that used `/reports` (and the
   "Báo cáo" menu link) as the page without requests use `/no-such-page` now (auth, alerts, dashboard, logs,
   realtime, code splitting); the logs filter tests count two more event types.
4. **`export_too_large` / `too_many_exports`** are shown above the form in the client's words (`errors.*`, the
   convention of `translateApiError`: `detail` is never shown), so without the server's numbers. Same for the download
   errors ("the localised server message" of B5 = the message for the server's code). Say so if `detail` should be
   shown for these codes.
5. **Both request forms stay mounted** (the inactive one hidden): what was entered survives a look at the other.
6. **A5:** besides "nothing before `expiresAt`", a thumbnail does not ask again until an image has loaded — a client
   clock far ahead of the server would otherwise make every fresh link look expired (a loop while the storage is down).
   The dialog preview asks for its one product (`GET /api/products/{id}`).
7. **Failed row:** error label plus an icon button to the task detail (not a text link) — 140 px action column.
8. **Sweep failure injection (A1):** done with the API tests' `CommitFailureInjection` registered in the Workers test
   host through `configureServices` — the fixture allows it.
9. **Smoke 020 on development ran twice:** on the committed state before any change (A0), and again with A1–A4
   (46/46 both; step 17 `True / True` in the second).

## Blocked / questions for Cowork
1. **The browser saving the file was not exercised.** A download in the Browser pane needs the user's explicit OK,
   so the click was intercepted. What makes the browser save and stay — `Content-Disposition: attachment;
   filename="…"` from the storage — is asserted by smoke 020 step 30 and smoke 021 step 16, and the anchor has no
   `target`. The stop condition ("would navigate away or open inline") is therefore not hit by evidence, but the
   literal check is open: the user clicks *Download* on `/reports` once, or allows me to.
2. **`ComingSoon`, `plannedTask` and the `comingSoon` texts are unused** now that every menu page is built. Remove?
3. **`MAX_EXPORT_RANGE_DAYS`** is a second client copy of a server limit (after `MAX_IMAGE_BYTES`). Fine as decided;
   if a third appears, a small `GET /api/config` for client limits would be cheaper than three constants (026?).
4. **Time zone label:** the browser reports its IANA zone as configured in the OS — on this machine `Asia/Bangkok`,
   not `Asia/Ho_Chi_Minh` (same offset). Shown as it is; the API accepted it.
5. No mutation run was made for 021 (not asked for). The decisions of A1/A2 are covered by 22 new backend tests.

## Out of scope / follow-ups
- The slow run is sensitive to load: three full runs were needed for one green one. Role-and-name queries over large
  tables inside `waitFor` are the pattern to avoid in new tests (README "Frontend" could say so — not added).
- `README` section 4 still describes migrations as the user's step; the workflow change of 2026-10-05 is only in
  `docs/tasks/README.md`.

## Development steps (applied)
All on development, in this order:
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` → `.env` unchanged ("the three entries
   are there"), user-secrets of `Omnichannel.Api` and `Omnichannel.Workers` set; exit 0.
2. `docker compose -f docker/docker-compose.yml --env-file docker/.env up -d objectstorage` → container recreated
   (healthy); its arguments now carry `-s3.allowedOrigins=…,http://127.0.0.1:5173,http://127.0.0.1:4173`; volume kept.
3. `dotnet ef migrations list -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api --no-build` as the migrator
   (connection read from the user-level variable inside a script, never printed; target checked: `127.0.0.1:5432`,
   database `omnichannel`) → pending exactly `20261004074312_ProductImage`, `20261004183420_ReportExports`; both
   `Up` methods only add (4 × `AddColumn` + `AddCheckConstraint`; `CreateTable` + 2 × `CreateIndex`).
   `dotnet ef database update …` → "Applying migration '20261004074312_ProductImage'. Applying migration
   '20261004183420_ReportExports'. Done." A second `migrations list` shows none pending.
4. `scripts\smoke\task-019.ps1` → PASSED: all 50 checks. `scripts\smoke\task-020.ps1` → PASSED: all 46 checks.
5. After Part A: `task-020.ps1` again → 46/46. After Part B: `task-021.ps1` → 25/25. This task changes no schema, no
   container and no key.
No API, Workers host or preview server is left running; every smoke user is locked.

## Manual steps for the user
One optional check, because it could not be applied by Claude Code without an explicit OK: on `/reports` (Manager or
Admin), click *Download* on a succeeded export and confirm that the browser saves `orders_….xlsx` / `inventory_….xlsx`
and the page stays.

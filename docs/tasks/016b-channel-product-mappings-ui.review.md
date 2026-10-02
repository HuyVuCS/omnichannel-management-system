# Review 016b — Frontend: channel product mappings on the Channels page

Reviewer: Cowork, 2026-10-03
Verdict: **Accepted** — no P0–P1 finding in the new code. A P1 *pre-existing* defect (form dialogs) was found and fixed
by the task; the deviation is accepted. One P2 (frontend suite unstable under load) does not block and goes to task 017
Part A.

## Checked
- Report and commits `6fb1896` (A1), `4d67b5f` (A2), `253edbf` (A3), `f279fac` (Part B), `e6f9eb7` (form dialogs),
  `af72fbf` (Parts C, D), `37c5f44` (browser finding), `6ebefe9` (smoke script), `62d5eea` (report). No migration, no new
  dependency, no new endpoint.
- Read in full: the diffs of `hintTags.ts` (throttle), `OrderOutcome.tsx`, `ChannelService.cs`, `ChannelContracts.cs`;
  `useResetFormOnOpen.ts`, `SyncResultAlert.tsx`; `vite.config.ts` test settings. Skimmed: `ChannelDetailPage.tsx`, the
  `stockImport.test.tsx` test list.
- Checked by Cowork against the code (not only the report):
  - **A2:** a flush opens the window, tags arriving in it go out together when it ends, idle tags go out at once; at
    most one flush per window, also after `cancel()` (the running window is kept). Matches the decision of review 016;
  - **A3:** the decision is taken once, by the connection state when the task is first seen finished (`announced`
    guards a later change of `live`); the claim behind it was verified for review 016 (order, movements and task
    status in one `SaveChanges` → one `NOTIFY`);
  - **Part B:** `WithStockSync` is applied on every path that returns a channel (list, get, create, update); the SQL
    projection sets `false` and is private to the service, so no caller can return it unset. One map
    (`RabbitMqTopology.StockSyncQueueOf`) for sync, dashboard and this flag;
  - **sync-result alert:** `currentData` — the previous save's task status is never shown as the new one's (the
    browser finding, test fails with `data`).
- **Verification:** the report's runs — backend 0 warnings, 752/752; typecheck, lint, build clean; full frontend suite
  281 tests, **3 of 5 runs green** (two timeouts in long existing tests); smoke `task-016b.ps1` 24/25 then 25/25; real
  browser incl. a production-build pass. **The user's smoke run (2026-10-03):** all 21 API checks and the build passed,
  `npm test` failed in `stockImport.test.tsx` (the output was not pasted; whether it was a timeout or an assertion is not
  confirmed — assumed the timeout class, see finding 1). Not re-run by Cowork (no npm in the user's VM).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | **Form dialogs fixed outside the literal scope** (`useResetFormOnOpen` leaves an unmounted form alone; every dialog `Form` has `clearOnDestroy`; six existing dialogs + the new one) | **Accepted.** The defect is **P1, pre-existing since 011** and production-only: after a dialog was closed completely, opening it for record B showed record A's values, and saving wrote them onto B. Rule of review 012 applies (adopt the mechanism at the existing producers). Regression tests fail on the old code (4/4), fix verified in the production build. Note: "is the form mounted" is read from `form.getFieldsError().length > 0` — AntD behaviour, pinned by those tests; re-check on an AntD major upgrade. |
| 2 | A2 rewrote assertions of existing burst/coalescing tests | Accepted — the behaviour they pinned was changed on purpose. |
| 3 | A3 push test 3 → 2 fetches per list; two tests added | Accepted. |
| 4 | Fixtures got `stockSyncEnabled: true` | Accepted. |
| 5 | "Back" goes to `/channels` | Accepted — the list has no address state. |
| 6 | Alert names the product; small mapping table with short titles; task link in the summary; short status labels | Accepted — needed to fit eight columns in 992 px; measured identical in vi and en. |
| 7 | The mapping dialog clears the API errors of the two external-id fields before each submit | Accepted (same mechanism as `OrderEntryForm`). |
| 8 | Mapping list requested only after the channel loaded | Accepted (one 404, not two). |
| 9 | Smoke keeps npm output in a temp log; `stockSyncEnabled` compared as text | Accepted. |

## Questions of the report
1. **Flaky frontend suite.** Two runs of the report and the user's run failed with whole-test timeouts in long
   existing tests (`processing.test.tsx`, `stockImport.test.tsx`); each passes alone. Cause: memory-bound workers
   (one jsdom per file; ~2.2 GB free with Docker) plus, in the smoke scripts, an API and a Workers host started by the
   script **running next to the suite**. Decision (017 Part A1): (a) smoke scripts run `npm run build` / `npm test`
   only **after** stopping the hosts they started; (b) `maxWorkers: 3` if a measurement shows it no slower in total
   than 4 under the same load; (c) the slowest tests (> 5 s alone: list them from a `--reporter=verbose` run) are split
   so no single test does a whole page journey; (d) `testTimeout` stays 20 s — raising it hides the next regression.
   Acceptance: 5 consecutive full runs green while PostgreSQL and RabbitMQ run.
2. **StrictMode hid a production defect.** Decision: the real-browser check of every frontend task runs on the
   **production build** (`npm run build` + `vite preview`, which uses the same `/api` proxy); the dev server stays for
   iterating. Tests keep rendering without StrictMode (StrictMode would have hidden this defect from them as well).
3. **Other `data` vs `currentData` places.** Lists keep showing the previous page while the next loads (intended, with a
   loading overlay). Detail pages bound to a route parameter (`OrderDetailPage`, `ProcessingTaskPage`,
   `ChannelDetailPage`) could show the previous record for a moment if the parameter changes while mounted — today no
   link goes from one record to another of the same route. → 018 Part A: audit, `currentData` (or remount by key) on
   those three pages, one test each.
4. **Stale API errors on optional fields of `CustomerFormModal`.** → 018 Part A: check in a test; if the error stays
   after the next submit, clear it as deviation 7 does.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | **Frontend suite unstable under load** (question 1): a red `npm test` in 2 of 6 known runs makes every report and smoke result ambiguous. | 017 Part A1. |
| P3 | Questions 2–4 above. | Q2: rule recorded in the implementation log now; Q3, Q4: 018 Part A. |
| P3 | `Assert-Step … $true <value>` in older smoke scripts passes for any non-boolean value (PowerShell comparison). | 017 Part A2: `Assert-Step` compares type and value (or the scripts compare as text, as 016b does); existing scripts keep passing on the development environment. |
| P3 | Products table scrolls horizontally at 1280 px (existing). | 018 Part A (same treatment as the orders table in 016 A4). |
| P3 | A `SIM-PERM-*` message stays in `stock.sync.website.dlq` after every browser check. | Accepted — expected for a permanent failure; DLQ handling has no UI. Purge only by hand. |

## Assessment
Part A applies the three decisions of review 016 exactly, with tests that fail on the old behaviour. The mapping UI keeps
every rule on the server (which channel syncs, why no sync started, the "one external id" rule) and shows the sync
task live through the existing hint path — the 1.8 s and 2.9 s in the browser are the end-to-end figure the throttle was
meant to give. The most valuable result is outside the scope: a silent data-overwriting defect in every edit dialog,
present since task 011, found by running the production build.

## Manual check
The user's smoke run (2026-10-03): API part passed, `npm test` failed in `stockImport.test.tsx` (see above). The
browser manual check (map a smoke product on Website, watch the task finish) is still the user's.

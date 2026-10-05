# Review 021 — Reports & Excel export page; labels for exports; Part A (review 020, review 019)

Reviewer: Cowork, 2026-10-05
Verdict: **Accepted** — no P0–P2 finding. Three P3 follow-ups go into **022 Part A** (one frontend clean-up, two
README items); one observation is accepted as a known limitation. The four questions of the report are answered below.

## Checked
- Report and commits `b492b86` (A1–A4), `076f518` (A5), `896d27c` (Part B), `68099d7` (report); Cowork's documents in
  `3a94587`. No migration, no new package (the A3 pin changes no resolved version: 286 lines, 0 differences).
- Read in full: `ReportExportExpiry` (`ReportContracts.cs`), the diffs of `ReportExportCleanup.MarkDeletedAsync`,
  `ProcessingTaskService.RetryAsync` / `EnsureExportNotExpiredAsync`, `ReportExportService` (list, detail, download),
  `Directory.Packages.props`, smoke 020 step 17; A5: `imageLinks.linkHasExpired`, `ProductThumbnail`,
  `ProductsPage.refreshExpiredLinks`, `ProductImageField.refreshExpiredLink`; Part B: `paths.ts`, `reportsApi.ts`,
  `exportRequest.ts`, `download.ts`, `ReportsPage.tsx`, `OrdersExportForm.tsx`, `InventoryExportForm.tsx`,
  `ExportsTable.tsx`, `ExportFilterSummary.tsx`, `ExportStatusTag.tsx`, and the diffs of `navigation.tsx`,
  `router.tsx`, `OrdersPage.tsx`, `InventoryPage.tsx`, `hintTags.ts`, `baseApi.ts`, `processingApi.ts`,
  `systemEventsApi.ts`, `eventSummary.ts`, `format.ts`.
- Checked by Cowork against the code:
  - **A1, one transaction.** `MarkDeletedAsync` opens a transaction, marks `DeletedAt` set-based, resolves
    `ProcessingFailed` of every task of the batch through `AlertStore.ResolveAsync` (its own `pg_notify` rides the same
    transaction) and commits. Passing keys of rows another instance marked is harmless (no unresolved row → no-op).
  - **A1, refused retry.** The expiry check runs after the row lock and the `Failed` check and before any outbox row is
    built; nothing is written on 409. The read of the export is unlocked — the remaining race (retry accepted just
    before `ExpiresAt`, the worker then fails it as expired) opens an alert that the next sweep resolves, because the
    export is terminal, past `ExpiresAt` and not yet marked. Self-healing; no action.
  - **A2.** One rule (`ReportExportExpiry`) for list, detail and download; non-terminal exports keep their task
    status; download checks expiry before "not ready" (deviation 2 — accepted, an expired export has nothing to wait for).
  - **A5.** One refetch per burst (`refreshingLinks` ref until the refetch settles); a thumbnail asks only after
    `expiresAt` and only once until an image has loaded, so a broken storage or a skewed client clock cannot loop
    (deviation 6 — a good addition). Both places that render image links (table, dialog preview) are covered.
  - **Download link as a credential.** Fetched by a mutation, followed through a hidden anchor (`rel="noreferrer"`,
    no `target`), removed from the store with the request's own `reset()` in `finally`, never put in a message,
    the address or the console; errors other than network/5xx refetch the list.
  - **Live status.** `ReportExport` is in the resync set; only `processing.task.changed` with
    `taskType: ReportExport` adds export tags (other task types leave the exports alone; `all` invalidates the type).
    Polling only while not live **and** an unfinished export is on the page, paused while the tab is hidden; the
    browser check measured the push path (Pending → Succeeded in 2.6 s, no poll).
  - **Prefill.** Location state is re-validated field by field (`oneOf`, `isGuid`, strings), read once per mount;
    nothing is requested before submit; the address of `/reports` stays the table's.
  - **Roles.** Menu item and page by `REPORTS_ROLES`; Staff gets the no-access page without a request to
    `/api/reports` and no shortcut button; the API enforces the same rule.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1300/1300; frontend typecheck and lint clean,
  5 of 5 consecutive full `npm test` green (452 tests), one full `npm run test:slow` green (third run, see finding 1);
  first-load set 970.14 → 971.00 kB raw, `ReportsPage` a lazy chunk (12.8 kB); smoke 019 50/50, 020 46/46 (twice), 021
  25/25 on development; production-build browser check (vi + en, Manager and Staff). Development steps of 019/020
  applied by Claude Code (A0): both migrations additive, listed before update, applied as the migrator.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | "One second before `ExpiresAt`" as a unit test at −1 s / 0 / +1 s; the API test uses +30 s / −1 s | Accepted — the boundary is where the rule lives; one API host on the real clock. |
| 2 | Download of a non-terminal export past `ExpiresAt` → `export_expired` | Accepted. |
| 3 | Existing tests changed (`Expired` expectations, `X-Amz-Expires` 300\|301, `/no-such-page` as the request-free page) | Accepted — each follows from A2 or from `/reports` making requests now. |
| 4 | `export_too_large` / `too_many_exports` in the client's words, without the server's numbers | Accepted — the convention since 010 (`detail` is never shown); the texts name the limit in words. |
| 5 | Both request forms stay mounted | Accepted. |
| 6 | A thumbnail asks again only after an image has loaded | Accepted — closes a loop the task did not foresee. |
| 7 | Failed row: label + icon button to the task detail | Accepted. |
| 8 | Sweep failure injection through `configureServices` | Accepted. |
| 9 | Smoke 020 run twice on development | Accepted. |

## Questions of the report
1. **The browser actually saving the file.** **Accepted on the evidence; the click stays an optional check for the
   user.** What decides "save and stay" is the stored `Content-Disposition: attachment` (smoke 020 step 30, smoke 021
   step 16) and an anchor without `target`; both are proven. The stop condition of the task is not hit. The user may
   click *Download* once on `/reports` (handoff, "Pending for the user"); if the file opens inline instead, that is a
   provider/browser finding for 025, not a defect of 021.
2. **`ComingSoon`, `plannedTask`, the `comingSoon` texts.** **Remove them** (022 A1): dead code and two dead
   translation blocks; a future page is added with its route, not as a placeholder.
3. **`MAX_EXPORT_RANGE_DAYS`, a second client copy of a server limit.** **Keep the constants.** Two copies, each
   commented and named in the README next to the server setting, are cheaper than a new endpoint with its own
   authorization, caching and tests. Recorded in the log with the trigger: a third copy, or a limit that differs per
   deployment, brings a small read-only `GET /api/config/client-limits` (then in 026).
4. **`Asia/Bangkok` instead of `Asia/Ho_Chi_Minh`.** **Correct as it is.** The browser reports the zone the OS is set
   to; both are valid IANA ids with the same offset and no daylight saving, the API accepted it, and the file's times
   are right. Showing the zone the file will use (not a "preferred" name) is the point of the label.
5. (No mutation run.) Fine — not asked for; A1/A2 are covered by 22 new backend tests, and Part B is UI.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **Slow-run rule only partly met.** Log "Task 019 result": every test red in *any* slow run is fixed or shown to be an application defect. Run 2 had seven red tests; one (A5) was fixed, the six others (`stockImport` ×2, `dashboard` ×1, `productImages` ×3) were attributed to load (Browser pane open, 1.9 GB free) and not investigated. Run 2 was not a representative machine state, so the attribution is plausible — but the report itself names the pattern that makes tests load-sensitive (role-and-name queries over large tables inside `waitFor`), and `productImages`' first test failing "could not find the list in time" may be that pattern. | Accepted for 021 (run 3 and five normal runs green). **022 A3:** README "Frontend" names the pattern to avoid; slow runs are made with the Browser pane closed and the machine otherwise idle, and a red test in such a run is never attributed to load without a second run of that file alone. |
| P3 | **Unused placeholder code** (`ComingSoon`, `NavigationItem.plannedTask`, `comingSoon.*` texts in vi/en) (question 2). | **022 A1.** |
| P3 | **README section 4 still says the user applies migrations**; the workflow of 2026-10-05 (Claude Code applies additive migrations as the migrator) is only in `docs/tasks/README.md` (report, follow-ups). | **022 A2:** README §4 states both: the command, and that Claude Code runs it as the migrator under the guardrails of `docs/tasks/README.md`. |
| — | **Filter summary dates in the viewer's zone.** `ExportFilterSummary` formats the stored `from`/`to` instants in the browser's zone, not in the export's `timeZone`. Only the requester sees an export, normally in the same zone; a requester who later opens the page in another zone sees the days shifted by one at most. | Accepted as a known limitation (no dayjs time-zone plugin for this alone). Revisit if exports become shareable. |

## Assessment
Part A closes the end-of-life gaps of 020 exactly where the review placed them: one expiry rule shared by the API and
the worker, the alert ended in the transaction of its cause, and a retry that could only fail refused before it writes
anything. The Reports page follows the established patterns (address-backed list, `currentData`-safe forms, role
constants next to paths, realtime tags derived from hint fields) and treats the download link as the credential it is.
The A5 work is small and careful: it fails closed (no request when the cause is not expiry) and cannot loop. The one
weak spot is process, not code: the slow run needed three attempts, and six reds were explained rather than
investigated — acceptable under the circumstances, tightened for the next frontend change.

## Manual check
Optional, for the user: on `/reports` (Manager or Admin) click *Download* on a succeeded export and confirm that the
browser saves the `.xlsx` under its name and the page stays (question 1). No development step is open.

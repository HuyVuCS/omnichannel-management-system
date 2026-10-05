# Review 020 — Excel export in the background (ClosedXML + object storage); Part A (review 019)

Reviewer: Cowork, 2026-10-05
Verdict: **Accepted** — no P0–P2 finding. Five P3 follow-ups, all in **021 Part A** (four backend, one script); two
items go to 025.

## Checked
- Report and commits `4f20d69` (Part A), `141c33d` (Parts B and C), `4df5baa` (smoke, key helper, README, `.http`),
  `c596353` (report); Cowork's documents in `a9c53f3`. 81 files outside `docs/`, +8381 / −143. One migration
  (`20261004183420_ReportExports`), one new package (`ClosedXML` 0.105.1, pinned through Central Package Management;
  transitive `SixLabors.Fonts` resolved at 1.0.0 from the range `[1.0.0, 3.0.0)`).
- Read in full: `ReportExportService.cs`, `ReportExportProcessingService.cs`, `ReportExportCleanup.cs`,
  `ReportExportMessageHandler.cs`, `ReportsController.cs`, `ReportSheet.cs`, `ProcessingTaskService.RetryAsync`
  (unchanged, read for the retry path of an export), `AlertStore` (open / resolve statements); diffs of
  `S3ObjectStorage.cs` (`PutAsync`), the migration, `ReportContracts.cs` (status enum, file name, time zones),
  `Directory.Packages.props`, `scripts/smoke/task-020.ps1`; README "Reports and Excel export" (structure).
- Checked by Cowork against the code:
  - **One transaction per request.** Task, export row, outbox event (payload `{ taskId }` only) and audit event are
    added to one context and saved by one `SaveChanges`; the limit checks and the `COUNT` run before it and write
    nothing. The outbox id is generated first and recorded as the task's message id (same pattern as order intake).
  - **No external call in a transaction.** `GenerateAsync` opens `REPEATABLE READ` + `SET TRANSACTION READ ONLY`,
    streams orders, item lines and the Info lookups, saves the workbook into a `MemoryStream` and commits; the upload
    runs after that, `CompleteAsync` afterwards in its own transaction (task row locked, export completed, attempt and
    task `Succeeded`, `ProcessingFailed` resolved). The sweep deletes objects outside any transaction and marks only
    what it deleted.
  - **Idempotency.** Terminal task → ACK before an attempt; `StartAttemptAsync` returns null for a task completed
    meanwhile; `CompleteAsync` on a terminal task closes the attempt only. The key `exports/{taskId}.xlsx` makes a
    redelivery after the upload overwrite the same object, and lets the sweep delete a failed export's file without a
    stored key.
  - **Sweep vs. worker vs. manual retry.** The sweep takes only terminal exports past `ExpiresAt`; the worker fails any
    export past `ExpiresAt` before building (`IsExpired` in `GetTaskAsync`). So a manual retry racing the sweep can
    never upload a new file after the old one was deleted. (One gap: the alert of such a retry — finding 2.)
  - **Access.** Every read goes through `OwnExports()` (`CreatedBy` = caller); another user's export, an Admin's view
    of a Manager's export and an unknown id are the same 404. The download link is presigned locally, never logged;
    the object carries its own `Content-Disposition: attachment` (ASCII file name, no header injection possible).
  - **Formula injection.** Text reaches a cell only through `Value` (`ReportSheet.SetText`); no `FormulaA1` anywhere;
    the leading-apostrophe doubling is correct and tested.
  - **Status mapping.** `ReportExportStatus` repeats the five `ProcessingTaskStatus` names plus `Expired`; `errorCode`
    only for `Failed`, never the message.
  - **Memory bound.** The limit is enforced while streaming (`EnsureBelowLimit` before each row), so a grown data set
    stops at `MaxRows + 1` rows instead of exhausting the process.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1278/1278 (860 unit); 22 of 22 mutants killed;
  smoke 46/46 on a throwaway environment and the expected 2-of-21 failure with the emulator stopped; measurement at
  `MaxRows`: 255 MiB (orders) / 336 MiB (inventory) peak working set, 6.1 s / 5.5 s, 4.1 / 5.0 MiB files; log scan of
  API and Workers: 0 hits; IANA zones and a font-less workbook identical on Windows and in `aspnet:10.0`. No frontend
  file changed. Migration not applied to development (user's manual step).
- **Doc deviations 22–25 confirmed**, 23 with the addition `report_export.message_rejected` (system event type for
  poison messages of the export queue; allowlist `messageId`, `reasonCode`).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `Reports` options class and rules in Application, validator adapter and binding in Infrastructure; chosen ranges | Accepted — Application stays free of the options package. |
| 2 | Second event type `report_export.message_rejected` | Accepted (the pattern of the other two queues). Label in 021. |
| 3 | `RequestRejectedException` (400 with an error code that belongs to no field) | Accepted. The frontend shows it as a form-level message (021). |
| 4 | Four localised messages instead of three | Accepted. |
| 5 | Worker code `export_parameters_invalid` (permanent) | Accepted — it is a deployment signal (the Workers host lacks a zone the API accepted); see question 1. |
| 6 | First sweep round after `min(CleanupInterval, 30 s)`; set-based `DeletedAt` | Accepted. 021 A1 puts the mark and the alert resolution into one transaction. |
| 7 | Leading apostrophe doubled | Accepted — correct and tested. |
| 8 | Localised sheet names; unset filters "(all)", unset search empty | Accepted. |
| 9 | `rowCount` = orders + item lines | Accepted (the unit of `MaxRows`; the Info sheet has both). |
| 10 | Time zone ids "as the host finds them", ≤ 64 characters, trimmed | Accepted; see question 6. |
| 11 | Key helper: Workers user-secrets + console input encoding fix | Accepted; the byte-order-mark finding is in the handoff gotchas. |
| 12 | Limit tests change live options; Workers end-to-end hosts on the API test database | Accepted (as 019 deviation 7, 008). |
| 13 | Throwaway emulator on 58333 through a compose override | Accepted. |
| 14 | Measurement on a Debug build, one data shape | Accepted. A Release build allocates no more; the numbers are an upper estimate for the data shape. |

## Questions of the report
1. **Alerts of exports that can never succeed.** **Keep opening them; the sweep resolves them** (021 A1).
   Every final failure opening `ProcessingFailed` is the contract of 017 — one rule for operators, and
   `export_parameters_invalid` is exactly what an operator must see (the hosts disagree on time zones), while
   `export_too_large` / `export_expired` tell the operator the limits or the Workers host need attention. But an alert
   with nothing left to act on must end: when the sweep expires an export, it resolves that task's `ProcessingFailed`
   alert **in the same transaction** as setting `DeletedAt` (`AlertStore` is only written inside the transaction of
   the cause). Since the sweep takes every terminal export past `ExpiresAt`, each such alert ends at most one sweep
   interval after the export's retention. Together with it, a **manual retry of an expired export is refused**
   (`POST /api/processing/tasks/{id}/retry` → 409 `export_expired`, nothing written): today the retry is queued, fails
   again with `export_expired` and opens a **new** alert (the unique index covers unresolved alerts only) that no sweep
   will ever resolve, because the export already has `DeletedAt` (finding 2). Opening no alert at all was rejected: it
   would hide the deployment signal and make export tasks the only task type that fails silently.
2. **Past `expiresAt`, not swept yet.** **Treat `expiresAt <= now` as expired in the API** (021 A2): the detail and the
   list answer `Expired` for a *terminal* export past its `expiresAt` (as well as for `DeletedAt` set), and the download
   answers 409 `export_expired` for any export past it. Reason: the retention is a promise about personal data; the
   worker already uses `ExpiresAt <= now` and the API should agree with it, and the cost is one condition. A link
   handed out shortly before `expiresAt` stays valid for its lifetime (5 min) — accepted, it was issued in time.
   Non-terminal exports keep their task status (the worker fails them as expired).
3. **`SixLabors.Fonts`.** **Pin it now** (021 A3), not in 026: NuGet resolves the lowest version of the range, so 1.0.0
   does not float by itself, but any package that asks for ≥ 2.0 — or a casual "update all packages" — would move it
   silently to the Six Labors Split License (2.x: Apache 2.0 only below a revenue threshold or for open source, a paid
   licence otherwise). A central pin (transitive pinning) turns that into a restore error instead. Verified on NuGet: the 2.x
   packages declare the Six Labors Split License.
4. **Upload timeout.** **Accept for now → 025.** At `MaxRows` a file is ~5 MiB, i.e. 0.5 MiB/s within 10 s. The
   production provider check in 025 measures one `MaxRows` upload from the Workers host; if it needs more than half
   the timeout, 025 adds a separate `ObjectStorage:UploadTimeout` (Workers host) rather than raising the timeout of
   every image HEAD / GET. Recorded in the roadmap row of 025.
5. **A user blocked while the Workers host is down.** **Accept.** That is what the limit is for (one user cannot fill
   the queue while nothing drains it); the exports are visible in the user's list as `Pending`, and the 409 message
   names the limit. The Reports page (021) shows the 409 as a form-level message next to the list. No cancel endpoint
   (out of scope; the user waits or an operator restarts the Workers host). Note: a manual retry of a failed export
   is not counted against the limit — accepted (Manager/Admin action on a failed task, not a new export).
6. **Windows time zone names.** **Leave as it is.** The contract (README, request DTO) is an IANA id, and the only
   client (021) always sends `Intl.DateTimeFormat().resolvedOptions().timeZone`, which is IANA. Accepting what the host
   resolves is harmless while API and Workers run the same image (025); the mismatch case is already a clean permanent
   failure (`export_parameters_invalid`) with an alert. Revisit if API and Workers ever run on different platforms.
7. **Dashboard summary `processing` has `ReportExport`.** **Correct** — it lists every task type, and a failed export
   is a failed task. The label comes with the other `ReportExport` labels in 021 (`processing.taskTypes.ReportExport`
   serves the dashboard, the Sync & Processing page and the Logs page).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **`ProcessingFailed` alerts of exports that can never succeed stay open forever** (report question 1). | **021 A1**: the sweep resolves the alert of each export it expires, in the transaction that sets `DeletedAt`. |
| P3 | **A manual retry of an expired export is accepted and re-opens an alert no sweep resolves.** `RetryAsync` is generic: it republishes the message; the worker fails with `export_expired`; `RecordFinalFailureAsync` opens a new `ProcessingFailed` (the dedup index excludes resolved rows); the sweep skips the export (`DeletedAt` set). | **021 A1**: `RetryAsync` answers 409 `export_expired` (localised) for a `ReportExport` task whose export is deleted or past `ExpiresAt`; nothing written. |
| P3 | **An export past `expiresAt` stays `Succeeded` and downloadable until the next sweep round** (report question 2). | **021 A2.** |
| P3 | **`SixLabors.Fonts` is not pinned**; a transitive bump would change its licence silently (report question 3). | **021 A3.** |
| P3 | **Smoke step 17 reads inverted:** "the object storage answers" with expected `False` / actual `False` on success (the value is `StorageDown`). | **021 A4**: flip the value (`-not $waited.StorageDown`, expected `True`) or rename the step to what it measures. |

## Assessment
The task delivers the plan without bending a rule: the request is one atomic write with nothing heavy in the API, the
worker never holds a transaction across the storage, the snapshot is real (and tested with an interceptor that commits
between the two queries), the file is attack-safe as text and correct in time zones across a daylight-saving change,
and exports are invisible to everybody but their requester. The deterministic key is a small design choice that pays
twice (redelivery and cleanup without orphans). The stop conditions were each answered with evidence, the memory
measurement gives the limit a basis, and the mutation run shows the tests guard the decisions that matter (isolation
level, ownership, limits, ordering, formula text, time zones). The findings are all about the *end* of an export's life
— alert, retry and status after the retention — which is where the generic machinery meets the one new concept
(expiry); they are small and go into 021.

## Manual check
None for the user. The development steps of the report (key helper, emulator CORS, migration `ReportExports`, smoke on
development) are applied by Claude Code in 021 A0 (user decision 2026-10-05, `docs/tasks/README.md` "Development
steps"); only the one-time `OMNICHANNEL_MIGRATOR_CONNECTION` setup is the user's.

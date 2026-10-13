# Report 026c — Follow-ups of 026b; object storage hygiene; one required-field text; accessibility pass

Status: Partially done — Parts A to D are implemented, verified and applied to development. **Part G (the release to
production) has not started: the task makes it wait for the user's go-ahead**, and the count of what the first
clean-up run would delete on production comes before that (see "Manual steps for the user").
Commit: `2f78c4e` (A1), `e73cd24` (A2), `e9a20d5` + `060ee4a` (B backend), `c2077c8` + `78958e9` (frontend: event label,
C1, C2, A3), `8cb22b6` (smoke scripts, README, runbook), CI_COMMIT_PLACEHOLDER; this report in a commit of its own.

## Summary
- **A:** the proxy's body limit is 1 MiB (8 x the API's, relation tested), so every oversize body up to 1 MiB gets the
  API's readable 413 — five of five smoke runs through the proxy; the task list finds the last attempt by an index
  lookup per page row and its page through a new index `(CreatedAt, TaskId)` (unfiltered page 40 ms -> 0.4 ms at the
  026b seed; migration `TaskListOrderIndex`); the red CI runs were one frontend test that asserted three field errors
  without waiting for them (fixed, plus two more of the same kind); runbook "Incidents" and section 4.
- **B:** uploads go to `uploads/products/{productId}/{uuid}`; a confirmation copies the upload inside the storage to a
  final key that is new for that confirmation, checks **the copy**, and the product refers to the copy — the upload
  link can no longer change a confirmed image. The upload's size is signed into the link (SeaweedFS enforces it,
  directly and through the proxy). The Workers delete uploads and unreferenced image objects older than a day
  (`ImageCleanup`, advisory lock, event `product_image.orphans_deleted`), with a dry run for operators.
- **C:** one required text for every form ("{label} là bắt buộc" / "{label} is required"); axe-core runs on what
  every test ends on (serious / critical fail the test); a browser pass with axe in a real browser and the keyboard:
  theme colours raised to AA contrast, labels, a tab stop for tables that scroll sideways.
- Backend 1935 / 1935 in one local run, 14 of 14 mutants killed; frontend 526 / 526 twice and once slow; throwaway
  stack `omni026c`: smoke 025 53 / 53, 026 five of five, 026b 16 / 16, 026c 41 / 41 with the clean-up drill.

## Files changed
Backend
- `docker/proxy/Caddyfile` — `max_size 1024KiB` and why.
- `Omnichannel.Api/Middleware/RequestBodyLimit.cs` — `ProxyLimitFactor = 8`.
- `Omnichannel.Application/Common/Abstractions/IObjectStorage.cs` — `PresignPut(..., contentLength, ...)`, `CopyAsync`,
  `ListAsync`, `ListedObject`, `StoredObjectPage`.
- `Omnichannel.Application/Products/ProductImageKey.cs` (new, moved out of `ProductImages.cs`) — staging and final
  keys, `NewStagingFor`, `NewFinalFor`, `TryParseStaging`, `TryParseFinal`, `IsFinalOf`; `BelongsTo` = staging only.
- `Omnichannel.Application/Products/ProductImages.cs` — `ProductImageRules.ConfirmationDeadline` (30 s).
- `Omnichannel.Application/Products/ProductImageCleanupContracts.cs` (new) — `ImageCleanupOptions`, its rules,
  `ProductImageEvents`, `ImageCleanupIds`, `ImageCleanupResult`, `IProductImageCleanup`.
- `Omnichannel.Application/Products/IProductImageService.cs`, `SystemEvents/SystemEventDetails.cs` (allowlist entry).
- `Omnichannel.Infrastructure/S3/S3ObjectStorage.cs` — signed `Content-Length`, copy (`REPLACE`, explicit content
  type), listing in pages.
- `Omnichannel.Infrastructure/Persistence/Products/ProductImageService.cs` — presign for a staging key; confirm =
  look at the upload, copy, check the copy, deadline, transaction, deletes.
- `Omnichannel.Infrastructure/Persistence/Products/ProductImageCleanup.cs` (new) — the sweep, with the interleaving
  argument in its class comment.
- `Omnichannel.Infrastructure/Persistence/Processing/ProcessingTaskService.cs` — last attempt per page row.
- `Omnichannel.Infrastructure/Persistence/Dashboard/DashboardService.cs` — `lastSucceededAt` per channel in the
  channels statement; the stock-sync counts read only the tasks they count.
- `Omnichannel.Infrastructure/Persistence/Configurations/ProcessingTaskConfiguration.cs`, migration
  `20261013132359_TaskListOrderIndex` (+ designer, snapshot) — index `IX_ProcessingTasks_CreatedAt_TaskId`.
- `Omnichannel.Infrastructure/DependencyInjection.cs`, `Omnichannel.Workers/WorkerHostSetup.cs`,
  `Omnichannel.Workers/ImageCleanupWorker/ImageCleanupService.cs` (new: service, options validator, dry run),
  `Omnichannel.Workers/Program.cs` (`--image-cleanup-dry-run=true`).
- Tests: `ProductImageHygieneTests.cs` (new, 12), `Workers/ImageCleanupServiceTests.cs` (new, 5),
  `Workers/ImageCleanupOptionsTests.cs` (new, 16), `ProductImageUnitTests.cs` (key tests rewritten),
  `ProductImageEndpointsTests.cs` (staging keys, signed length, link after confirmation, concurrent confirmations of
  one upload), `ProcessingTaskEndpointsTests.cs`, `Workers/QueryShapeTests.cs`, `SecurityHardeningUnitTests.cs`,
  `SystemEventEndpointsTests.cs`, `SystemEventDetailsTests.cs`, `StoragePublicOriginTests.cs`, two storage test
  doubles, `WorkersFixture.cs` (the clean-up is off in worker test hosts unless a test switches it on).

Frontend
- `src/app/providers.tsx` — `validateMessages.required`, theme colours; `src/shared/components/layout.ts` —
  `THEME_COLORS`, `PRIMARY_COLOR` `#0958d9`; `src/shared/i18n/{vi,en}.json` — `common.requiredField`, the event's
  label and summary.
- `src/test/accessibility.ts` (new), `src/test/setup.ts` — axe-core after every test; `package.json`,
  `package-lock.json` — `axe-core` 4.13.0 (dev dependency, exact).
- `src/shared/hooks/useKeyboardScrollableTable.ts` (new), `features/processing/components/AttemptsTable.tsx`,
  `features/orders/OrderDetailPage.tsx`, `features/channels/components/ChannelProductFormModal.tsx`,
  `features/reports/components/OrdersExportForm.tsx`, `features/dashboard/DashboardPage.tsx` — the local fixes of C2.
- `features/logs/eventSummary.ts`, `systemEventsApi.ts`, `src/test/systemEvents.ts` — the clean-up event.
- Tests: `orderEntry.test.tsx`, `stockImport.test.tsx`, `channelDetail.test.tsx` (A3 and the new required text),
  `logs.test.tsx`, `customers.test.tsx` (dialog by keyboard).

Scripts and documents
- `scripts/smoke/task-026c.ps1` (new), `_hardening.ps1`, `task-026b.ps1`, `task-019.ps1`.
- `README.md`, `docs/runbook.md` (sections 4, 11, 12, 15 and a new section 16).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend, 1935 tests; 1872 before) | **1935 passed, 0 failed in one local run** (4 m 21 s) on the final backend code. The run before it: 1932 passed, 2 failed — one real (`SystemEventDetailsTests` lists the event classes: the new class was missing, fixed) and one `Npgsql ... The operation has timed out` in a reconciliation test (the kind of 026b q4; it did not come back) |
| Mutants (14, each: build, targeted tests, restore) | 14 killed, 0 survived — list below |
| `npm run typecheck`, `npm run lint` | clean |
| `npm test` (frontend, 526 tests; 524 before) | 526 / 526 **twice in a row** (332 s, 321 s) on the final code. Before the last fixes: 525 / 525, then 524 / 525 — the one failure is the third flaky test of A3 (fixed) |
| `npm run test:slow` | 526 / 526 (345 s) |
| `npm run build`, `npm run measure` | built; first-load set 970.74 kB raw / 318.39 kB gzip, main chunk 171.00 / 55.25 kB; no file of `dist/assets` contains axe-core |
| `omni026c` (throwaway production stack, own tag / ports / subnet, two Workers): build, migrate, up | exit 0; both index migrations applied by the `migrate` service; 7 containers healthy after 127 s |
| `omni026c`: `task-025.ps1` | PASSED: all 53 checks |
| `omni026c`: `task-026.ps1` x 5 | PASSED 11 / 11 **five of five** (one run waited 61 s for the login limit, see deviation 12) |
| `omni026c`: `task-025b.ps1` x 5 | 22 of 28 in each of the five runs; the same six fail every time and cannot pass on a throwaway stack (steps 04, 06, 13–15: headers and 404s of the static host, served here by `vite preview`; step 10: TLS). **Every step of the hardening block, the former race included, passed in all five** |
| `omni026c`: `task-026b.ps1` | PASSED: all 16 checks (without the stale drill, whose code is unchanged); steps 02–04 now assert the API's readable 413 through the proxy, announced and chunked |
| `omni026c`: `task-026c.ps1` with `SMOKE_IMAGE_SWEEP=1` | PASSED: all 41 checks |
| `omni026c`: `compose run --rm --no-deps workers --image-cleanup-dry-run=true` | one clean-up, nothing deleted: "1 objects listed; a run now would delete 0 uploads and 0 unreferenced images older than 00:02:00; 1 old images in use would be kept" |
| `omni026c` removed | `down -v` of that project, its three images by tag, its env file: 0 containers, 0 volumes, 0 images left; production untouched (its containers "Up 3 hours" before and after) |
| Development: `task-026c.ps1` | PASSED: all 28 checks (24 of the script, clean-up, `npm run build`, `npm test`) |
| Development: `task-019.ps1` | SMOKE_019_PLACEHOLDER |
| CI (GitHub Actions) | CI_RESULT_PLACEHOLDER |

### A1 — the 413 through the proxy
`_hardening.ps1` (used by `task-026.ps1` and `task-025b.ps1`) asserts for a body of 128 KiB + 1: status 413 **with the
API's headers**, the problem document `request_body_too_large` and the correlation id of the `X-Correlation-Id`
header — with a Content-Length and without one — and for 1 MiB + 1 only the status. Five runs against `omni026c`: all
of these steps green five times. The first series of five had runs 4 and 5 red with **429**, not with a bare 413: the
checks post to the login endpoint, the only one that reads a body without an account, and three of its requests per
run count against the limit of 10 a minute. The helper now waits the minute out (at most twice per request).

### A2 — task list and summary at the seed of 026b
Throwaway `postgres:17` with `auto_explain`, migrated, `scripts/perf/seed-task-026b.sql` (24,000 tasks, 24,720
attempts); the statements the API sends for six list requests and the summary, three rounds each after a warm-up;
the JSON of every list request compared byte by byte before / after (identical; the summary differs only in the
fields that move with "now").

| Statement (page query unless noted) | Before | Lookup per row | + index `(CreatedAt, TaskId)` |
| --- | --- | --- | --- |
| unfiltered, page of 20 | 40.4 / 39.6 / 43.9 ms | 9.9 / 8.7 / 11.4 ms | 0.29 / 0.40 / 0.41 ms |
| unfiltered, page of 50 | 34.1 / 36.4 / 33.4 ms | 8.7 / 9.6 / 14.9 ms | 0.86 / 0.66 / 0.61 ms |
| `status=Failed` | 25.1 / 27.7 / 28.0 ms | 1.3 / 1.2 / 1.7 ms | 3.9 / 1.2 / 0.9 ms |
| `taskType=StockSync` | 64.4 / 36.2 / 29.7 ms | 6.3 / 7.2 / 8.7 ms | 0.44 / 0.34 / 0.56 ms |
| `workerName=OrderWorker` | 44.1 / 41.5 / 34.8 ms | 10.1 / 10.2 / 13.5 ms | 0.59 / 0.56 / 0.84 ms |
| `sortBy=updatedAt` | 40.2 / 60.8 / 33.4 ms | — | 9.5 / 13.3 / 16.1 ms (still sorts every task: the order is `COALESCE(UpdatedAt, CreatedAt)`, recorded) |
| Summary: stock sync per channel | 5.2 / 18.7 / 5.6 ms (reads all 14,000 stock-sync tasks) | — | 0.13 ms (`lastSucceededAt`, in the channels statement) + 0.32 ms (counts) |

- Plan after: `Index Scan Backward using IX_ProcessingTasks_CreatedAt_TaskId` for the page, `Index Scan using
  IX_ProcessingAttempts_TaskId_AttemptNo` with `Index Only Scan Backward` for the highest attempt, once per row.
- The index was first measured as a hypothetical one in a transaction that was rolled back: unfiltered page 9.4 ms ->
  1.1 ms, `taskType` page 3.6 -> 0.09 ms, a time range 7.2 -> 0.07 ms. It is used and it helps, so it is this task's
  one migration (additive: `CREATE INDEX`).
- `lastSucceededAt` as its own lookup gains about 5 ms (>= 2 ms): changed. Per channel it is one step backwards in
  `IX_ProcessingTasks_Status_UpdatedAt`; for a channel that has stock-sync tasks but never a successful one the
  planner may walk that index further (not measured at the seed, where every channel has successes).

### A3 — the red CI runs
Run 38279643947 (`294cb91`) **and** run 38392950288 (`f363c23`, the commit that planned this task) fail in the same
job and test: *Frontend* -> `orderEntry.test.tsx` > "asks for the required fields before anything is sent, and the
last line cannot be removed" — `Unable to find an element with the text: Hãy nhập thông tin cho trường Khách hàng`.
Both commits change documents only; the backend job and the three image jobs are green in both. Cause: a **flaky
test**. The form validates each field on its own and shows each error when that field's validation has finished; the
test waited for the first text (`findByText`) and asked for the other three at once (`getByText`). On a slower
runner the second error is not there yet. Fixed by the rule of log "Task 017 result" (wait for the condition, no
longer timeout): every text is waited for. The same pattern in `stockImport.test.tsx` was fixed with it. The full
runs of this task found a third test of the kind (`channelDetail.test.tsx`: a button asked for by its English name
at once after the language switch, whose texts are loaded by the first switch — red in 2 of 3 full runs): fixed the
same way. No run was repeated to get it green.

### B — mutants (all killed)
| # | Mutant | Killed by |
| --- | --- | --- |
| 1 | sweep: the reference question is skipped | `An_object_a_product_starts_to_refer_to...`, `The_clean_up_deletes_old_uploads...`, the dry-run test |
| 2 | sweep: age is ignored | `The_clean_up_deletes_old_uploads_and_old_unreferenced_images_and_nothing_else` |
| 3 | sweep: keys the application did not write are deleted too | the same |
| 4 | sweep: a dry run deletes | `A_dry_run_reports_what_a_run_would_delete...` |
| 5 | sweep: no limit per run | `A_run_stops_at_its_limit...` |
| 6 | confirm: the first bytes of the copy are not checked | `An_upload_replaced_between_the_first_look_and_the_copy...`, `Confirm_rejects_and_deletes...` |
| 7 | confirm: the size of the copy is not checked again | `An_upload_replaced_between...` |
| 8 | confirm: no deadline between copy and lock | `A_confirmation_held_up_longer_than_the_deadline...` |
| 9 | confirm: the product refers to the staging key | `Presign_upload_and_confirm...` |
| 10 | confirm: a repeated confirmation is not recognised | `The_upload_link_cannot_change_the_image...`, `Confirming_the_upload_of_the_current_image_again...`, the storage-down test |
| 11 | confirm: the loser of two confirmations of one upload keeps its copy | `Concurrent_confirms_of_the_same_upload...` |
| 12 | confirm: the staging object is not deleted | `Confirm_rejects_and_deletes...`, the flow tests |
| 13 | key: a final key is accepted as an upload key | `ProductImageKeyTests` |
| 14 | task list: the last attempt is the first one | `The_last_attempt_of_every_task_of_a_page...`, `QueryShapeTests` |

### B1 — how a confirmation works now
`404` -> the key is a **staging** key of this product (else 400) -> if the product's image is already a copy of this
upload: 200, nothing looked at (the repeated confirmation; no storage call, so it works while the storage is down) ->
`HEAD` of the upload (exists, size, type; a hopeless upload is deleted without being copied) -> **copy** to
`products/{productId}/{new id}` with the content type set explicitly -> `HEAD` and first bytes **of the copy** (size
and type again, signature) -> transaction: lock the product row; if a concurrent confirmation of the same upload came
first, do nothing; if more than 30 s passed since the copy, give up (503); else set `ImageKey` to the copy -> after
the commit delete the previous image (or the own copy of a loser) and the staging object. A confirmation that does
not finish deletes its copy, except when the commit itself failed (its outcome is unknown: the clean-up decides).

- **Old keys stay valid:** the final format is unchanged (`products/{productId}/{uuid}`, 82 characters); reading,
  replacing and removing work on the stored key as before; nothing rewrites a stored key.
- **How a repeated confirmation is recognised:** the id of a final object starts with 60 bits of the SHA-256 of the
  upload id and ends with 62 random bits (UUID version 8). `IsFinalOf(imageKey, uploadId)` compares the first part.
  A key stored before this task is a UUIDv7 and never matches.
- Tests of the task's list: two confirmations of different uploads (three, as before), a repeated confirmation, a
  staging key of another product, the product's own final key as "upload key" (400), four concurrent confirmations
  of the **same** upload (one image, one object left), the link used again after the confirmation (bytes served
  unchanged), the upload replaced between the first look and the copy (rejected by the check of the copy, both
  objects gone), a failed commit (copy left, swept later, image intact).

### B2 — the clean-up and why it cannot delete a referenced object
The argument is in the class comment of `ProductImageCleanup`. In short: a product's `ImageKey` becomes a final key
in exactly one place — the transaction of the confirmation that created that object. The object is written once, by
that confirmation (its key is new, no link leads to it, no client can name it), and the confirmation refers to it
within 30 s after the copy or removes it. The clean-up only considers objects written more than `MinAge` ago, and
`MinAge` is at least 2 minutes (4 deadlines; the host does not start with less; production: 24 h). So an object that
is old **and** unreferenced when asked can never become referenced. The question is asked after the listing all the
same, one statement per page of 200 keys; the test "an object a product starts to refer to between the listing and
the question is kept" does exactly that through a hook on the storage.
I could not construct an interleaving that deletes a referenced object. The nearest ones, and what happens:
- a confirmation of an upload that is older than `MinAge` while a run deletes that upload: the copy fails ("nothing
  uploaded"), nothing is referred to;
- a confirmation that committed although its commit call threw: the copy is kept by the confirmation and by the
  clean-up (it is referenced);
- the storage's clock ahead of or behind the Workers' by less than `MinAge` minus the deadline: no effect.
On the throwaway stack the drill showed it end to end: an upload nobody confirmed and an image object left by a
remove while the storage was down were deleted about two minutes after they were written (116 s after the script
started waiting), the image in use of the same age was listed as "in use, kept" and still served.

### B3 — signed `Content-Length`
The presigned `PUT` now signs `content-length` (signed headers `content-length;content-type;host`). SeaweedFS 4.48:
a body one byte longer, one byte shorter or 5 MiB against a small declaration -> **403**, the declared length -> 200
— in the integration test against the emulator and in the smoke **through the Caddy proxy** (steps 07–10). Kept. The
frontend sends the file whose size it declared (`file.size`); the browser sets the header itself.

### C2 — browser pass (production build, `vite preview`, throwaway stack, 1280 px, signed in as an Admin)
axe-core in the real browser on 16 views (13 menu pages, order detail, task detail, channel detail), then the
keyboard. Before the fixes: no violation of any rule except the ones in this table; after: none of `serious` /
`critical` on any view.

| Page | Issue | Fix |
| --- | --- | --- |
| every page | brand blue `#1677ff` as text on white and under white button text: 4.1:1; on the selected menu item 3.66:1 (AA needs 4.5:1) | `PRIMARY_COLOR` `#0958d9` (AntD blue-7): 6.2:1 / 5.5:1 |
| Channels, Orders, Order detail, Task detail, Products, Inventory, Users, Dashboard | "success" tag `#52c41a` on `#f6ffed`: 2.2:1; warning and error tags and the text of a field error likewise below 4.5:1 | status text colours `#237804`, `#a35a00`, `#cf1322` (5.4 / 5.0 / 5.1:1 on their tints); the light tints set explicitly (derived from the darker colours AntD made them grey: seen and corrected) |
| Dashboard, Reports, Order detail | secondary text, statistic titles, description labels at 45 % black: 3.3:1 | 65 % black (7.0:1) |
| every page with filters, Order entry | placeholders at 25 % black: 1.8:1 — in the filters the placeholder is the visible name | 60 % black (5.7:1) |
| Order detail, Task detail | the attempts table scrolls sideways and has nothing focusable: it cannot be scrolled by keyboard (axe `scrollable-region-focusable`, serious) | its scroll container is a tab stop, a region named after the table and — on the order detail — its task |
| Channel detail (mapping dialog) | two disabled inputs (channel, product) without a label a screen reader can find (axe `label`, critical; found by the test suite) | label tied to the input (`htmlFor` / `id`) |
| Reports (orders export, with an error shown) | the second input of the date range has no name but its placeholder (axe `label-title-only`, serious; found by the test suite) | `aria-label` on the range |
| Dashboard | "Updated at ..." changes when a realtime hint arrives, nothing says so | `aria-live="polite"` on that text (the connection indicator already is `role="status"`) |
| every select | the list of options (`role="listbox"`) has no accessible name (axe `aria-input-field-name`, serious) | **not fixable here**: the library offers no way to name it; its combobox is named. Written down as the one exception in `src/test/accessibility.ts` |
| every page | no `<h1>` (axe `page-has-heading-one`, moderate) | **reported, not changed**: the page title is the `PageHeader` of every page — a change of page structure |
| every page | keyboard focus is shown by a 2.4 px outline in a light blue (`rgb(133,192,255)`, about 1.9:1 against white); a sortable column header shows focus by its text colour only | **reported** (question 1) |

Keyboard, checked on Products, Customers and Orders: Tab order is menu -> header (language, sign out) -> page actions
and filters -> column headers -> row actions -> paging, every one reachable; Enter on a column header sorts
(`aria-sort`, the address); a filter opens with the arrow keys, Enter chooses, the focus stays on the filter; a dialog
opened with Enter takes the focus (`role="dialog"`, `aria-modal`, labelled), Tab stays inside it (past the last
control the focus touches the page's first element for one event and is put back on the dialog's first control — it
never rests outside). **Escape and the focus returning to the opening button could not be judged in the browser
pane** (it was hidden, and a hidden pane does not advance the close animation): that part is the new test in
`customers.test.tsx`, which passes.

## Deviations from the task
1. **B1: the checks run on the copy**, after a cheap look at the upload (exists, size, type) — not "checks on the
   staging key, then copy". The upload link is still valid while a confirmation runs: what is checked under the
   staging key can be replaced before the copy. The copy cannot be written by anybody else, so checking it is what
   closes review 019 P3 without a gap. Two more storage calls per confirmation.
2. **B1: the final key is new for every confirmation** (a digest of the upload id + random bits), not derived 1:1
   from the staging id. With one derived key, two confirmations of the same upload would write the same object, and
   an old orphan under that key could be written again and then be deleted by a clean-up that listed its old age.
   With a key of its own per confirmation the safety argument of B2 needs no exception.
3. **B1: a confirmation gives up 30 s after its copy** (503 `storage_unavailable`, the upload stays, the client can
   repeat) — the bound B2 rests on. Not in the task.
4. **B2: `MinAge` has a floor of 2 minutes** (start-up validation), `Interval` 1 s to 7 days, `MaxPerRun` 1 to 10,000
   (default 500). The drill therefore waits two minutes, not seconds.
5. **B2: a dry run** (`--image-cleanup-dry-run=true`: no worker starts, nothing is deleted or written) — added for
   the count Part G asks for, with the same code as a real run.
6. **B2: keys under the prefixes that are no key of the application are never deleted** (counted and logged), and
   every run writes one log line (the event only when something was deleted).
7. **A2: the lookup is `AttemptNo = (SELECT max(...))` per row, not `ORDER BY ... LIMIT 1`** — EF Core translates
   `OrderByDescending().Take(1)`, correlated or from the navigation, to `ROW_NUMBER()` over the whole table (tried
   first, measured: unchanged 40 ms). The plan is the per-row index lookup the task asks for; a test pins that no
   statement of the list contains `ROW_NUMBER`.
8. **A2: the index is `(CreatedAt, TaskId)`, both ascending**, not `(CreatedAt DESC, TaskId)`: the list sorts both
   columns in the same direction, read backwards the ascending index serves "newest first" and forwards "oldest
   first"; a mixed index would serve neither without a sort.
9. **C2: axe runs after every test of the suite** (what each test ends on), not through a helper called per page: no
   page or dialog can be forgotten. Cost: the suite takes about 5.4 minutes instead of about 4.7 on this machine.
   While axe runs the injected style sheets are detached (jsdom matched every rule for every element: 1–2.8 s per
   test, 0.3–0.6 s without).
10. **C2: the colours of the theme changed** — visible on every page (a darker blue, darker status texts, darker
    secondary text and placeholders). The task allows "contrast within the theme"; it is still a change of look the
    user should see before the release (question 2).
11. **`axe-core` 4.13.0, not the newest 4.14.0** (published 8 days before this task): pinned to a version that has
    been out for two months. Licence MPL-2.0, development only.
12. **`_hardening.ps1` waits out a 429 of the login limit** (61 s, at most twice per request) so that five runs in a
    row are stable; a run that hits the limit takes a minute longer.
13. **`task-025b.ps1` x 5 on the throwaway stack is only partly meaningful** (six steps test Vercel and TLS); its
    hardening block is what the five runs were for. The live five runs belong to Part G.
14. **`task-026b.ps1` ran without the stale drill** (its code is unchanged since 026b).
15. **`task-019.ps1` changed** (step 14: the key of a presign is a staging key) — an older smoke script that would
    otherwise fail on the new key form.
16. **Worker test hosts run without the clean-up** unless a test switches it on (`WorkersFixture`), like the stale
    sweep and reconciliation.

## Blocked / questions for Cowork
1. **Focus indicator.** The focus ring is AntD's light-blue outline (about 1.9:1 against white) and a sortable
   column header shows focus by colour only. Both are theme-level (`colorPrimaryBorder` is also the border of info
   alerts and "processing" tags). Make the ring the brand blue through a component token / a global `:focus-visible`
   rule, in a follow-up?
2. **The new colours** (deviation 10) — accept as they are, or should the brand blue stay `#1677ff` for large
   elements (buttons: white on `#1677ff` is 4.1:1, enough for large text only)?
3. **`page-has-heading-one`:** every page has a title in `PageHeader` that is not an `<h1>`. Changing it is a
   structural change of every page (the task says report). Follow-up?
4. **A third confirmation outcome.** What the upload link writes *after* a confirmation stays under the staging key
   (nothing refers to it) and a later confirmation of that key — once the image is no longer a copy of that upload —
   treats it as an ordinary upload: copied, checked, new image (smoke step 24). That is consistent (a Manager
   confirms what is there now), but it means a staging key is reusable for as long as its link lives. Fine, or
   should a confirmed upload id be refused for good (it would need a place to remember it)?
5. **Task list sorted by `updatedAt`** still sorts every task (9–16 ms at the seed): the order is
   `COALESCE(UpdatedAt, CreatedAt)`; an expression index would serve it. Recorded, not added.
6. **`Products.ImageKey` has no index:** the clean-up's reference question reads the products once per page of 200
   keys. Fine at this size; an index when products are in the tens of thousands.

## Development steps (applied)
- Storage keys, compose services: nothing changed for development (`docker/docker-compose.yml` is untouched).
- Migrations, as the migrator (connection from the user-level variable, never printed): `dotnet ef migrations list
  --no-build` -> 16 migrations, pending exactly `20261013132359_TaskListOrderIndex`; it had been applied on two
  throwaway databases before (the measuring one and `omni026c`); additive (`CREATE INDEX`).
  `dotnet ef database update --no-build` -> `Applying migration '20261013132359_TaskListOrderIndex'. Done.`; pending
  afterwards: 0.
- `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-026c.ps1` on development -> PASSED: all 28 checks.
- `scripts\smoke\task-019.ps1` on development -> SMOKE_019_PLACEHOLDER
- No host is left running (the scripts stopped what they started); the measuring container and the throwaway stack
  are removed.

## Production steps (applied)
None. Part G has not started (it needs the user's go-ahead, and the count below comes first). No request was sent to
production in this task.

## Manual steps for the user
1. **Go-ahead for the count on production (read-only).** Method: after step 2 of the release procedure (the new
   images are built, the old containers keep running), `docker compose @stack run --rm --no-deps workers
   --image-cleanup-dry-run=true` — a one-off container of the new Workers image that starts no worker, lists the two
   image prefixes, asks the database which of the old objects a product refers to, **deletes nothing and writes
   nothing**, and logs the counts and the keys. I will report the number before anything else happens.
2. **Go-ahead for the release (Part G)** after that number: runbook section 4 with section 5 for the one index
   migration (`TaskListOrderIndex`, additive), then `task-026.ps1` and `task-025b.ps1` live five times each, the
   first `ImageCleanup` line in the Workers log, and the images of the live products.
3. Look at the new colours once before the release (question 2): `npm run dev` in `frontend/`, any page.

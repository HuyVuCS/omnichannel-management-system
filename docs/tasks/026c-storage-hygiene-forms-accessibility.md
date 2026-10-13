# Task 026c — Follow-ups of 026b (proxy limit, task-list query, red CI run); object storage hygiene (staging keys, orphan sweep, signed length); one required-field text; accessibility pass

Status: Open
Written by: Cowork, 2026-10-13
Depends on: 026b (accepted — `docs/tasks/026b-database-performance-stale-tasks.review.md`)

## Context
Doc §12 (S3 product images), §3.2 (UI), §14; CLAUDE.md. Roadmap row 026c. Production has **no backup**; guardrails of
`docs/tasks/README.md` "Production steps" are absolute. Part G releases to production with the user's go-ahead.

Read first: review 026b (questions 1, 3, 4); reviews 019 (P3 upload link after confirm; orphans; q on `Content-Length`),
024b (q2 required text); log "Task 019 plan/result", "Task 020 plan" (export cleanup — the pattern for a storage sweep),
"Task 026b plan"; `ProductImageService.cs`, `ProductImages.cs` (`ProductImageKey`), `S3ObjectStorage.cs`,
`ReportExportCleanup*`, `ProcessingTaskService.cs` (task list query), `docker/proxy/Caddyfile`,
`scripts/smoke/_hardening.ps1`, `frontend/src/app/providers.tsx`.

### Stop and report (`Blocked`) if
- a change would make an image that production serves today unreachable (its stored key must stay valid as it is);
- the orphan sweep could delete an object that a product row refers to, under any interleaving you can construct;
- the accessibility pass finds a problem whose fix changes a flow or a page structure (report it; fix only local
  issues: names, labels, roles, focus, contrast within the theme);
- a migration appears (none expected).

## Part A — Follow-ups of review 026b

### A1. Proxy body limit above the API's (q1)
`max_size` of the API site = **1 MiB** = 8 × `RequestBodyLimit.MaxBytes`; the backend test that parses the Caddyfile
asserts that relation (named in the test and in both comments). `_hardening.ps1`: 128 KiB + 1 through the proxy →
the API's readable 413 (CORS header, problem body, correlation id) — **every** run; > 1 MiB → refused (status only).
Run `task-026.ps1` / `task-025b.ps1` five times each against the throwaway stack (and live after G) — all green.

### A2. Task list query (q3)
- "Last attempt" of each task on a page: a lateral lookup per row (`ORDER BY "AttemptNo" DESC LIMIT 1`, unique
  `(TaskId, AttemptNo)` index) instead of `ROW_NUMBER()` over all attempts; same JSON (existing tests + one with
  several attempts per task).
- Measure with the seed of 026b (`scripts/perf/seed-task-026b.sql`, `auto_explain`) before / after: unfiltered page,
  three filters. Add `(CreatedAt DESC, TaskId)` only if the plan uses it and it helps (then it is this task's one
  migration — additive; remove the "none expected" stop for that index only).
- `lastSucceededAt` of the summary: measure a separate statement; change only if ≥ 2 ms at the seed; otherwise record.

### A3. Red CI run of `294cb91`
Read the run's log (`gh run view --log-failed`). Report the failing job / test and the cause. A flaky test → fix it by
the rules of log "Task 017 result" (deterministic waits, no longer timeouts); an infrastructure fault of the runner →
say so with the evidence. Not re-run until green.

### A4. Runbook
"Incidents": Docker Desktop stopped after a Windows / WSL update (what was seen on 2026-10-13, the check `wsl
--status`, `docker desktop restart`, what comes back by itself). §4: the first `compose run` after a changed service
definition recreates that service — the downtime starts at step 3 (report 026).

## Part B — Object storage hygiene (review 019)

### B1. Uploads go to a staging key; confirm copies to the final key
- Presign issues a link for a **staging** key `uploads/products/{productId}/{uuidv7}`; confirm takes that key, runs the
  existing checks (size, type, magic bytes) on it, then **copies** it server-side to the final key
  `products/{productId}/{uuidv7}` (the format of every stored image today — those stay valid unchanged), sets the
  product's `ImageKey` to the final key in the transaction, then deletes the staging object. The upload link can
  therefore never replace a confirmed image (review 019 P3 closed).
- `ProductImageKey`: one parser for each prefix; `BelongsTo` for confirm accepts **staging** keys only; product
  responses and the delete path work with final keys. A repeated confirmation of the same staging key after success
  answers as today (idempotent): decide how it is recognised (e.g. the final key derived from the staging id) and test it.
- `IObjectStorage` gets a server-side copy (`CopyObject`, metadata and content type kept or set explicitly); the
  emulator / SeaweedFS behaviour of copy is verified in the provider tests of the infrastructure (as in 019 / 020).
- Concurrency: the existing product row lock and the "losers' objects are deleted" rule stay; tests for two
  confirmations of different uploads, a repeated confirmation, a confirmation of a staging key of another product.
- Frontend: nothing changes in the flow (it sends back the key it got); check.

### B2. Orphan sweep (Workers host, periodic — like `ReportExportCleanup`)
- Deletes (a) staging objects older than **24 h** (never confirmed) and (b) objects under `products/` that **no
  product row refers to** and are older than **24 h**. Options section `ImageCleanup` (`Enabled`, `Interval` default
  6 h, `MinAge` 24 h, `MaxPerRun`), validated at start; one run at a time (advisory lock, own key); a listing in pages
  (`ListObjectsV2`, a new storage method), the reference check in batches against `Products.ImageKey`.
- Safety: an object is deleted only if it is older than `MinAge` **and** unreferenced at the moment of the check; a
  confirm that copies within the last 24 h is never at risk (age). Write down the interleaving argument in the class
  comment and test it (an object that becomes referenced between listing and check is kept).
- Export files are **not** touched (`exports/` has its own retention).
- One `SystemEventLog` per run that deleted something (counts; keys are not personal data but are capped in the list),
  Logs label vi / en; a failed delete is logged and retried next run.

### B3. Signed `Content-Length` (review 019)
Try including the announced size in the presigned `PUT` (signed `Content-Length`) against SeaweedFS **through the
proxy**: does a `PUT` with a different length get refused? If yes, keep it (the frontend already sends the exact
file). If SeaweedFS does not enforce it, do **not** keep a signature the provider ignores: record the result in the
log; B1 + B2 already bound the damage (a too-large staging object is refused at confirm and swept).

## Part C — Frontend

### C1. One required-field text (review 024b q2)
`validateMessages` in the application's `ConfigProvider`: required → "{label} là bắt buộc" / "{label} is required",
per language with the existing i18n; remove per-field `message:` props that only repeat it (keep those that say more);
existing tests that assert AntD's default text follow. Check one form of each kind (input, select, number, date, upload).

### C2. Accessibility pass
- Automated: `axe-core` as a **dev dependency** (pinned) in a Vitest helper that runs on every page rendered in the
  existing page tests (or a dedicated test that renders each route): **no `serious` / `critical` violations**; jsdom
  cannot judge colour contrast — exclude that rule there and check contrast in the browser instead.
- Manual, in the browser (production build against development, 1280 px): every page by keyboard only — focus visible,
  dialogs trap and return focus, menus and tables reachable, the connection indicator and live updates announced
  without stealing focus (`aria-live="polite"` where text changes). Contrast of the theme's primary colour on white
  and of status tags (AA). Table in the report: page, issue, fix.
- Fix local issues only (labels, accessible names of icon buttons, roles, `alt` of product images — the SKU / name,
  focus order); anything structural → report.

## Part D — Verification
- Backend 0 warnings, full `dotnet test` (local timeouts of 026b q4 may persist — CI is the reference; report both);
  frontend typecheck / lint / test ×2 / slow / build / `measure` (axe-core must not enter the bundle); CI green.
- Throwaway stack `omni026c` (own tag / ports / subnet): `task-025.ps1`, `task-026.ps1` ×5, `task-026b.ps1`, new
  `task-026c.ps1` — staging upload → confirm → final key served; the old upload link `PUT` after confirm does **not**
  change the served image; an unreferenced object older than `MinAge` (short throwaway options; age faked by a
  `MinAge` of seconds) is swept, a referenced one is kept; B3 result. Removed with `down -v` of that project only.
- Browser session (C2) against the throwaway stack or development.

## Part G — Release to production (runbook §4), with the user's go-ahead
No migration expected (A2 may add one index: then runbook §5). After `up`: `task-026.ps1` and `task-025b.ps1` live
(five times each — A1 must make them stable), the first `ImageCleanup` run in the Workers log (what it deleted —
production holds images from go-live and 025b; only unreferenced objects older than 24 h may go). **Before G**, count
what the first run would delete on production with a read-only method of your choice (no delete, no write), tell the
user the number and the method, and wait for the go-ahead. Images of the live products still displayed (user
check or `task-025b.ps1` step).

## Acceptance criteria
- [ ] A1 proxy 1 MiB, relation tested, readable 413 every run (×5 throwaway, ×5 live); A2 lateral lookup, measured,
      same JSON; A3 cause of the red run reported (and fixed if flaky); A4 runbook.
- [ ] B1 staging → copy → final, old keys valid, upload link cannot replace a confirmed image (smoke), tests;
      B2 sweep with options, lock, age + reference check, interleaving test, event + label; B3 decided by measurement.
- [ ] C1 one required text vi / en; C2 axe clean (serious / critical) on every page, manual keyboard / contrast table,
      local fixes.
- [ ] Suites and CI green; throwaway stack removed; release G with live checks green; nothing referenced deleted.
- [ ] Report with "Development steps (applied)" and "Production steps (applied)".

## Out of scope
Dropping indexes; a provider lifecycle rule; image resizing / thumbnails; a product detail page; backups; Oracle VM
(025c); end-to-end acceptance tests (027).

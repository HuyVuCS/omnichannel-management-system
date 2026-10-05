# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-05 15:10 (+07:00), by Claude Code, **in the middle of task 021** (Part A committed, Part B not).
- Branch `master` (nothing pushed). HEAD: `076f518` "task 021 A5: a thumbnail whose image link has expired asks for
  fresh links once" (this file is committed on top of it).
- **Uncommitted (all of it is task 021 Part B, complete and per-file tested, waiting for the final verification):**
  - modified: `README.md`; `frontend/src/app/{navigation.tsx,router.tsx,codeSplitting.test.tsx}`;
    `frontend/src/features/{alerts/alerts.test.tsx, auth/auth.test.tsx, dashboard/dashboard.test.tsx,
    inventory/InventoryPage.tsx, inventory/inventory.test.tsx, logs/eventSummary.ts, logs/logs.test.tsx,
    logs/systemEventsApi.ts, orders/OrdersPage.tsx, orders/orders.test.tsx, processing/processingApi.ts,
    realtime/hintTags.ts, realtime/hintTags.test.ts, realtime/realtime.test.tsx}`;
    `frontend/src/shared/{api/baseApi.ts, i18n/en.json, i18n/vi.json, utils/format.ts}`; `frontend/src/test/systemEvents.ts`
  - new: `frontend/src/features/reports/` (page, forms, table, API slice, tests), `frontend/src/test/reports.ts`,
    `scripts/smoke/task-021.ps1`
- No API, Workers host or preview server is left running by Claude Code. The development PostgreSQL, RabbitMQ and
  object storage containers are up. A frontend test series may still be running in the background of the old chat (it
  writes nothing into the repository).
- Newest migration: **`20261004183420_ReportExports`** — **applied to development** on 2026-10-05 (with
  `20261004074312_ProductImage`), by Claude Code as the migrator. Task 021 adds no migration.

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs (simulated); SignalR only notifies the UI; product
images and export files live in S3-compatible object storage (no AWS).
Source-of-truth order when documents disagree:
1. `CLAUDE.md`
2. Architecture doc `THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx` (in the Cowork project, not in the repository)
3. `docs/implementation-log.md` (decisions and deviations)
4. `docs/roadmap.md` (plan, open decisions)

## 3. Workflow
- **Cowork** plans and reviews: writes `docs/tasks/NNN-*.md` and `NNN-*.review.md`, updates the roadmap and the log.
- **Claude Code** implements: `/next-task` picks the lowest-numbered task without a report, commits, writes
  `NNN-*.report.md`, and **applies the development steps itself** (key helper, changed containers, additive
  migrations as the migrator, smoke script on development).
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps".

## 4. Progress
- 001–020 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 021 — Reports & Excel export page + Part A (review 020, review 019) + A0 — **Open, in progress**:
  [task](tasks/021-reports-page.md). No report yet.
- 022 — Reconciliation Worker — Planned
- 023 — `POST /api/webhooks/{slug}` — Planned
- 024 — Invoice Worker + Notification Worker — Planned

## 5. In flight — task 021
**Done and committed**
- A0 (development steps of 019/020): `scripts\setup-object-storage.ps1`; `docker compose … up -d objectstorage`
  (recreated with the `127.0.0.1` CORS origins); `dotnet ef migrations list` (pending exactly `ProductImage`,
  `ReportExports`, both additive) → `database update`; smoke `task-019.ps1` **50/50** and `task-020.ps1` **46/46** on
  development (020 on the committed state, before any change).
- `3a94587` Cowork's documents of the round. `b492b86` A1–A4: the sweep marks `DeletedAt` and resolves the task's
  `ProcessingFailed` alert in one transaction; manual retry of an expired export → 409 `export_expired`; `Expired`
  by time in list / detail / download (`ReportExportExpiry`); `SixLabors.Fonts` pinned at 1.0.0 with transitive
  pinning (resolved versions identical before/after, 286 lines, 0 differences; a package needing 2.x fails with
  `NU1109`; 1.0.0 is Apache-2.0 per nuget.org); smoke 020 step 17 reads `True`. Backend **1300/1300**, 0 warnings;
  smoke 020 again 46/46 on development with these changes. `076f518` A5 (expired image links).

**Done, not committed (Part B)** — see section 1. Verified so far: typecheck and lint exit 0; every touched test
file green; **full `npm test` 4 of 4 green so far in the five-run series** (452 tests; 432 s, 259 s, 218 s, 234 s);
first-load set 970.14 → 971.00 kB raw (318.10 → 318.40 gzip), `ReportsPage` is a lazy chunk (12.8 kB).
Browser check on the production build on development (1280 px, vi + en): shortcut from Orders prefilled the form;
the export went Pending → In progress → Succeeded by push (no polling); download click produced an anchor to the
storage with `rel="noreferrer"`, no `target`, and the link is in no store / DOM / text afterwards; a fetch of the
link without credentials answered 200 with bytes starting `PK`; inventory export; labels on Sync & Processing, Logs
and the dashboard; Staff has no menu item, gets the no-access page and no shortcut button.

**Left to do (in this order)**
1. Fifth full `npm test` run and one full `npm run test:slow` (run with nothing else running; if the old chat's
   results are not available, run the series again: 5 × `npm test`, then `npm run test:slow`).
2. `npm run build` + `npm run measure` (final numbers), then `scripts\smoke\task-021.ps1` on development (run it in
   the background and read its output file; it ends with the frontend build and tests).
3. Final `dotnet build` + `dotnet test` in `backend/` (no backend change since `b492b86`).
4. Commit Part B as `task 021: …`; write `docs/tasks/021-reports-page.report.md` and commit it.

**For the report (not written anywhere else yet)**
- Deviations: A2's "one second before `ExpiresAt`" is a unit test of `ReportExportExpiry` (the single API test host
  runs on the real clock; the API test uses ±30 s / −1 s); the "pin bites" probe was
  `SixLabors.ImageSharp.Drawing` 2.1.0; `X-Amz-Expires=300` assertion of 020 loosened to 300|301 (timing-dependent,
  red once); two existing export tests now expect `Expired` (A2); tests that used `/reports` as the request-free
  placeholder page now use `/no-such-page`; `export_too_large` / `too_many_exports` are shown in the client's own
  words (`errors.*`, the existing convention), not the server's `detail` with its numbers; the exports table sums to
  976 px (992 minus a scrollbar), action column fixed right.
- Question for Cowork: `ComingSoon` and `plannedTask` are unused now (every menu page is built) — remove them?
- No mutation run was done for 021 (the task does not ask for one).

## 6. Pending for the user
- **One confirmation:** the browser check did not let the browser actually *save* the export file (a download in the
  Browser pane needs your explicit OK). Either say "yes, download it" in the chat, or click *Download* on
  `/reports` once yourself and confirm that the file is saved under its name and the page stays. The header that
  causes it (`Content-Disposition: attachment`) is checked by smoke 020 step 30 and smoke 021 step 16.
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (Claude Code reads it
  from there; it is never printed).
- Open decisions (roadmap): before 024 — where "payment succeeded" comes from; before 025 — hosting and the
  production object-storage provider.

## 7. Environment
- Start infrastructure with docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals:
  API, Workers, scripts/commands. Commands: README §1, §5, §6; exports: README "Reports and Excel export";
  frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672, object storage S3 API 8333 (all on `127.0.0.1`).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID`,
    `OBJECT_STORAGE_SECRET_ACCESS_KEY`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts;
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (user-level environment variable) — for `dotnet ef database update`.
- Tests: `dotnet test` in `backend/` (1300, about 3 min; needs Docker); `npm test` in `frontend/` (452, 4–7 minutes,
  run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`.
- Test accounts on development: smoke users are locked after each run (also `smoke-021-ui-*` of the browser check).

## 8. Gotchas learned
- **An export ends at `ExpiresAt`**, swept or not: `Expired` in the API, download and retry 409 `export_expired`;
  the sweep resolves the task's alert in the transaction that sets `DeletedAt`. → README "Reports and Excel export".
- **Transitive pinning is on**: every version in `Directory.Packages.props` also caps transitive packages; a
  conflict is `NU1109` at restore. → README, *Package*.
- **A download link is a credential**: fetched by a mutation, followed through a temporary anchor, removed with
  `request.reset()` — the hook's own `reset` does not remove it. → `features/reports/components/ExportsTable.tsx`.
- **Every menu page makes requests now**: the request-free page of tests is `/no-such-page`. → Part B tests.
- **A table next to a scrollbar has 976 px** at 1280 px, not 992. → `LogsPage.tsx`, `ExportsTable.tsx`.
- **Expired image links**: a thumbnail asks for fresh links only after `expiresAt`, and once until an image has
  loaded. → README "Frontend", *Product images*.
- **Migrations as the migrator**: read the user-level variable inside a script, redact the output, list before
  update, additive only. → `docs/tasks/README.md`.
- **Exports:** always a background task; message `{ taskId }`; visible to the requester only. → report 020.
- **Alerts are written only by `AlertStore`**, inside the transaction of the cause. → README "Alerts".
- **Frontend:** tests end with `await settled()`; request tests also run under `npm run test:slow`; check effects in
  `npm run build` + `vite preview`; jsdom applies no library CSS. → README "Frontend".
- **A script that starts hosts and leaves them running hangs the calling tool**: run it in the background and read
  its output file; a running API / Workers process locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only (no repository path literal in a scratch script — pass it as a parameter);
  stderr of native tools is a terminating error under `Stop`. → report 020, deviation 11.
- **Bash heredocs with apostrophes fail** in this shell: write edit scripts to a file first.
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Claude Code:** finish task 021 — section 5, "Left to do" (test series, smoke 021 on development, final backend
run, commit Part B, report). **User:** the one confirmation of section 6. Then **Cowork:** review task 021.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: chờ Claude Code hoàn tất task 021 (docs/tasks/021-reports-page.report.md chưa có; Part A đã commit b492b86 và 076f518, Part B chưa commit), sau đó review task 021, cập nhật roadmap và implementation log, rồi viết task 022 (Reconciliation Worker).
```

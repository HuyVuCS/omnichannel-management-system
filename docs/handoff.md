# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-05 15:50 (+07:00), by Claude Code, right after finishing task 021 (replaces the snapshot taken
  in the middle of it).
- Branch `master` (nothing pushed). HEAD: the report commit of task 021 (`task 021: report`), on top of `896d27c`
  "task 021: Reports & Excel export page …"; this file is committed with or right after it.
- Working tree: clean. No API, Workers host or preview server is left running by Claude Code; the development
  PostgreSQL, RabbitMQ and object storage containers are up.
- Newest migration: **`20261004183420_ReportExports`** — applied to development on 2026-10-05 (with
  `20261004074312_ProductImage`), by Claude Code as the migrator. Task 021 adds no migration.
- `docs/roadmap.md` still shows 021 as `Open`: Cowork updates the roadmap and the log in the review.

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
- 021 — Reports & Excel export page + Part A (review 020, review 019) + A0 — **Review**:
  [task](tasks/021-reports-page.md), [report](tasks/021-reports-page.report.md).
- 022 — Reconciliation Worker — Planned
- 023 — `POST /api/webhooks/{slug}` — Planned
- 024 — Invoice Worker + Notification Worker — Planned

## 5. In flight — task 021
**Task 021: report written, awaiting Cowork review.** Nothing half-finished; everything is committed:
`3a94587` Cowork's documents of the round, `b492b86` A1–A4 (backend), `076f518` A5, `896d27c` Part B (Reports page,
shortcuts, live tag, labels, smoke script, README), then the report.
Verified: backend build 0 warnings, **1300/1300**; frontend typecheck and lint clean, **5 of 5** consecutive full
`npm test` runs green (452 tests) and one full `npm run test:slow` green (the third; the first two are explained in
the report); smoke `task-019.ps1` 50/50, `task-020.ps1` 46/46 (twice) and `task-021.ps1` **25/25 on development**;
first-load set 970.14 → 971.00 kB raw; browser check on the production build (vi + en, Manager and Staff).
Questions the review must answer (report, "Blocked / questions for Cowork"):
1. The browser actually saving a downloaded file was not exercised (see section 6).
2. `ComingSoon` / `plannedTask` / the `comingSoon` texts are unused now — remove?
3. `MAX_EXPORT_RANGE_DAYS` is the second client copy of a server limit — keep constants, or a config endpoint (026)?
4. The time zone shown is the browser's IANA zone as the OS names it (here `Asia/Bangkok`).
Deviations to confirm are listed in the report (nine, none architectural).

## 6. Pending for the user
- **One optional check:** on `/reports` (Manager or Admin) click *Download* on a succeeded export and confirm that
  the browser saves the `.xlsx` under its name and the page stays. Claude Code checked everything up to that click
  (link target, `rel`, nothing kept, bytes and the `attachment` header) but may not save a file in the Browser pane
  without an explicit OK.
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
**Cowork:** review task 021 (report + commits `b492b86`, `076f518`, `896d27c`), answer the four questions, update
the roadmap and the implementation log, then write task 022 (Reconciliation Worker). **User:** the optional download
check of section 6. **Claude Code:** `/next-task` once task 022 exists.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 021 (docs/tasks/021-reports-page.report.md, các commit b492b86, 076f518, 896d27c), trả lời 4 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 022 (Reconciliation Worker).
```

# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-05 03:15 (+07:00), by Claude Code, right after finishing task 020.
- Branch `master` (nothing pushed). HEAD: `c596353` "task 020: report" (this file is committed on top of it).
- Working tree: clean. No API, Workers host or throwaway container is left running by Claude Code; the development
  PostgreSQL, RabbitMQ and object storage emulator containers are up (not touched in this chat).
- Newest migration: **`20261004183420_ReportExports`** (task 020) — generated, applied only on a throwaway
  environment; **not applied to development**. Before it: `20261004074312_ProductImage` (task 019; whether it is
  applied on development is unconfirmed), `20261002215102_Alerts` (applied).
- `docs/roadmap.md` still shows 020 as `Open`: Cowork updates the roadmap and the log in the review.
- Left on the machine by this chat: the image `mcr.microsoft.com/dotnet/aspnet:10.0` (340 MB, pulled for a Linux check).

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
- **Claude Code** implements: `/next-task` picks the lowest-numbered task without a report, commits, writes `NNN-*.report.md`.
- **The user** runs steps that need secrets and the manual checks; smoke scripts live in `scripts/smoke/`.
- Details and the report template: [docs/tasks/README.md](tasks/README.md).

## 4. Progress
- 001–019 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 020 — Excel export in the background + Part A (review 019) — Done ([review](tasks/020-excel-export.review.md)).
- 021 — Reports & Excel export page + Part A (review 020, review 019) + A0 (development steps of 019/020) — **Open**:
  [task](tasks/021-reports-page.md).
- 022 — Reconciliation Worker — Planned
- 023 — `POST /api/webhooks/{slug}` — Planned

## 5. In flight
**Task 020: report written, awaiting Cowork review.** Nothing half-finished; everything of this chat is committed:
`a9c53f3` Cowork's documents of the round, `4f20d69` Part A, `141c33d` backend + worker + migration + tests,
`4df5baa` smoke script + key helper + README + `.http`, `c596353` report.
Verified: backend build 0 warnings, 1278/1278 tests; 22 of 22 mutants killed; smoke 46/46 on a **throwaway**
environment; an export at `MaxRows` peaks at 255 MiB (orders) / 336 MiB (inventory) in the Workers process; log scan
clean; IANA time zones and a workbook without fonts checked on Windows and in the API's Linux image. No frontend file
changed. No stop condition of the task was hit.
Questions the review must answer (report, "Blocked / questions for Cowork"):
1. The `ProcessingFailed` alert of an export that can never succeed (`export_expired`, `export_too_large`,
   `export_parameters_invalid`) never resolves — resolve it in the sweep, or open none?
2. An export past `expiresAt` but not swept yet is still `Succeeded` and downloadable (at most one sweep interval).
3. `SixLabors.Fonts` (transitive of ClosedXML) is 1.0.0 / Apache-2.0; from 2.0 another licence — pin it (026)?
4. `ObjectStorage:RequestTimeout` (10 s) also bounds the upload of a whole export file (025).
5. Unfinished exports count toward the per-user limit while the Workers host is down (409 for the fourth).
6. Windows time zone names are accepted where the host knows them — restrict to IANA?
7. The dashboard summary's `processing` now has a `ReportExport` entry (label in 021).
Doc deviations 22–25 are confirmed in the report (23 with one addition: event type `report_export.message_rejected`).

## 6. Pending for the user
*(Updated by Cowork after review 020.)* Since 2026-10-05 Claude Code applies the development steps itself
([docs/tasks/README.md](tasks/README.md), "Development steps"); the steps of 019/020 are task 021 A0.
One-time, by the user: the persistent user environment variable `OMNICHANNEL_MIGRATOR_CONNECTION` (migrator role,
`Host=127.0.0.1`), then restart the editor / Claude app so Claude Code inherits it.
- Open decisions (roadmap): before 024 — where "payment succeeded" comes from; before 025 — hosting and the
  production object-storage provider.

## 7. Environment
- Start infrastructure with docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals:
  API, Workers, scripts/commands. Commands: README §1, §5, §6; stock sync: README §7; images: README "Product images";
  exports: README "Reports and Excel export". Frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672, object storage S3 API 8333 (all on `127.0.0.1`).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID`,
    `OBJECT_STORAGE_SECRET_ACCESS_KEY`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`,
    and since 020 `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey` (set by the key helper);
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (1278, about 2.5 min; integration tests need Docker: PostgreSQL, RabbitMQ,
  `chrislusf/seaweedfs:4.48`); `npm test` in `frontend/` (405, 4–5 minutes, run with nothing else running);
  `npm run test:slow`; `npm run measure` after `npm run build`.
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.
- Test accounts on development: smoke users are locked after each run; the test Admin `ui-check-61563` is locked.

## 8. Gotchas learned
- **Exports:** always a background task; the message carries only `{ taskId }`; rows are read in one
  `REPEATABLE READ, READ ONLY` transaction; the upload happens after it; the object key is `exports/{taskId}.xlsx`;
  an export exists only for its requester (404 otherwise). → README "Reports and Excel export"; report 020.
- **Text cells:** ClosedXML never writes a formula through `Value`, but it drops a leading apostrophe — the sheet
  writer doubles it. Times are converted to the request's zone; widths are fixed. → `Excel/ReportSheet.cs`; report 020.
- **Lists and exports share their filters** (`OrderFilters`, `InventoryFilters`): change a list filter there. → report 020.
- **Workers test hosts** get dummy storage settings and no retention sweep unless asked
  (`BuildWorkersHost(retentionSweep: …)`); export tests use a user of their own (`ApiFactory.CreateUserClientAsync`).
  → report 020, "Existing tests changed".
- **Product images:** the API never receives image bytes; nothing is trusted until confirm checks the stored object;
  no storage call inside a transaction; links and keys are never logged. → README "Product images"; report 019.
- **A rejected worker message carries a `reasonCode`**; a new event type needs an allowlist entry and a detail case.
  → README "System events".
- **Alerts are written only by `AlertStore`**, inside the transaction of the cause. → README "Alerts"; report 017.
- **Frontend:** tests end with `await settled()`; request tests also run under `npm run test:slow`; dialogs end with
  `<ResetFormOnOpen>`; check effects in `npm run build` + `vite preview` (StrictMode hides defects); jsdom applies no
  library CSS. → README "Frontend"; reports 016b, 018b, 019; log "Task 018 result".
- **Cowork's git:** run `git --no-optional-locks status` from the Cowork VM (a plain `git status` leaves `.git/index.lock`).
- **`Assert-Step` is strict**; smoke scripts run npm after stopping the hosts they started. → `scripts/smoke/_common.ps1`.
- **A script that starts hosts and leaves them running hangs the calling tool** (piped or redirected): run it in the
  background and read its output file. → reports 018b, 020.
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result", "Task 006 plan".
- **The hub takes the token from the query string**; never log URLs of `/api/hubs/*`. → review 015.
- **A running API or Workers process locks `bin/Debug`**; stop them before building. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only; stderr of `docker` / `dotnet` is a terminating error under
  `$ErrorActionPreference = 'Stop'` (`cmd /c "… 2>&1"`); under a UTF-8 console a pipe to a native command starts with
  a byte-order mark unless `[Console]::InputEncoding` is set (`$OutputEncoding` alone is not enough).
  → `scripts/setup-object-storage.ps1`; report 020, deviation 11.
- **Git rewrites files with CRLF** on checkout / stash pop; keep constraint SQL in EF configurations on one line.
  → `ProductConfiguration.cs`, `ReportExportConfiguration.cs`.
- **Throwaway environment for smoke runs** (new migration not on development): PostgreSQL 55432, RabbitMQ 55672, the
  emulator through the compose service under another project name — on another port with a `ports: !override` file
  while the development emulator runs. → report 020, "Smoke script".

## 9. Next action
*(Updated by Cowork.)* Review 020 done (accepted), task 021 written. **User:** set `OMNICHANNEL_MIGRATOR_CONNECTION`
once (section 6). **Claude Code:** `/next-task` → 021 (A0 applies the pending steps of 019/020 first).

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 020 (docs/tasks/020-excel-export.report.md, các commit 4f20d69..c596353), trả lời 7 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 021 (trang Reports & Excel export + Part A).
```

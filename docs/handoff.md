# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-03 05:55 (+07:00), by Claude Code, right after finishing task 017.
- Branch `master` (nothing pushed). HEAD: `e2bf1b8` "task 017: report" (this file is committed on top of it).
- Working tree: clean. No API, Workers host, dev server or throwaway container is left running by Claude Code.
- Newest migration: **`20261002215102_Alerts`** (task 017) — applied to the development database by the user
  (2026-10-03).
- Updated by Cowork 2026-10-03 after review 017 (review, roadmap, log, task 018 written).

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs (simulated); SignalR only notifies the UI.
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
- 001–017 — Done (titles in [docs/roadmap.md](roadmap.md); 017 [review](tasks/017-alerts-backend.review.md) accepted:
  no P0–P1 in the new code; a pre-existing P1 form-dialog defect goes to 018 Part A1).
- 018 — Alerts page + dashboard alert card; Part A from reviews 016b and 017 — **Open**:
  [task file](tasks/018-frontend-alerts-page.md).
- 018b — Logs/Audit (`GET /api/system-events` + page) — Planned (split from 018 by the user, 2026-10-03; access and
  payload decided: implementation log "Task 018 plan").
- 019 — S3 product images (presigned upload) — Planned (decision needed first: S3 in development)
- 020 — Excel export (ClosedXML) — Planned

## 5. In flight
**Task 018 is open for Claude Code** (`/next-task`). Decisions: implementation log "Task 017 result", "Task 018 plan".
Part A, A1 first:
- A1 (P1) form dialogs reset from a component inside `<Form>` on every open (also the same record); remove
  `useResetFormOnOpen`; repetition numbers before/after; production-build check;
- A2 `currentData` on the three detail pages; A3 stale errors on optional customer fields; A4 products table at 1280 px;
- A5 smoke table: `OK` column first, no dropped columns;
- A6 (backend) deactivating a mapping resolves its `StockSyncFailed` alert (user's decision).

## 6. Pending for the user
- Optional: paste the `task-017.ps1` table (the run was reported done, the table was not pasted).
- Optional: `scripts\smoke\_assert-selftest.ps1`; `task-010` … `013`, `016` after the migration.
- Still unconfirmed from 016b: `Host=127.0.0.1` in both user-secrets and in `OMNICHANNEL_MIGRATOR_CONNECTION`; the manual
  browser check of the channel mapping page (report 016b "Manual steps").
- Open decisions (roadmap): before 019 — S3 in development; before 024 — where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5,
  §6; stock sync and the channel simulator: README §7; alerts: README "Alerts". Frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (820; integration tests need Docker, about 1.5 min); `npm test` in `frontend/`
  (292, about 3 minutes, run with nothing else running); `npm run measure` after `npm run build` (first-load set 968 kB raw).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.

## 8. Gotchas learned
- **Alerts are written only by `AlertStore`**, one SQL statement inside the transaction of the cause; a new path that
  changes stock or a threshold must call `ReconcileLowStockAsync` before commit. → README "Alerts"; report 017.
- **The low-stock rule exists twice**: `InventoryPredicates.IsLowStock` (LINQ) and `IsLowStockSql` (SQL text), pinned by
  a test. → report 017.
- **A frontend test must end with its requests settled**: an in-flight request is answered by the next test's MSW
  handlers. → report 017, "Verification".
- **Known flake until 018 A1:** the 016b dialog test above — a real product race, not a test problem. → review 017, question 1.
- **Waits in frontend tests:** use the fake clock (`useJumpableClock` / `jump` in `orderEntry.test.tsx`), not real pauses.
- **`Assert-Step` is strict** (type and value; strings case-sensitive; arrays/objects throw). → `scripts/smoke/_common.ps1`.
- **Smoke scripts run npm after stopping the hosts they started** (`Invoke-FrontendChecks`). → report 017.
- **The dev server runs React StrictMode; tests and production builds do not.** Check dialogs/effects in
  `npm run build` + `vite preview`. → report 016b.
- **`data` vs `currentData` in RTK Query**; **hints invalidated through a throttle**. → report 016b.
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result", "Task 006 plan".
- **Every `SaveChanges` that stores an order, a movement or a task status sends one `pg_notify`.** → report 015.
- **The hub takes the token from the query string**; never log URLs of `/api/hubs/*`. → review 015.
- **A running API or Workers process locks `bin/Debug`**; stop them before building. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only; run helper scripts from the repository root. → `scripts/smoke/_common.ps1`.
- **Git rewrites files with CRLF** on checkout / stash pop.
- **Claude Code's Bash tool:** heredocs with apostrophes fail; write helper scripts with the Write tool.
- **Migrations needing a smoke run before the user applies them:** Claude Code uses a throwaway PostgreSQL/RabbitMQ
  environment (generated credentials, roles from `docker/postgres/init`). → report 017, "Smoke table".

## 9. Next action
**Claude Code:** `/next-task` → task 018 (Part A first, A1 before everything else). **User:** run `task-018.ps1` and the
manual check when the report lists them. **Then Cowork:** review 018, write 018b.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Review 017 xong, task 018 đã được viết. Việc tiếp theo: review task 018 khi docs/tasks/018-frontend-alerts-page.report.md đã có, rồi viết task 018b (Logs/Audit).
```

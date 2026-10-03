# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-03 21:45 (+07:00), by Claude Code, right after finishing task 018b.
- Branch `master` (nothing pushed). HEAD: `5b8fab0` "task 018b: report" (this file is committed on top of it).
- Working tree: clean. No API, Workers host, preview server or throwaway container is left running by Claude Code;
  the development PostgreSQL and RabbitMQ containers are up.
- Newest migration: **`20261002215102_Alerts`** (task 017) — applied to the development database (confirmed by the
  018b smoke run there). Task 018b added **no migration**.
- `docs/roadmap.md` and `docs/implementation-log.md` still show 018b as `Open` / planned: Cowork updates them in the
  review.

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
- 001–018 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 018b — Logs/Audit (`GET /api/system-events` + page `/logs`) + Part A (018 report, in-flight request guard) —
  **Review**: [task](tasks/018b-logs-audit.md), [report](tasks/018b-logs-audit.report.md).
- 019 — S3 product images (presigned upload) — Planned (decision needed first: S3 in development)
- 020 — Excel export (ClosedXML) — Planned
- 021 — Reports & Excel export page — Planned

## 5. In flight
**Task 018b: report written, awaiting Cowork review.** Nothing half-finished; everything of this chat is committed
(`b84ef98` 018 report, `1267307` A2, `0f8259c` backend, `0570c8b` page + smoke + README, `5b8fab0` report).
Verified: backend 890/890, 0 warnings; frontend 373 tests, 5 of 5 full runs green with the guard; smoke 37/37 on
development; browser check on the production build. Questions the review must answer (report, "Blocked / questions"):
1. Default window of the Logs page: open-ended `?since=<instant>` (now − 24 h), or a fixed end?
2. The guard counts at the `fetch` call instead of MSW's life-cycle events (MSW reports a request ~19 ms late) — accept?
3. Three `orderEntry.test.tsx` "outcome by push" tests count list refetches differently when answers take 120 ms
   (green otherwise) — a Part A item?
4. A successful order writes no system event (its trace shows nothing on the Logs page) — new event type wanted?
5. `reason` of rejected messages is never returned (exception text / body fragments) — store a code instead?

## 6. Pending for the user
- Nothing required for 018b (no migration; the smoke script ran on development).
- Optional, 3 minutes: the keyboard pass on **Nhật ký / kiểm toán** described in the 018b report ("Manual steps").
- Still unconfirmed from 016b: `Host=127.0.0.1` in both user-secrets and in `OMNICHANNEL_MIGRATOR_CONNECTION`.
- Before running smoke scripts or migrations: Docker Desktop running and `postgres` healthy
  (`docker compose -f docker/docker-compose.yml --env-file docker/.env ps`).
- Open decisions (roadmap): before 019 — S3 in development; before 024 — where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5,
  §6; stock sync and the channel simulator: README §7; alerts: README "Alerts"; events: README "System events
  (Logs/Audit)". Frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (890; integration tests need Docker, about 1.5 min); `npm test` in `frontend/`
  (373, 3–5 minutes, run with nothing else running); `npm run measure` after `npm run build` (first-load set 970 kB raw).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.
- Test accounts on development: smoke users are locked after each run; the test Admin `ui-check-61563` is locked
  (unlock + new password through the bootstrap admin for a browser check, lock again).

## 8. Gotchas learned
- **A frontend test that ends with a request in flight fails** (`src/test/pendingRequests.ts`, `setup.ts`); end it with
  `await settled()` (`test/interactions.ts`) or the text the last answer renders. To find at-risk tests, delay every
  answer temporarily inside the wrapper. → report 018b, "A2".
- **System events leave the API through an allowlist** (`SystemEventDetails`): a new event type needs a constant in a
  `…Events` class and an entry, or a unit test fails; never `lastError` / `reason`. → README "System events".
- **Alerts are written only by `AlertStore`**, one SQL statement inside the transaction of the cause; a new path that
  changes stock or a threshold must call `ReconcileLowStockAsync` before commit. → README "Alerts"; report 017.
- **The low-stock rule exists twice**: `InventoryPredicates.IsLowStock` (LINQ) and `IsLowStockSql` (SQL text), pinned by
  a test. → report 017.
- **Every dialog `Form` ends with `<ResetFormOnOpen openCount={useOpenCount(…)}>`** and has `clearOnDestroy`. → log "Task 018 result".
- **Radio groups / `Segmented` need an explicit `name`.** → log "Task 018 result".
- **Cowork's git:** run `git --no-optional-locks status` from the Cowork VM; a plain `git status` there leaves `.git/index.lock` behind.
- **Waits in frontend tests:** use the fake clock (`useJumpableClock` / `jump` in `orderEntry.test.tsx`), not real pauses.
- **`Assert-Step` is strict** (type and value; strings case-sensitive; arrays/objects throw). → `scripts/smoke/_common.ps1`.
- **Smoke scripts run npm after stopping the hosts they started** (`Invoke-FrontendChecks`). → report 017.
- **A script that starts hosts and leaves them running must not have its output piped**: the hosts inherit the pipe and
  the caller hangs. Redirect to a file. → report 018b (browser check).
- **The dev server runs React StrictMode; tests and production builds do not.** Check dialogs/effects in
  `npm run build` + `vite preview`. → report 016b.
- **`data` vs `currentData` in RTK Query**; **hints invalidated through a throttle**. → report 016b.
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result", "Task 006 plan".
- **Every `SaveChanges` that stores an order, a movement or a task status sends one `pg_notify`.** → report 015.
- **The hub takes the token from the query string**; never log URLs of `/api/hubs/*`. → review 015.
- **A running API or Workers process locks `bin/Debug`**; stop them before building. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only; run helper scripts from the repository root; with
  `$ErrorActionPreference = 'Stop'` stderr of `docker` / `dotnet` is a terminating error (`cmd /c "… 2>&1"`).
- **Git rewrites files with CRLF** on checkout / stash pop.
- **Migrations needing a smoke run before the user applies them:** Claude Code uses a throwaway PostgreSQL/RabbitMQ
  environment (generated credentials, roles from `docker/postgres/init`). → report 017, "Smoke table".

## 9. Next action
**Cowork:** review task 018b ([report](tasks/018b-logs-audit.report.md), commits `b84ef98`..`5b8fab0`), answer its five
questions, update the roadmap and the log, then write task 019 — which first needs the **user's** decision on S3 in
development (real AWS bucket with dev credentials, or a local S3-compatible emulator).

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 018b (docs/tasks/018b-logs-audit.report.md, các commit b84ef98..5b8fab0), trả lời 5 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 019 (cần quyết định trước: S3 cho môi trường dev).
```

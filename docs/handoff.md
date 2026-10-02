# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-02 (+07:00), by Cowork after review 015.
- Branch `master` (nothing pushed). HEAD: the Cowork commit "docs: review 015, task 016" on top of `a8c0cfd`
  ("task 015: report").
- Working tree: clean after that commit.
- Newest migration: `20261002053501_StockSync` (task 014; applied to the development database by the user). Task 015
  added none.

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
- 001 — Build, test, `InitialCreate` migration — Done
- 002 — Channel / Customer / Product CRUD, inventory list, integration-test infrastructure — Done
- 003 — Inventory import, movement history, `inventory.changed` outbox events — Done
- 004 — Order intake `POST /api/orders/{slug}` (outbox first) — Done
- 005 — RabbitMQ topology, message envelope, Outbox Publisher — Done
- 006 — Order Worker: atomic reservation, attempt tracking — Done
- 007 — Retry/backoff and DLQ for all consumers — Done
- 008 — Orders and processing read API, manual retry, audit events — Done
- 009 — User management (Admin), token revocation — Done
- 010 — Frontend scaffold: auth, layout, bilingual UI, Users page — Done
- 011 — Frontend: Products, Customers, Channels pages — Done
- 012 — Frontend: inventory list, stock import, movement history — Done
- 013 — Frontend: order entry, orders list/detail — Done
- 014 — ChannelProduct mapping API + Stock Sync Worker (simulated channels, Polly); expected 23505 not logged as errors — Done
- 015 — Realtime hints (PostgreSQL `NOTIFY` → SignalR hub) + `GET /api/dashboard/summary`; Part A from review 014 — Done
- 016 — Frontend: realtime client, dashboard, Sync & Processing page, order entry by push; Part A from reviews 013–015 — **Open**
- 016b — Frontend: channel product mapping UI on the Channels page (split from 016, user 2026-10-02) — Planned
- 017 — Alerts (low stock, processing failures), `GET /api/alerts` — Planned
- 018 — Frontend: Alerts page, Logs/Audit page — Planned

## 5. In flight
**Task 016: task file written, not started.** Nothing is half-finished and nothing is uncommitted.
- Task: [docs/tasks/016-realtime-dashboard-and-processing-page.md](tasks/016-realtime-dashboard-and-processing-page.md);
  its design decisions are in the implementation log, "Task 016 plan" (read first; stop and report `Blocked` for
  anything not settled there).
- Part A (separate commits): A1 smoke scripts start hosts only for the local default (review 015); A2 pickers hide only
  stale answers (`currentData`); A3 panel title repetition; A4 orders table at 1280 px; A5 load only the active language
  (all review 013); A6 committed bundle-measurement script (review 014).
- Part B (backend, small): `productSku` on task summary and detail. Parts C–E: realtime client (`@microsoft/signalr`,
  lazy), dashboard, Sync & Processing list + detail route + retry. `scripts/smoke/task-016.ps1` and a real-browser check
  (WebSockets through the Vite proxy) required.
- Task 015 as delivered: [report](tasks/015-realtime-hints-and-dashboard-summary.report.md) — commits `55993e4`,
  `47ff45a`, `a41165a`, `f74251b`, `d15281e`, `945e9a8`, `a8c0cfd`; backend 745/745 (505 unit, 240 integration), frontend
  untouched; [review](tasks/015-realtime-hints-and-dashboard-summary.review.md): accepted, all 10 deviations accepted, no
  P0–P2.
- Split tasks use a letter suffix (`016b` after `016`, before `017`) — `docs/tasks/README.md`, `/next-task`.

## 6. Pending for the user
- Nothing open from 014 and 015: the user confirmed (2026-10-02) the 014 migration on the development database and the
  015 smoke run and manual check (the smoke table of the development run was not pasted into a file).
- After task 016: its smoke script and the short manual check (two tabs, an order, the dashboard updates) — see the
  task's acceptance criteria; the report will list them.
- Test data in the development database: `SMOKE-014-*` / `SMOKE-015-*` products, smoke customers, locked users
  `smoke-01x-*` from the smoke runs, plus everything listed in reports 011–013.
- Open decisions (roadmap, "Decisions still needed"): before 017 — where alerts are stored; before 019 — S3 in
  development; before 024 — where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5,
  §6; stock sync and the channel simulator: README §7. Frontend: `cd frontend; npm run dev` (README "Frontend").
- Ports: API 5080, frontend dev server 5173 (proxies `/api`), PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (alternative for another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (integration tests need Docker); `npm test` in `frontend/` (no API needed).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.

## 8. Gotchas learned
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → implementation log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order**; every stock change writes a movement and an outbox row in the
  same transaction. → implementation log, "Task 003 result", "Task 006 plan".
- **EF Core's `CommandError` log entry carries no exception**; the level of a database failure is decided in
  `DatabaseFailureLogInterceptor`, and a unique violation is only "expected" when its constraint is in
  `ExpectedUniqueViolations` (`IsUniqueViolation` never matches other names). → report 014, "A1"; log, "Task 014 result".
- **Stock sync pushes the current stock, never the message snapshot**, and holds no lock during a channel call; a new
  `StockSync` task needs channel and product (CHECK constraint — tests that seed "another task type" use `Invoice`).
  → report 014; `StockSyncMessageHandler.cs`.
- **Directed messages**: `OutboxEvent.RoutingKey` = queue name; `inventory.changed` fans out to three queues. Manual retry
  of a stock-sync task must not copy the fanned-out event. → log, "Task 014 plan".
- **Every test Workers host now runs the Stock Sync Worker**: a test that inspects the `stock.sync.*` queues removes it
  (`RemoveHostedService<StockSyncWorkerService>`). → report 014, deviation 3.
- **Failing integration tests wait for 60 s timeouts**: run mutations against one targeted test each. → report 014.
- **A running API or Workers process locks `bin/Debug`**; start them one after the other, stop them before building.
  → report 008; report 013.
- **Every `SaveChanges` that stores an order, a movement or a task status sends one `pg_notify`** (`RealtimeHintInterceptor`, API and Workers); a set-based `ExecuteUpdate`/raw SQL write of those tables would bypass it. Each API instance holds one `LISTEN` connection (`application_name` `omnichannel-hint-listener`). → report 015.
- **The hub takes the token from the query string**; `AccessTokenRedactionEnricher` keeps it out of the API log. Never log URLs of `/api/hubs/*` elsewhere (client logging at Warning, proxies). → review 015.
- **Smoke scripts start an API with the development user-secrets** when the configured URL is unreachable — until 016 Part A1 restricts that to the local default. → report 015, "Incident".
- **Workers must run in their own terminal**; migrations run with the migrator role, never on start-up. → README §4, §6.
- **PowerShell 5.1:** scripts are ASCII only; an `if` that returns a one-element array is unrolled — wrap it in `@( )`.
  `Assert-Step` compares with `-eq` (ignores case). → `scripts/smoke/_common.ps1`, `task-014.ps1`.
- **`git checkout` rewrites files with CRLF** (autocrlf) while tools write LF: multi-line search/replace must handle both.
- **Access tokens are rejected when the security stamp changes**; login is limited to 10 attempts per minute and IP.
  → implementation log, "Frontend decisions"; report 009.
- **Tests with mocks miss integration defects**: every frontend task found real defects only in the browser, and the
  014 smoke run found a script bug only against real hosts. Keep the real run. → reports 010–014.
- **Frontend:** server-search pickers must not offer options of an older search; an API error on a `Form.List` is not
  cleared by re-validation; RTK `setupListeners` / `resetApiState` and the "focused" flag; fake only `setTimeout`,
  `clearTimeout`, `Date`; at most 4 test workers; the first-load set (not the main chunk) is the bundle metric, and its
  measuring script is not committed yet (016 Part A). → reports 012, 013; review 014.
- **Claude Code's Bash tool:** heredocs containing quotes or apostrophes can fail to parse. Write helper scripts to a
  file with the Write tool and run the file.

## 9. Next action
**Claude Code:** `/next-task` → task 016 (Part A first, separate commits; then Part B backend, Parts C–E; report with
smoke table, bundle numbers and browser check). Then **the user:** smoke script and manual check of 016 if Claude Code
cannot run them; then **Cowork:** review 016 and write task 016b (channel product mapping UI; Part A from review 016).

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: task 015 đã được review và chấp nhận; task 016 (frontend: realtime client, dashboard, trang Sync & Processing, order entry qua push; Part A từ review 013–015) đã có file task và đang chờ Claude Code chạy /next-task. Khi có docs/tasks/016-realtime-dashboard-and-processing-page.report.md thì review task 016, cập nhật roadmap và implementation log, rồi viết task 016b (frontend: mapping sản phẩm theo kênh trên trang Channels; Part A từ review 016).
```

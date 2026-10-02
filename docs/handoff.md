# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-02 14:15 (+07:00).
- Branch `master` (nothing pushed). HEAD `ca8c2aa` — "docs: review 014, task 015".
- Working tree: clean.
- Newest migration: `20261002053501_StockSync` (task 014). Review 014 records that the user applied it to the development
  database; check with `dotnet ef migrations list` if in doubt.

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
- 015 — Realtime hints (PostgreSQL `NOTIFY` → SignalR hub) + `GET /api/dashboard/summary`; Part A from review 014 — **Open**
- 016 — Frontend: dashboard, realtime, Sync & Processing page, channel product mappings — Planned
- 017 — Alerts (low stock, processing failures), `GET /api/alerts` — Planned
- 018 — Frontend: Alerts page, Logs/Audit page — Planned

## 5. In flight
**Task 015: task file written, not started.** Nothing is half-finished and nothing is uncommitted.
- Task: [docs/tasks/015-realtime-hints-and-dashboard-summary.md](tasks/015-realtime-hints-and-dashboard-summary.md);
  its design decisions are in the implementation log, "Task 015 plan" (read first; stop and report `Blocked` for anything
  not settled there).
- Part A (separate commits, from [review 014](tasks/014-channel-product-mapping-and-stock-sync.review.md)):
  1. **P2:** a stock-sync task can stay `RetryScheduled` / `Pending` forever when its mapping or channel is deactivated
     while a retry copy waits — in `StockSyncService.ResolveInventoryChangeAsync` look up the task by idempotency key
     *before* the channel/mapping check (same for the final-failure path); tests + mutation;
  2. `GET /api/channels/{channelId}/products/{channelProductId}` (the 201 `Location` of 014 must resolve);
  3. README section 6: remove "`order.processing` has no consumer yet";
  4. `Omnichannel.Api.http`: samples for the mapping endpoints and the two new endpoints.
- Part B: `pg_notify` in the committing transaction → one `LISTEN` connection per API instance → hub
  `/api/hubs/notifications`. Part C: dashboard summary. No migration expected; frontend untouched;
  `scripts/smoke/task-015.ps1` required.
- Task 014 as delivered: [report](tasks/014-channel-product-mapping-and-stock-sync.report.md) — commits `6b62354`,
  `bc68cc7`, `4d06813`, `0c612a4`, `2a6ddfc`; backend 669/669 (459 unit, 210 integration), frontend 168/168; review:
  accepted, all 16 deviations accepted.
- From the last Claude Code chat, not in any repository file: nothing that matters. The throwaway PostgreSQL / RabbitMQ
  containers used for the 014 smoke run were removed; only the compose containers remain.

## 6. Pending for the user
- Nothing open from 014: review 014 records migration, smoke run and the manual checks of 012, 013 and 014 as done
  (the smoke table of the development run was not pasted into a file).
- After task 015: its smoke script and the short manual check (hint batch in the API log) — see the task's acceptance
  criteria; the report will list them.
- API and Workers were **not** started on the development environment by Claude Code in task 014.
- Test data in the development database: `SMOKE-014-*` products, `Smoke customer 014-*`, locked users `smoke-014-*`
  from the user's smoke run, plus everything listed in reports 011–013.
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
**Claude Code:** `/next-task` → task 015 (Part A first, separate commits; then Parts B and C; report with the smoke table).
Then **the user:** smoke script and manual check of 015 if Claude Code cannot run them; then **Cowork:** review 015 and
write task 016.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: task 014 đã được review và chấp nhận; task 015 (realtime hints qua PostgreSQL NOTIFY → SignalR hub + GET /api/dashboard/summary, Part A từ review 014) đã có file task và đang chờ Claude Code chạy /next-task. Khi có docs/tasks/015-realtime-hints-and-dashboard-summary.report.md thì review task 015, cập nhật roadmap và implementation log, rồi viết task 016 (frontend: dashboard, realtime, trang Sync & Processing, mapping sản phẩm theo kênh; Part A từ review 013 và 014).
```

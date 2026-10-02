# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-02 (+07:00), by Cowork, after review 016 and writing task 016b.
- Branch `master` (nothing pushed). HEAD: the Cowork commit "docs: review 016, task 016b" (on top of `70772aa`).
- Working tree: clean after that commit. Hosts: as the user left them.
- Newest migration: `20261002053501_StockSync` (task 014; applied to the development database by the user). Tasks
  015, 016 added none; 016b adds none.

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
- 001–016 — Done (titles in [docs/roadmap.md](roadmap.md); 016 = realtime client, dashboard, Sync & Processing page,
  order entry by push; [review](tasks/016-realtime-dashboard-and-processing-page.review.md) accepted, no P0–P2).
- 016b — Frontend: channel detail with product mappings, `stockSyncEnabled` on channels; Part A from review 016 —
  **Open** ([task file](tasks/016b-channel-product-mappings-ui.md))
- 017 — Alerts (low stock, processing failures), `GET /api/alerts` — Planned (decision needed first: where alerts are
  stored)
- 018 — Frontend: Alerts page, Logs/Audit page — Planned

## 5. In flight
**Task 016b: written, not started.** `/next-task` in Claude Code picks it (lowest-numbered task without a report).
- Part A (review 016): A1 `127.0.0.1` instead of `localhost` for local connections (README, design-time factory,
  Workers RabbitMQ host) — the user then changes the user-secrets; A2 hint flush becomes a throttle (flush at once when
  idle, ≤ 1 per second); A3 `OrderOutcome` invalidates lists only when the connection is not live.
- Part B: `stockSyncEnabled` on channel responses (from `RabbitMqTopology.StockSyncQueueOf`, set after the query).
- Parts C, D: stock-sync column on the channels list, route `/channels/:channelId` with the mapping list, create/edit
  dialogs, sync-result alert with the task's live status.

## 6. Pending for the user
- After 016b A1 is merged (or now): set `Host=127.0.0.1` instead of `Host=localhost` in `ConnectionStrings:Default` of
  both user-secrets and in `OMNICHANNEL_MIGRATOR_CONNECTION` (review 016, question 1). Secrets are the user's.
- Test data in the development database: `SMOKE-016-*` and `SMOKE-016-UI-*` products and customers, four locked
  `smoke-016-*` users, two inactive website mappings — plus everything listed in earlier reports.
- Open decisions (roadmap, "Decisions still needed"): before 017 — where alerts are stored; before 019 — S3 in
  development; before 024 — where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5,
  §6; stock sync and the channel simulator: README §7. Frontend: `cd frontend; npm run dev` (README "Frontend").
- Ports: API 5080, frontend dev server 5173 (proxies `/api`, WebSockets included), PostgreSQL 5432, RabbitMQ 5672,
  RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (alternative for another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (integration tests need Docker); `npm test` in `frontend/` (no API needed, about
  3 minutes); `npm run measure` after `npm run build` for the first-load set.
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.

## 8. Gotchas learned
- **A new database connection costs about 2 s on this machine**: `localhost` resolves to `::1` first, PostgreSQL is
  published on `127.0.0.1` only, and the refused IPv6 connect takes 2075 ms (measured). It shows up as a 2 s request or
  a late Order Worker whenever a pool opens a connection. → report 016, question 1.
- **Realtime client:** the SignalR library sits behind `features/realtime/hub.ts`; its own automatic reconnect is not
  used (`realtimeConnection.ts` owns the retries); tests use `src/test/hub.ts` (`renderApp` defaults to a hub that never
  connects, so pages poll as before). Hints are invalidated at most once per second. → report 016; README "Frontend".
- **RTK Query removes an invalidated cache entry that has no subscriber** instead of refetching it, and delays an
  invalidation while any request is pending. → report 016, "Summary".
- **Only the language in use is loaded**: switch with `changeLanguage` of `shared/i18n`, never `i18n.changeLanguage`
  directly; editing a translation file reloads every open dev tab. → report 016 (A5).
- **A hidden browser tab pauses CSS animations and throttles timers**: an AntD dialog looks "still open" and timings are
  longer than in a visible tab. Measure in the fronted tab. → this chat's browser check.
- **Smoke scripts start an API / Workers host only for the local default**; with `SMOKE_BASE_URL` set they must already
  run. → `scripts/smoke/_common.ps1` (016 A1).
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → implementation log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order**; every stock change writes a movement and an outbox row in the
  same transaction. → implementation log, "Task 003 result", "Task 006 plan".
- **Every `SaveChanges` that stores an order, a movement or a task status sends one `pg_notify`**; a set-based
  `ExecuteUpdate` / raw SQL write of those tables would bypass it. → report 015.
- **The hub takes the token from the query string**; it is redacted in the API log and in the client's log lines. Never
  log URLs of `/api/hubs/*` elsewhere. → review 015; `signalrHub.ts`.
- **Directed messages** (`OutboxEvent.RoutingKey` = queue name) and **stock sync pushes the current stock**, never the
  message snapshot. → implementation log, "Task 014 plan".
- **A running API or Workers process locks `bin/Debug`**; stop them before building. Failing integration tests wait for
  60 s timeouts. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only (a non-ASCII path inside a script is misread — derive it from the current
  directory); an `if` that returns a one-element array is unrolled — wrap it in `@( )`. → `scripts/smoke/_common.ps1`.
- **`git checkout -- <file>` discards uncommitted edits and rewrites the file with CRLF**: commit before a mutation
  check, or restore the mutated line by hand.
- **Tests with mocks miss integration defects**: the 016 browser run again found four defects the tests could not see.
  Keep the real run. → reports 010–016.
- **Cowork's shell on the user's VM cannot delete files by default**: a plain `git status` there refreshes the index and
  leaves `.git/index.lock` behind. Run read-only git commands with `GIT_OPTIONAL_LOCKS=0`. The VM has no npm registry
  access and the Windows `node_modules` do not run there, so Cowork cannot run the frontend tests. → review 016.
- **Claude Code's Bash tool:** heredocs containing quotes or apostrophes can fail to parse. Write helper scripts to a
  file with the Write tool and run the file.

## 9. Next action
**Claude Code (new chat):** `/next-task` → task 016b. **Then Cowork:** review 016b, update roadmap and implementation
log, settle the decision "where are alerts stored" with the user, write task 017. The user can change the user-secrets
host at any time.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 016b (frontend: trang chi tiết kênh với mapping sản phẩm, stockSyncEnabled; Part A từ review 016: 127.0.0.1, flush hint dạng throttle, OrderOutcome chỉ invalidate khi không live) theo report docs/tasks/016b-channel-product-mappings-ui.report.md, cập nhật roadmap và implementation log, chốt với tôi quyết định "lưu alert ở đâu" (bảng Alert mới hay chỉ SystemEventLog), rồi viết task 017.
```

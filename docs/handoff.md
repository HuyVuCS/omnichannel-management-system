# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-02 22:57 (+07:00), by Claude Code, at the end of the chat that implemented task 016.
- Branch `master` (nothing pushed). HEAD `d4f7db5` — "task 016: report".
- Working tree: clean. No host is running (API, Workers, frontend dev server stopped); PostgreSQL and RabbitMQ containers
  were left running.
- Newest migration: `20261002053501_StockSync` (task 014; applied to the development database by the user). Tasks 015
  and 016 added none.

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
- 001–015 — Done (titles in [docs/roadmap.md](roadmap.md); 015 = realtime hints + `GET /api/dashboard/summary`).
- 016 — Frontend: realtime client, dashboard, Sync & Processing page, order entry by push; Part A from reviews 013–015 —
  **Report written, status "Partially done"** (the roadmap table still says Open)
- 016b — Frontend: channel product mapping UI on the Channels page — Planned
- 017 — Alerts (low stock, processing failures), `GET /api/alerts` — Planned
- 018 — Frontend: Alerts page, Logs/Audit page — Planned

## 5. In flight
**Task 016: implemented and committed, verification not finished** (the chat hit its usage limit during the browser
check). Read the [report](tasks/016-realtime-dashboard-and-processing-page.report.md) first; the
[task file](tasks/016-realtime-dashboard-and-processing-page.md) has the acceptance criteria.
- Commits: `d97b3ef` (A1), `c46e249` (A2), `cd78522` (A3), `78a71b8` (A4), `5cbe8c3` (A6), `c075867` (A5), `6101f5f`
  (Part B), `5de07dd` (Part C), `c6ee3dd` (Parts D, E), `4669e20` (smoke script), `054a42d` (browser fixes), `d4f7db5` (report).
- Verified at `c6ee3dd`: backend build 0 warnings, 746/746; frontend 243/243, lint and typecheck clean; smoke
  `task-016.ps1` 23/23 on the development environment.
- **Left to do** (report, "Not done"):
  1. `npm test` and `npm run lint` in `frontend/` — not re-run after `054a42d` (column widths in `DashboardPage.tsx` /
     `ProcessingPage.tsx`, label "failed publish attempts" in both translation files, one assertion in
     `processingTask.test.tsx`).
  2. Browser: order entry with Workers stopped for more than 60 s ("still processing", then "Check again").
  3. Browser: orders list at 1280 px in vi and en (A4 — widths are checked by a test only).
  4. Browser: language-switch measurement of the task detail page.
  5. A1, Workers branch (`SMOKE_BASE_URL` set, API reachable, no Workers host) was not run.
  Then set the report's status and add the results. `/next-task` will **not** pick 016 again (it has a report).
- Open questions for Cowork (report, "Blocked / questions"): the 2-second cost of every new database connection (see
  section 8); whether "dashboard within ~2 s" needs a leading flush; lists refetched twice per finished order in push mode.
- The browser check used helper scripts in the chat's scratch folder (not in the repository): they started the API and
  Workers from the build output and created smoke users through `scripts/smoke/_common.ps1`. A new chat writes its own.

## 6. Pending for the user
- `cd frontend; npm ci` — task 016 added `@microsoft/signalr`.
- Manual check of 016: two tabs (dashboard, order entry), submit an order, the dashboard's order count moves without
  reload. Optionally `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-016.ps1` (passed 23/23 in this chat).
- Recommended: set `Host=127.0.0.1` instead of `Host=localhost` in `ConnectionStrings:Default` of both user-secrets
  (section 8, first bullet). Not done by Claude Code: secrets are the user's.
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
- **Claude Code's Bash tool:** heredocs containing quotes or apostrophes can fail to parse. Write helper scripts to a
  file with the Write tool and run the file.

## 9. Next action
**Claude Code (new chat):** finish the verification of task 016 — the five items of section 5 "Left to do" — and update
`docs/tasks/016-realtime-dashboard-and-processing-page.report.md` (status, results; fix what the checks find). Then
**Cowork:** review 016, update roadmap and implementation log, write task 016b. The user can do the manual check and
the `Host=127.0.0.1` change at any time.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: task 016 (frontend: realtime client, dashboard, trang Sync & Processing, order entry qua push; Part A từ review 013–015) đã được cài đặt và commit, report ở docs/tasks/016-realtime-dashboard-and-processing-page.report.md có trạng thái "Partially done" vì phần kiểm tra trên trình duyệt chưa xong (xem docs/handoff.md mục 5). Sau khi Claude Code hoàn tất phần kiểm tra còn lại và cập nhật report, hãy review task 016 (kể cả 3 câu hỏi trong report: kết nối database mới mất ~2 giây do localhost/IPv6, ngân sách độ trễ ~2 giây của dashboard, danh sách bị refetch hai lần ở chế độ push), cập nhật roadmap và implementation log, rồi viết task 016b (frontend: mapping sản phẩm theo kênh trên trang Channels; Part A từ review 016).
```

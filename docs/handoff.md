# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-03 01:20 (+07:00), by Claude Code, after implementing task 016b.
- Branch `master` (nothing pushed). HEAD: `62d5eea` "task 016b: report" (this file is committed on top of it).
- Working tree: clean. No API, Workers host or dev server is left running; the PostgreSQL and RabbitMQ containers are up.
- Newest migration: `20261002053501_StockSync` (task 014; applied to the development database by the user). Tasks
  015, 016, 016b added none.

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
- 001–016 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 016b — Frontend: channel detail with product mappings, `stockSyncEnabled` on channels; Part A from review 016 —
  **Review** ([report](tasks/016b-channel-product-mappings-ui.report.md); the roadmap still says Open until Cowork
  reviews it)
- 017 — Alerts (low stock, processing failures), `GET /api/alerts` — Planned (decision needed first: where alerts are
  stored)
- 018 — Frontend: Alerts page, Logs/Audit page — Planned
- 019 — S3 product images (presigned upload) — Planned (decision needed first: S3 in development)

## 5. In flight
**Task 016b: report written, awaiting Cowork review.** Commits `6fb1896` (A1), `4d67b5f` (A2), `253edbf` (A3),
`f279fac` (Part B), `e6f9eb7` (form dialogs), `af72fbf` (Parts C, D), `37c5f44` (browser finding), `6ebefe9` (smoke
script), `62d5eea` (report). Nothing is half-finished. What the review has to look at (all in the report):
- **Deviation 1, outside the literal scope:** a pre-existing defect of every form dialog was fixed. In a production
  build a dialog opened for another record after a full close showed the previous record's values; the dev server hid
  it (React StrictMode). `useResetFormOnOpen` now leaves an unmounted form alone and every dialog `Form` has
  `clearOnDestroy`; regression tests for products, customers, channels, users.
- **Question 1:** the frontend suite (281 tests) is flaky under its own load: 3 of 5 full runs passed, 2 had 20 s
  timeouts in long existing tests (`processing.test.tsx`). `maxWorkers` was not changed (log: fixed at 4 until 025).
- **Question 2:** should every frontend browser check include one pass on `vite preview` (no StrictMode)?
- Questions 3, 4 and the follow-ups (stale `data` of RTK Query hooks elsewhere, API errors on rule-less fields of
  `CustomerFormModal`, products table width at 1280 px, `Assert-Step … $true` in older smoke scripts).
- Existing tests whose assertions changed because A2 / A3 changed the behaviour they pinned (deviations 2–4).
- Not measured in the browser: the Admin view of the channels list (action column).

## 6. Pending for the user
- **A1:** set `Host=127.0.0.1` instead of `Host=localhost` in `ConnectionStrings:Default` of both user-secrets and in
  `OMNICHANNEL_MIGRATOR_CONNECTION` (commands with placeholders: report 016b, "Manual steps"). Until then every new
  database connection still costs about 2 s.
- Restart the API and the Workers host (new build: `stockSyncEnabled`).
- Manual check of 016b: Manager → Channels → "Website" → "Gắn sản phẩm" → smoke product, external SKU `WEB-…` → the
  alert goes from "Đang chờ" to "Thành công" without a reload; set the mapping to "Ngừng" afterwards.
- Test data left in the development database by 016b: products `SMOKE-016B-UI-24463-A/B/C` and two `SMOKE-016B-<n>`,
  six locked `smoke-016b-*` users, four Inactive website mappings, one message in `stock.sync.website.dlq` — plus
  everything listed in earlier reports.
- Open decisions (roadmap, "Decisions still needed"): before 017 — where alerts are stored; before 019 — S3 in
  development; before 024 — where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5,
  §6; stock sync and the channel simulator: README §7. Frontend: `cd frontend; npm run dev` (README "Frontend").
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (alternative for another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (752; integration tests need Docker); `npm test` in `frontend/` (281, no API
  needed, about 4 minutes); `npm run measure` after `npm run build` for the first-load set (968.23 kB raw).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.

## 8. Gotchas learned
- **The dev server runs React StrictMode; tests and production builds do not.** StrictMode's double mount hid the dialog
  defect. Check mount/unmount, form and effect behaviour once in `npm run build` + `vite preview`. → report 016b,
  deviation 1; README "Frontend" (form dialogs).
- **A dialog `Form` needs `clearOnDestroy` next to `useResetFormOnOpen`**, and a field without a client rule keeps its
  API error until it is removed by hand before the next submit. → `useResetFormOnOpen.ts`, `ChannelProductFormModal.tsx`.
- **`data` of an RTK Query hook is the previous argument's result while the new one loads**; use `currentData` where
  a wrong value must never show. → report 016b, browser finding; `SyncResultAlert.tsx`.
- **Run the full frontend suite with nothing else running** (API, Workers, dev server, browser): it is memory-bound
  and long tests time out at 20 s. → report 016b, question 1.
- **A hidden browser pane gets no animation frames**: AntD dialogs stay stuck mid-animation and their mask blocks
  clicks; a screenshot advances a frame. Read results from the DOM. → report 016b, "Real-browser check".
- **Hints are invalidated through a throttle** (at once when idle, otherwise at the end of the second); the connect
  invalidation only refetches pages that are already mounted. → `hintTags.ts`; report 016b, deviation 2.
- **Local connections use `127.0.0.1`**, never `localhost` (IPv6 attempt costs ~2 s). → README §3; log "Task 016 result".
- **PowerShell: `$true -eq 'any text'` is true** — compare flags as text in smoke scripts. → `task-016b.ps1`.
- **Realtime client**, **RTK Query invalidation rules**, **language loading**: → report 016; log "Task 016 result".
- **Smoke scripts start an API / Workers host only for the local default.** → `scripts/smoke/_common.ps1`.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order**; every stock change writes a movement and an outbox row in the
  same transaction. → log, "Task 003 result", "Task 006 plan".
- **Every `SaveChanges` that stores an order, a movement or a task status sends one `pg_notify`**; set-based writes of
  those tables would bypass it. → report 015.
- **The hub takes the token from the query string**; never log URLs of `/api/hubs/*`. → review 015.
- **A running API or Workers process locks `bin/Debug`**; stop them before building. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only; derive non-ASCII paths from the current directory (run helper scripts
  from the repository root); wrap an `if` that returns an array in `@( )`. → `scripts/smoke/_common.ps1`.
- **`git checkout -- <file>` discards uncommitted edits and rewrites the file with CRLF.**
- **Cowork's shell on the user's VM:** run read-only git with `GIT_OPTIONAL_LOCKS=0`; it cannot run the frontend
  tests. → review 016.
- **Claude Code's Bash tool:** heredocs containing apostrophes or backslashes can fail or change the text. Write helper
  scripts with the Write tool and run the file.

## 9. Next action
**Cowork:** review task 016b (report + commits above), decide the report's questions 1 and 2, update the roadmap and the
implementation log, settle "where are alerts stored" with the user, write task 017. **The user** can do the A1
user-secrets change and the manual check at any time. **Claude Code:** nothing until 017 exists.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 016b theo report docs/tasks/016b-channel-product-mappings-ui.report.md (chú ý deviation 1: sửa lỗi dialog form hiện dữ liệu bản ghi trước ở bản production, ngoài phạm vi task; câu hỏi 1: bộ test frontend chập chờn khi chạy đủ; câu hỏi 2: có kiểm tra thêm trên vite preview không), cập nhật roadmap và implementation log, chốt với tôi quyết định "lưu alert ở đâu" (bảng Alert mới hay chỉ SystemEventLog), rồi viết task 017.
```

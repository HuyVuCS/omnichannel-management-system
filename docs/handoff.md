# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-03 (+07:00), by Cowork, after review 016b.
- Branch `master` (nothing pushed). HEAD: the Cowork commit "docs: review 016b" (on top of `b5f533f`).
- Working tree: clean after that commit.
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
- 001–016b — Done (titles in [docs/roadmap.md](roadmap.md); 016b
  [review](tasks/016b-channel-product-mappings-ui.review.md) accepted: no P0–P1 in the new code, a pre-existing P1
  form-dialog defect fixed, one P2 — frontend suite unstable under load — goes to 017 Part A).
- 017 — Alerts (low stock, processing failures), `GET /api/alerts`; Part A from review 016b — Planned, **blocked on the
  user's decision: where alerts are stored** (new `Alert` table with open/acknowledged, or `SystemEventLog` only)
- 018 — Frontend: Alerts page, Logs/Audit page; Part A from review 016b — Planned
- 019 — S3 product images (presigned upload) — Planned (decision needed first: S3 in development)

## 5. In flight
**Nothing is being implemented.** Task 017 is not written yet: Cowork writes it once the user has decided where alerts
are stored. Its Part A (review 016b) is fixed already:
- A1 frontend suite stable under load: smoke scripts run `npm run build` / `npm test` after stopping the hosts they
  started; measure `maxWorkers` 3 vs 4; split tests that take > 5 s alone; `testTimeout` stays 20 s; acceptance 5
  consecutive green full runs with PostgreSQL and RabbitMQ running;
- A2 `Assert-Step` compares type and value (older scripts compare `$true` with non-boolean values).
The user's smoke run of 016b (2026-10-03): API checks green, `npm test` red in `stockImport.test.tsx` (output not
pasted; assumed the same timeout class).

## 6. Pending for the user
- **Decision before 017:** where alerts are stored (new `Alert` table with open/acknowledged · `SystemEventLog` only).
- **A1 of 016b:** `Host=127.0.0.1` instead of `Host=localhost` in `ConnectionStrings:Default` of both user-secrets and in
  `OMNICHANNEL_MIGRATOR_CONNECTION` (commands: report 016b "Manual steps", or the in-place PowerShell snippet from the
  Cowork chat of 2026-10-02). Until then every new database connection costs about 2 s.
- Manual check of 016b: Manager → Channels → "Website" → "Gắn sản phẩm" → smoke product, external SKU `WEB-…` → the
  alert goes from "Đang chờ" to "Thành công" without a reload; set the mapping to "Ngừng" afterwards.
- Test data left in the development database by 016b: products `SMOKE-016B-UI-24463-A/B/C` and two `SMOKE-016B-<n>`,
  six locked `smoke-016b-*` users, four Inactive website mappings, one message in `stock.sync.website.dlq` — plus
  everything listed in earlier reports.
- Other open decisions (roadmap): before 019 — S3 in development; before 024 — where "payment succeeded" comes from.

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
**User:** decide where alerts are stored. **Then Cowork:** write task 017 (alerts + Part A from review 016b).
**Claude Code:** nothing until 017 exists. The user can do the `127.0.0.1` change and the manual check at any time.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Review 016b đã xong. Việc tiếp theo: chốt với tôi quyết định "lưu alert ở đâu" (bảng Alert mới có open/acknowledged hay chỉ SystemEventLog), rồi viết task 017 (alerts backend; Part A từ review 016b: bộ test frontend ổn định khi chạy cùng smoke, Assert-Step so sánh chặt).
```

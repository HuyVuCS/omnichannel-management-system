# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-02 05:15 (+07:00).
- Branch `master` (nothing pushed). HEAD `f80a7b8` — "task 013: report".
- Working tree: clean.
- Newest migration: `20260930130908_ProcessingTaskAttemptBudget` (tasks 009–013 added none).

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs; SignalR only notifies the UI.
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
- 011 — Frontend: Products, Customers, Channels pages; AntD 6, localised messages, `isLowStock`, code splitting — Done
- 012 — Frontend: inventory list, stock import, movement history — Done (user's manual check not recorded as confirmed)
- 013 — Frontend: order entry, orders list/detail, order links in the movement history — **Review**
  (the roadmap still says `Open`; Cowork updates it with the review)
- 014 — ChannelProduct mapping + Stock Sync Worker; Part A: expected 23505 conflicts not logged as errors — Planned
- 015 — SignalR hub, worker → API notifications, dashboard summary — Planned
- 016 — Frontend: dashboard, realtime, Sync & Processing page — Planned

## 5. In flight
**Task 013: report written, awaiting Cowork review.** Nothing is half-finished and nothing is uncommitted.
- Report: [docs/tasks/013-frontend-order-entry-and-orders.report.md](tasks/013-frontend-order-entry-and-orders.report.md).
- Commits: `bb61ea5` (A2 deterministic expiry test), `4398d43` (A3 inventory API out of the main chunk), `44d501e` (A1
  picker labels + shared search hook), `abcb192` (pickers offer no option while a search is pending), `2fa3d5a` (the three
  pages, `CustomerSelect`, processing slice, smoke script, README), `f80a7b8` (report).
- Last verified: frontend typecheck, lint, 167/167 tests in 17 files, build (main chunk 254.46 kB, first-load set
  978.50 kB); `scripts/smoke/task-013.ps1` 31/31; real-browser check with API and Workers; 38/38 mutations caught.
  Backend untouched (not built or tested in this task).
- **For Cowork to confirm** (report, "Deviations" and "Blocked / questions"):
  - item 1: tab-visibility tracking is a custom `setupListeners` handler in `frontend/src/app/tabVisibility.ts`, mounted
    from `App.tsx` (the plain call registers once per page and its flag is reset by `resetApiState` on logout);
  - item 2: a finished task invalidates the order *list* tag only (otherwise the order is fetched twice);
  - item 3: `useListQueryState` got `defaultSortDir`;
  - item 4: one existing movement test was changed (it asserted that an order reference is not a link);
  - item 5: both pickers offer no option while a search is pending (behaviour change of the 012 `ProductSelect`);
  - question: which size to track — the main chunk is unstable (bundler regroups shared code); recommendation: the
    first-load set now, pinned vendor chunks with 025.
- Follow-ups for a Part A (report, "Out of scope / follow-ups"): the stock-import form keeps a stale list-level error after
  the next submit (one line + test); translations of both languages are loaded up front; the orders table is wider than
  the content area at 1280 px.

## 6. Pending for the user
- Manual check of task 013 (report, "Manual steps for the user"): start API, then Workers, then `npm run dev`; one order
  per panel, duplicate, conflict, insufficient stock, Workers stopped → "Kiểm tra lại", list filters after a reload,
  detail → movements → back, Enter in a picker right after typing.
- Manual check of task 012 (report 012) if not done yet: review 012 lists it as pending.
- API and Workers are **stopped** (Claude Code started them for the checks and stopped them again).
- Test data left in the development database: products `SMOKE-013-*`, `SMOKE-013-UI-A/B/C-99189`; customers
  `Smoke customer 013-*`, `Smoke UI customer 013-99189`; orders `SMOKE-013-WEB-*`, `SMOKE-013-POS-*`, `WEB-20261002-0M2G3R`,
  `DP-20261002-10XR08`, `DP-20261002-EKWBCU`, `POS-20261002-WK2RH1`; locked users `smoke-013-*`; plus everything listed in
  reports 011 and 012. The locked test Admin `ui-check-61563` was not used.
- Open decisions (roadmap, "Decisions still needed"):
  - before 014: real or simulated channel APIs;
  - before 015: how Workers trigger SignalR pushes;
  - before 017: where alerts are stored;
  - before 019: S3 in development;
  - before 024: where "payment succeeded" comes from.

## 7. Environment
- Start infrastructure with docker compose, then three terminals: API, Workers, scripts/commands. Commands: README §1, §5, §6.
  Frontend: `cd frontend; npm run dev` (README "Frontend").
- Ports: API 5080, frontend dev server 5173 (proxies `/api` to the API), PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672.
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the smoke
    scripts;
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`.
- Tests: `dotnet test` in `backend/` (integration tests need Docker); `npm test` in `frontend/` (no API needed).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10.

## 8. Gotchas learned
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → implementation log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order**; every stock change writes a movement and an outbox row in the
  same transaction. → implementation log, "Task 003 result", "Task 006 plan".
- **A running API or Workers process locks `bin/Debug`**, and starting both with `dotnet run` at the same moment makes one
  build fail: start one after the other. → report 008; report 013, "Out of scope / follow-ups".
- **Workers must run in their own terminal**; migrations run with the migrator role, never on start-up. → README §4, §6.
- **PowerShell 5.1 misreads UTF-8 scripts without a BOM**, and the repository path contains non-ASCII characters. Smoke
  scripts are ASCII only and resolve paths from `$PSScriptRoot`. → `docs/tasks/README.md`; `scripts/smoke/_common.ps1`.
- **`Assert-Step` compares with `-eq` (ignores case)**; **login is limited to 10 attempts per minute and IP.**
  → `scripts/smoke/task-012.ps1`, `task-010.ps1`.
- **Access tokens are rejected when the security stamp changes**; a temporary lockout does not end sessions.
  → implementation log, "Frontend decisions"; report 009.
- **Tests with mocks miss integration defects.** Every frontend task found real defects only in the browser (013: a picker
  chose the wrong product on Enter; a table 7 px too wide). Keep the real-browser check. → reports 010–013.
- **Server-search pickers must not offer options of an older search** (Enter picks the first one).
  → `frontend/src/shared/hooks/useServerSearchSelect.ts`; report 013, "Real-browser check".
- **Native `title` tooltips never appear in automated screenshots**: check the attribute in the DOM. → report 013, "A1".
- **RTK Query `setupListeners`:** the default handler registers once per page load, and `resetApiState` resets the
  "focused" flag to its value at page load. → `frontend/src/app/tabVisibility.ts`; report 013, deviation 1.
- **RTK invalidation is delayed while a query is pending**: invalidating a tag of a request that is running fetches it
  again. → report 013, deviation 2.
- **An API error set on a `Form.List` is not cleared by re-validation** (the list has no rule): clear it on submit.
  → `OrderEntryForm.tsx`; report 013.
- **The main chunk size is not a stable metric** (rolldown regroups shared vendor code); use the first-load set.
  → report 013, "A3".
- **Fake timers in frontend tests:** fake only `setTimeout`, `clearTimeout`, `Date` with `shouldAdvanceTime`; AntD and
  user-event need running timers. → `auth.test.tsx`, `orderEntry.test.tsx`.
- **The frontend suite is memory-bound**: at most 4 workers. → report 012, deviation 3; `frontend/vite.config.ts`.
- **FluentValidation 12:** `{PropertyName}` per validation, `{ComparisonProperty}` once per rule. → report 012, "A2".
- **AntD 6:** `Modal.destroyAll()` also closes hook confirms; `modal.confirm` keeps its opening texts; closed dropdowns stay
  in the DOM; animations never finish in jsdom; form dialogs must reset on open. → reports 011, 012;
  `frontend/src/test/catalog.ts`, `renderApp.tsx`.
- **Language switch changes text only**: fixed widths from `frontend/src/shared/components/layout.ts`; measure VI/EN/VI in
  the browser. → implementation log, "Frontend decisions"; reports 012, 013.
- **Claude Code's Bash tool:** backticks inside `node -e "…"` are executed by the shell, and heredocs with apostrophes can
  fail to parse. Write helper scripts to a file (quoted heredoc or the Write tool) first.

## 9. Next action
**Cowork:** review task 013 (report + commits in section 5), confirm or reject the five deviations and answer the
chunk-metric question, update `docs/roadmap.md` and `docs/implementation-log.md`, settle the 014 decision (real or
simulated channel APIs) with the user, and write task 014 (ChannelProduct mapping + Stock Sync Worker; Part A: EF Core
logging of expected 23505 conflicts, the stale list error in the stock-import form).
In parallel **the user:** start API and Workers and do the manual check of task 013.
Then **Claude Code:** `/next-task`.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 013 (docs/tasks/013-frontend-order-entry-and-orders.report.md, các commit bb61ea5, 4398d43, 44d501e, abcb192, 2fa3d5a), xác nhận hoặc bác năm điểm lệch nêu ở mục 5 của handoff (theo dõi trạng thái hiển thị của tab bằng handler riêng, chỉ invalidate danh sách đơn hàng, tuỳ chọn defaultSortDir của useListQueryState, sửa một test biến động tồn kho, picker không hiện lựa chọn khi đang tìm), trả lời câu hỏi về chỉ số kích thước bundle (main chunk hay first-load set), cập nhật roadmap và implementation log, chốt quyết định cho 014 (API kênh thật hay giả lập), rồi viết task 014 (ChannelProduct mapping + Stock Sync Worker; Part A: EF Core không ghi log lỗi cho xung đột 23505 đã lường trước, lỗi danh sách còn sót ở form nhập kho).
```

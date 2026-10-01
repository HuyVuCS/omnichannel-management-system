# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-01 21:20 (+07:00).
- Branch `master` (nothing pushed). HEAD `50eacdd` — "task 012: report".
- Working tree: clean.
- Newest migration: `20260930130908_ProcessingTaskAttemptBudget` (tasks 009–012 added none).

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
- 012 — Frontend: inventory list, stock import, movement history; camelCase error keys, field names, route error page — **Review**
  (the roadmap still says `Open`; Cowork updates it with the review)
- 013 — Frontend: order entry + orders list/detail, order links in the movement history — Planned
- 014 — ChannelProduct mapping + Stock Sync Worker — Planned
- 015 — SignalR hub, worker → API notifications, dashboard summary — Planned

## 5. In flight
**Task 012: report written, awaiting Cowork review.** Nothing is half-finished and nothing is uncommitted.
- Report: [docs/tasks/012-frontend-inventory-pages.report.md](tasks/012-frontend-inventory-pages.report.md).
- Commits: `a5a3b11` (A1 camelCase keys), `7be93f4` (A2 field names), `f3b7050` (`createdByEmail`), `5c63867` (A3 route error
  page), `95d42dc` (A4 dialogs closed on session end), `e49eb87` (A5 search box), `884aac8` (the three pages, product picker,
  smoke script, README), `50eacdd` (report).
- Last verified: backend build 0 warnings, 528/528 tests (360 unit, 168 integration); frontend typecheck, lint, 120/120
  tests, build (main chunk 223 kB); `scripts/smoke/task-012.ps1` 24/24; real-browser check done.
- **For Cowork to confirm** (report, "Deviations"):
  - item 1: A2 changed one line in `IdentityUserService`, outside the files the task listed;
  - item 3: frontend tests are capped at 4 workers in `frontend/vite.config.ts` (the suite was flaky with 12 while the API ran);
  - item 4: the import confirmation is a `Modal` of the page, not `modal.confirm`;
  - item 6: extra cache rules (product changes invalidate the inventory list; stock lists refetch on open).
- No blocking question. Follow-ups are listed in the report ("Out of scope / follow-ups").

## 6. Pending for the user
- Restart the API so it runs the task-012 code. No migration, no new packages.
- Manual check of task 012 (report, "Manual steps for the user"): low-stock switch on Inventory, a two-line import with
  confirmation and result, the same document again, movement filters after a reload, `VI | EN`, the pages as Staff.
- Test data left in the development database: products `SMOKE-012-*`, imports `SMOKE-012-GRN-*`, `UI/UI-012-GRN-1`,
  `UI-012-GRN-2`; locked users `smoke-012-*` (three Managers from smoke runs), `smoke-012-ui-69399`,
  `smoke-012-ui-staff-69399`; plus everything listed in report 011. The locked test Admin `ui-check-61563` was not used.
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
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** Consumers count failures themselves and use
  the delay tiers. → implementation log, "Task 007 result"; review 007.
- **Inventory rows are always locked in `ProductId` order**, and every stock change writes a movement and an outbox row in the
  same transaction. → implementation log, "Task 003 result", "Task 006 plan".
- **A running API or Workers process locks `bin/Debug`.** Stop them before `/next-task`, or build with
  `--artifacts-path`; `dotnet ef` works with `--configuration Release`. → report 008; review 008.
- **Workers must run in their own terminal**; migrations run with the migrator role, never on start-up. → README §4, §6.
- **PowerShell 5.1 misreads UTF-8 scripts without a BOM**, and the repository path contains non-ASCII characters. Smoke scripts
  are ASCII only and resolve paths from `$PSScriptRoot`. → `docs/tasks/README.md`, "Smoke scripts"; `scripts/smoke/_common.ps1`.
- **`Assert-Step` compares with `-eq`, which ignores case.** Check exact casing with `-ceq` and assert `$true`.
  → `scripts/smoke/task-012.ps1`.
- **Login is limited to 10 attempts per minute and IP.** Smoke scripts stay below it; wait a minute between runs.
  → `scripts/smoke/task-010.ps1`.
- **Access tokens are rejected when the user's security stamp changes**; a temporary lockout does not end sessions. Test
  tokens must belong to real users. → implementation log, "Frontend decisions"; report 009.
- **Tests with mocks miss integration defects.** Every frontend task found real defects only in the browser against the real
  API (012: buttons that moved on a language switch, fields 1.6 px apart). Keep the real-browser check.
  → reports 010–012, "Real-browser check".
- **The frontend suite is memory-bound, not CPU-bound**: one jsdom per test file; with the API and Docker running, more than
  4 parallel workers time out on this machine. → report 012, "Deviations" item 3; `frontend/vite.config.ts`.
- **FluentValidation 12:** `{PropertyName}` is resolved per validation, `{ComparisonProperty}` once when the rule is built.
  A message naming a second field passes it as an argument. → report 012, "A2"; `FieldNames.cs`.
- **AntD 6:** `Modal.destroyAll()` also closes `App.useApp().modal` confirms; a `modal.confirm` keeps the texts it was opened
  with (use a page `Modal` when it must follow the language). → report 012, "A4" and "Deviations" item 4.
- **Closed AntD dropdowns stay in the DOM**, in jsdom and in the browser: a test that looks up an option by text must search
  the open dropdown only. → `frontend/src/test/catalog.ts` (`chooseProduct`).
- **AntD animations never finish in jsdom.** Tests switch them off, except the "reopened before closed" tests.
  → `frontend/src/test/renderApp.tsx`; report 011.
- **Form dialogs must reset on open** (`useResetFormOnOpen`). → report 011; README "Frontend".
- **Language switch changes text only**: fixed widths from `frontend/src/shared/components/layout.ts`, also for buttons whose
  text differs per language. → implementation log, "Frontend decisions"; report 012.
- **The Vite dev server can miss a file written several times within a moment** (it kept serving the intermediate version).
  Touch the file or reload before measuring in the browser.
- **In Claude Code's Bash tool, heredocs containing apostrophes can fail to parse.** Write helper scripts to a file first.

## 9. Next action
**Cowork:** review task 012 (report + commits above), confirm or reject the four deviations in section 5, update
`docs/roadmap.md` and `docs/implementation-log.md`, and write task 013 (order entry + orders list/detail, reusing
`ProductSelect`; order links in the movement history).
In parallel **the user:** restart the API and do the manual check of task 012.
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
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 012 (docs/tasks/012-frontend-inventory-pages.report.md, các commit a5a3b11, 7be93f4, f3b7050, 5c63867, 95d42dc, e49eb87, 884aac8), xác nhận hoặc bác bốn điểm lệch nêu ở mục 5 của handoff (một dòng sửa trong IdentityUserService, giới hạn 4 worker cho test frontend, hộp xác nhận nhập kho là Modal của trang, quy tắc cache bổ sung), cập nhật roadmap và implementation log, rồi viết task 013 (trang nhập đơn hàng + danh sách/chi tiết đơn hàng, dùng lại ProductSelect; liên kết đơn hàng trong lịch sử biến động).
```

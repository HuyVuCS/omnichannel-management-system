# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-01 19:31 (+07:00).
- Branch `master` (nothing pushed). HEAD `04df2f8` — "task 011: report (correct two counts)".
- Working tree: clean.
- Newest migration: `20260930130908_ProcessingTaskAttemptBudget` (tasks 009–011 added none).

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
- 011 — Frontend: Products, Customers, Channels pages; AntD 6, localised messages, `isLowStock`, code splitting — **Review**
  (the roadmap still says `Open`; Cowork updates it with the review)
- 012 — Frontend: inventory list, stock import, movement history — Planned
- 013 — Frontend: order entry + orders list/detail — Planned
- 014 — ChannelProduct mapping + Stock Sync Worker — Planned

## 5. In flight
**Task 011: report written, awaiting Cowork review.** Nothing is half-finished and nothing is uncommitted.
- Report: [docs/tasks/011-frontend-catalog-pages.report.md](tasks/011-frontend-catalog-pages.report.md).
- Commits: `e595411` (`isLowStock`), `ead627a` (localised validation messages), `18cead6` (AntD 6),
  `29ab04e` (code splitting + the three pages + smoke script + README), `ffa513d` / `04df2f8` (report).
- Last verified: backend build 0 warnings, 479/479 tests (326 unit, 153 integration), 3 runs; frontend typecheck, lint,
  77/77 tests, build (main chunk 153 kB); `scripts/smoke/task-011.ps1` 16/16.
- **Question for Cowork** (report, "Blocked / questions"): validation error keys of query-string parameters are PascalCase
  (`"To"`, `"SortBy"`) while the convention is camelCase. Not changed in 011 because the task said keys stay unchanged.
  Recommendation: camelCase all keys in `ValidationProblemResponse`.
- Follow-ups listed in the report: technical property names inside messages (`'low Stock Threshold'`), an open
  confirmation dialog stays over the login page after logout, optional chunk tuning.

## 6. Pending for the user
- Restart the API so it runs the task-011 code. No migration to apply.
- In `frontend/` run `npm ci` once (AntD 6 replaced AntD 5).
- Manual check of task 011 (report, "Manual steps for the user"): create and edit a product (try the SKU `bad sku!` first),
  create a customer with a long address, deactivate a test channel, look at the pages as Staff, switch `VI | EN` with a
  dialog open.
- Test data left in the development database by the checks: products `SMOKE-011-*`, customer `Smoke 011 UI 56579`, channel
  `smoke-011-ui-53092` (Inactive), locked users `ui-check-61563` (Admin), `ui-check-staff-1`, `smoke-011-ui-68707`,
  `smoke-010-*`, `smoke-staff*`.
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
  are ASCII only and take paths as parameters. → `docs/tasks/README.md`, "Smoke scripts"; `scripts/smoke/_common.ps1`.
- **Login is limited to 10 attempts per minute and IP.** Smoke scripts stay below it; wait a minute between runs.
  → `scripts/smoke/task-010.ps1`.
- **Access tokens are rejected when the user's security stamp changes** (role change, admin lock, password reset); a temporary
  lockout does not end sessions. Test tokens must belong to real users. → implementation log, "Frontend decisions"; report 009.
- **Tests with mocks miss integration defects.** Every frontend task so far found real defects only in the browser against the
  real API (English Identity messages, dialog height, stale form values on reopen). Keep the real-browser check.
  → reports 010 and 011, "Real-browser check".
- **AntD animations never finish in jsdom or in a hidden browser pane.** Tests switch them off, except the "reopened before
  closed" tests, which rely on it. → `frontend/src/test/renderApp.tsx`; report 011.
- **Form dialogs must reset on open** (`useResetFormOnOpen`); `destroyOnHidden` alone leaves the previous record's values.
  → report 011; README "Frontend".
- **Language switch changes text only**: fixed widths from `frontend/src/shared/components/layout.ts`, nothing keyed by
  language. → implementation log, "Frontend decisions"; task 010.
- **Query-string validation keys are PascalCase** (open question above). → report 011.
- **In Claude Code's Bash tool, heredocs containing apostrophes can fail to parse.** Write helper scripts to a file first.

## 9. Next action
**Cowork:** review task 011 (report + commits above), decide the query-string key question, update `docs/roadmap.md` and
`docs/implementation-log.md`, and write task 012 (inventory list, stock import, movement history pages).
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
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 011 (docs/tasks/011-frontend-catalog-pages.report.md, các commit e595411, ead627a, 18cead6, 29ab04e), quyết định câu hỏi về key lỗi validation của query string (PascalCase hay camelCase), cập nhật roadmap và implementation log, rồi viết task 012 (trang tồn kho, nhập kho, lịch sử biến động).
```

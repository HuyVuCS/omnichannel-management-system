# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-05 00:57 (+07:00), by Claude Code, right after finishing task 019.
- Branch `master` (nothing pushed). HEAD: `b5ebe79` "task 019: report" (this file is committed on top of it).
- Working tree: clean. No API, Workers host, preview server or throwaway container is left running by Claude Code;
  the development PostgreSQL and RabbitMQ containers are up. The development **object storage emulator is not
  running yet** (it needs the user's key helper first, section 6).
- Newest migration: **`20261004074312_ProductImage`** (task 019) — generated, applied only on a throwaway
  environment; **not applied to development**. Before it: `20261002215102_Alerts` (applied).
- `docs/roadmap.md` and `docs/implementation-log.md` still show 019 as `Open` / planned: Cowork updates them in the
  review.

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs (simulated); SignalR only notifies the UI; product
images live in S3-compatible object storage (no AWS).
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
- 001–018b — Done (titles in [docs/roadmap.md](roadmap.md)).
- 019 — Product images in S3-compatible object storage + Part A (latency switch, `reasonCode` of rejected messages) —
  **Review**: [task](tasks/019-product-images-object-storage.md), [report](tasks/019-product-images-object-storage.report.md).
- 020 — Excel export (ClosedXML), background export task + object storage for large files — Planned
- 021 — Reports & Excel export page — Planned
- 022 — Reconciliation Worker — Planned

## 5. In flight
**Task 019: report written, awaiting Cowork review.** Nothing half-finished; everything of this chat is committed:
`451b8cd` A1, `426b8d9` A2, `281108c` backend + emulator service + migration + key helper, `1b85ce2` Cowork's
documents of the round, `35608d6` frontend + smoke script + README, `b5ebe79` report.
Verified: backend 1056/1056, 0 warnings; frontend 405 tests, 5 of 5 full runs green, `npm run test:slow` green; smoke
50/50 on a **throwaway** environment; real-browser check on the production build (found and fixed a visible native
file input). Emulator chosen by the spike: **SeaweedFS 4.48** (Garage also met the criteria).
Questions the review must answer (report, "Blocked / questions for Cowork"):
1. One `ObjectStorage:ServiceUrl` serves both the API and the browsers; a containerised / deployed API may need a
   separate public endpoint for presigning (025?).
2. The client's early size check is the constant 5 MiB while `MaxImageBytes` is configuration — accept or expose it?
3. `npm run test:slow` is far more sensitive than the normal run but not fully deterministic (two tests were red in
   one of three runs each) — is one green run the rule?
4. A failed object delete logs the product id and the object key (not secret) — acceptable?
5. The storage `PUT` shows `200 OK` plus a "cancelled" mark in the browser pane's network list (body never read).
Doc deviations 19–21 are confirmed in the report.

## 6. Pending for the user
Required before product images work on development (report, "Manual steps for the user"), in this order:
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` — generates the storage key pair into
   `docker/.env` and the user-secrets of `Omnichannel.Api`; prints no key. (Until then `docker compose up` fails on
   the missing `OBJECT_STORAGE_*` variables, and the API fails its start-up validation of `ObjectStorage`.)
2. `docker compose -f docker/docker-compose.yml --env-file docker/.env up -d` — starts the emulator (first pull 724 MB).
3. Migration `ProductImage` as the migrator: `cd backend; dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api`.

Then optional: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-019.ps1` on development, and a pass with
real key presses / the real file dialog in the product edit dialog (the two things the browser check could not do).
- Still unconfirmed from 016b: `Host=127.0.0.1` in both user-secrets and in `OMNICHANNEL_MIGRATOR_CONNECTION`.
- Open decisions (roadmap): before 024 — where "payment succeeded" comes from; before 025 — hosting and the
  production object-storage provider.

## 7. Environment
- Start infrastructure with docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals:
  API, Workers, scripts/commands. Commands: README §1, §5, §6; stock sync: README §7; images: README "Product images".
  Frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672, object storage S3 API 8333 (all on `127.0.0.1`).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID`,
    `OBJECT_STORAGE_SECRET_ACCESS_KEY`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`
    (the Workers host has no object storage until 020);
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`, read by the
    smoke scripts (another environment: `SMOKE_BASE_URL`, `SMOKE_ADMIN_EMAIL`, `SMOKE_ADMIN_PASSWORD`);
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (environment variable) — for `dotnet ef database update`; not available to Claude Code.
- Tests: `dotnet test` in `backend/` (1056; integration tests need Docker and pull `chrislusf/seaweedfs:4.48` on first
  use, about 1.5 min); `npm test` in `frontend/` (405, 4–5 minutes, run with nothing else running);
  `npm run test:slow` (same suite, every answer 120 ms late); `npm run measure` after `npm run build` (first-load set
  970 kB raw).
- Node 22.12 or later (`frontend/.nvmrc` pins 24); .NET SDK 10. Workers run in Development for the simulator's SKU rules.
- Test accounts on development: smoke users are locked after each run; the test Admin `ui-check-61563` is locked.

## 8. Gotchas learned
- **Product images:** the API never receives image bytes; nothing is trusted until confirm checks the stored object;
  no storage call inside a transaction; listing products makes no storage call; links and keys are never logged.
  → README "Product images"; report 019 "Design notes".
- **jsdom applies no library CSS:** AntD's `.ant-form input[type="file"] { display: block }` beats the `hidden`
  attribute; hide with an inline style and look at the production build. → report 019, browser check.
- **Any frontend task that adds tests with requests runs `npm run test:slow`**; a test that counts requests starts on
  `await settled(store)`. → README "Frontend" (Tests); report 019 A1.
- **A rejected worker message carries a `reasonCode`** (`MessageRejectionCodes`); a new rejection path needs a
  constant, a case in `MessageRejectionTests` and a label in vi/en. → README "System events".
- **A frontend test that ends with a request in flight fails**; end with `await settled()`. → report 018b, "A2".
- **System events leave the API through an allowlist** (`SystemEventDetails`); never `lastError` / `reason`. → README "System events".
- **Alerts are written only by `AlertStore`**, inside the transaction of the cause; stock/threshold changes must call
  `ReconcileLowStockAsync`. → README "Alerts"; report 017.
- **Every dialog `Form` ends with `<ResetFormOnOpen openCount={useOpenCount(…)}>`** and has `clearOnDestroy`;
  **radio groups / `Segmented` need an explicit `name`.** → log "Task 018 result".
- **The dev server runs React StrictMode; tests and production builds do not.** Check dialogs/effects in
  `npm run build` + `vite preview`. → report 016b.
- **Cowork's git:** run `git --no-optional-locks status` from the Cowork VM (a plain `git status` leaves `.git/index.lock`).
- **`Assert-Step` is strict**; **smoke scripts run npm after stopping the hosts they started.** → `scripts/smoke/_common.ps1`; report 017.
- **A script that starts hosts and leaves them running must not have its output piped** (redirect to a file). → report 018b.
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result", "Task 006 plan".
- **The hub takes the token from the query string**; never log URLs of `/api/hubs/*`. → review 015.
- **A running API or Workers process locks `bin/Debug`**; stop them before building. → reports 008, 014.
- **PowerShell 5.1:** scripts are ASCII only; stderr of `docker` / `dotnet` is a terminating error under
  `$ErrorActionPreference = 'Stop'` (`cmd /c "… 2>&1"`); text piped to a native command needs
  `$OutputEncoding = ASCII` (a BOM is not JSON). → `scripts/setup-object-storage.ps1`.
- **Git rewrites files with CRLF** on checkout / stash pop; a multi-line raw string in an EF configuration would put
  the file's line endings into the model (keep constraint SQL on one line). → `ProductConfiguration.cs`.
- **Migrations needing a smoke run before the user applies them:** Claude Code uses a throwaway environment
  (PostgreSQL, RabbitMQ, and the emulator through the compose service under another project name). → report 019, "Smoke script".

## 9. Next action
**User:** the three manual steps of section 6 (key helper, `docker compose up -d`, migration `ProductImage`).
**Cowork (in parallel):** review task 019 ([report](tasks/019-product-images-object-storage.report.md), commits
`451b8cd`..`b5ebe79`), answer its five questions, update the roadmap and the log, then write task 020.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 019 (docs/tasks/019-product-images-object-storage.report.md, các commit 451b8cd..b5ebe79), trả lời 5 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 020 (Excel export).
```

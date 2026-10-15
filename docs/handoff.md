# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-15 14:10 (+07:00), by Claude Code, after review 027.
- Branch `master`, pushed to `origin` (`HuyVuCS/omnichannel-management-system`, public). HEAD: `b6413c2` "docs: review
  027 - roadmap complete; acceptance criteria quoted verbatim" (this file is committed on top). Working tree clean.
- Branch `production` is at `3b27e91` — what the live frontend and the live stack run (image tag `3b27e91c6daa`,
  runtime .NET 10.0.13; rollback images `:9712cf1f7544` kept).
- Newest migration: **`20261014062556_LastStockSyncIndex`** — applied to development and production (17).
- Nothing of Claude Code is running. Running on the machine: the development containers (`omnichannel`) and the
  production stack (`omnichannel-prod`).

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs, invoice provider and mail server (all simulated);
SignalR only notifies the UI; product images and export files live in S3-compatible object storage.
Source-of-truth order when documents disagree:
1. `CLAUDE.md`
2. Architecture doc `THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx` (in the Cowork project, not in the repository)
3. `docs/implementation-log.md` (decisions and deviations)
4. `docs/roadmap.md` (plan, open decisions)

## 3. Workflow
- **Cowork** plans and reviews: writes `docs/tasks/NNN-*.md` and `NNN-*.review.md`, updates the roadmap and the log.
- **Claude Code** implements: `/next-task` picks the lowest-numbered task without a report, commits on `master`,
  writes `NNN-*.report.md`, and **applies the development steps itself**.
- **Production** holds real (demo) state and has **no backup**: a production step only when a task or the user asks,
  by [docs/runbook.md](runbook.md); a request to production — also a read-only one — needs a sentence to the user first.
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md).

## 4. Progress
- 001–027 (with 016b, 018b, 024b, 025b, 025d, 026b, 026c) — **Done** (titles in [docs/roadmap.md](roadmap.md)).
  027 was the last planned task: **the roadmap is complete** (review 027).
- 025c — Oracle Always Free VM (optional, only if the user wants an always-on host) — Planned.

## 5. In flight
**Nothing.** Task 027 is reviewed and accepted ([task](tasks/027-acceptance-end-to-end.md),
[report](tasks/027-acceptance-end-to-end.report.md), [review](tasks/027-acceptance-end-to-end.review.md)); released
as `3b27e91`. All fifteen criteria of doc §16 are proven in [docs/acceptance.md](acceptance.md) (throwaway `Full`
106 / 106, e2e 5 × 8 / 8, CI job `acceptance`, live `NoDrills` 75 / 75 + e2e 8 / 8). By the user's rule the open
points of review 027 are known limitations, not new tasks. Nothing of the last chat is outside committed files.

## 6. Pending for the user
- Both items of review 027 "Remaining for the user" are done (checked 2026-10-15): the temporary variables
  `OMNICHANNEL_LIVE_ADMIN_EMAIL` / `_PASSWORD` are removed, the local .NET runtime is 10.0.13.
- No development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (never printed).
- Open decisions: none. Optional only: 025c (always-on host); the small CSS for checkbox / radio focus (review 027 q1).

## 7. Environment
- **Development:** docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals: API,
  Workers, scripts/commands. Commands: README §1, §5, §6; frontend: `cd frontend; npm run dev`.
- Ports (all `127.0.0.1`): API 5080, dev server 5173, `vite preview` 4173, PostgreSQL 5432, RabbitMQ 5672, RabbitMQ
  UI 15672, object storage 8333; production proxy 8080 (API) and 8081 (storage).
- **Production:** compose project `omnichannel-prod` (`docker/docker-compose.prod.yml`, 2 Workers); live origins and
  every procedure in [docs/runbook.md](runbook.md) (4 release / rollback, 5 migrations, 13 live values, 17 acceptance).
- **Acceptance:** README "Acceptance" — `scripts/smoke/task-027.ps1` (`-Mode Full` on a throwaway stack with
  `docker/docker-compose.acceptance.yml`, `-Mode NoDrills` on development / `-Live`), `npm run e2e` (Playwright).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_*`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:*` keys, `Webhooks:Channels:<slug>:Secrets:0`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`,
    `ObjectStorage:*` keys;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_*`, `WEBHOOK_SECRET_*`, read by the scripts;
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (user-level environment variable) — for `dotnet ef database update`;
  - `docker/.env.prod` (git-ignored) — every secret of the production stack and `IMAGE_TAG`; names in
    `docker/prod.env.example`. It holds **no** Admin password: the production Admin is only with the user (live runs:
    temporary user-level variables, runbook §17).
- Tests: `dotnet test` in `backend/` (1942, about 5.5 min; needs Docker); `npm test` in `frontend/` (546, about
  9.5 minutes, nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`; `npm run e2e`
  (needs `E2E_*` variables, README "Acceptance").

## 8. Gotchas learned
- **A push to `master` deploys nothing**; only `production` does. → runbook §4.
- **Downtime of a release starts at `run migrate` when a service definition changed; a changed Caddyfile needs
  `restart proxy`.** → runbook §4.
- **`docker-compose.acceptance.yml` is for throwaway stacks and CI only** (SKU rules on, 1 s / 2 s retries). → runbook §17.
- **With short retry delays the channel's circuit breaker opens during a DLQ drill** (`channel_circuit_open`); the
  drill waits 31 s. → report 027, deviation 5.
- **From this machine `*.ts.net` resolves to the tailnet address**, which Chromium blocks for the public Vercel page:
  live e2e needs `E2E_HOST_RESOLVER_RULES` with a public Funnel address. → README "Acceptance", report 027 dev. 7.
- **A new NuGet advisory fails CI's restore** (NU1903, warnings are errors) — e.g. CVE-2026-70159. → report 027, dev. 14.
- **Login limit 10 / min per address**: smoke and e2e wait 61 s on a 429. → report 026c, 027.
- **Through Funnel an oversize chunked body can be answered 502** (accepted for public origins only). → runbook §15.
- **Throwaway production-like stacks have their own `IMAGE_TAG`**, project name, ports and edge subnet. → runbook §4.
- **Docker Desktop does not come back by itself after Windows upgrades WSL**; throwaway ports below 49152. → runbook §11.
- **EF Core turns `OrderByDescending().Take(1)` on a correlated set into `ROW_NUMBER()`.** → report 026c, deviation 7.
- **Frontend tests:** axe-core after every test; e2e runs axe in a real browser with contrast. → README "Frontend",
  `frontend/src/test/accessibilityException.ts` (the one exception, shared).
- **Migrations as the migrator**: list before update, additive only, throwaway first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**; a running API / Workers process
  locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only; a single object has no `Count`; stderr of native tools is terminating under
  `Stop`. → reports 020, 025b, 027.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Nobody has to act.** The roadmap is complete. If the user wants more: **user** decides (025c always-on host, or a
new feature); **Cowork** then writes the task; **Claude Code** runs `/next-task`.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: roadmap đã hoàn thành (task 027 đã review và release 3b27e91); chờ người dùng quyết định có làm 025c (Oracle VM, tuỳ chọn) hay việc mới không, rồi viết task tương ứng.
```

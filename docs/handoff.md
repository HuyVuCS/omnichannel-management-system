# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-14 13:05 (+07:00), by Claude Code, after the release of task 026c.
- Branch `master`, pushed to `origin` (`HuyVuCS/omnichannel-management-system`, public). HEAD: `80dfbed`
  "docs: handoff snapshot" (this file is committed on top); the report of the release is `bcebe73`.
- Branch `production` is at `9712cf1` — what the live frontend and the live stack run (image tag `9712cf1f7544`;
  rollback images `:a980747a49fc` kept).
- Uncommitted: `docs/tasks/026b-database-performance-stale-tasks.review.md` (an edit by Cowork; not Claude Code's).
- Nothing of Claude Code is running. Running on the machine: the development containers (`omnichannel`) and the
  production stack (`omnichannel-prod`).
- Newest migration: **`20261013132359_TaskListOrderIndex`** — applied to development and production (16).

## 2. Project in 5 lines
Multi-channel order and inventory management: React dashboard → ASP.NET Core API → PostgreSQL (source of truth) →
transactional outbox → RabbitMQ → .NET Workers → channel APIs, invoice provider and mail server (all simulated);
SignalR only notifies the UI; product images and export files live in S3-compatible object storage (no AWS).
Source-of-truth order when documents disagree:
1. `CLAUDE.md`
2. Architecture doc `THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx` (in the Cowork project, not in the repository)
3. `docs/implementation-log.md` (decisions and deviations)
4. `docs/roadmap.md` (plan, open decisions)

## 3. Workflow
- **Cowork** plans and reviews: writes `docs/tasks/NNN-*.md` and `NNN-*.review.md`, updates the roadmap and the log.
- **Claude Code** implements: `/next-task` picks the lowest-numbered task without a report, commits on `master`,
  writes `NNN-*.report.md`, and **applies the development steps itself**.
- **Production** holds real state and has **no backup**: a production step only when a task or the user asks, by
  [docs/runbook.md](runbook.md); a request to production — also a read-only one — needs a sentence to the user first.
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps",
  "Production steps".

## 4. Progress
- 001–025, 025b, 025d, 026, 026b — Done (titles in [docs/roadmap.md](roadmap.md)).
- 025c — Oracle Always Free VM (optional) — Planned
- 026c — Follow-ups of 026b, object storage hygiene, required-field text, accessibility — **Review** (report status
  *Done*, released): [task](tasks/026c-storage-hygiene-forms-accessibility.md),
  [report](tasks/026c-storage-hygiene-forms-accessibility.report.md).
- 027 — End-to-end smoke tests against the acceptance criteria — Planned

## 5. In flight — task 026c
**Report written, released, awaiting Cowork review.** Everything built, measured and released is in the report;
Part G: count before the release 0 objects to delete (2 images in use), migration in 4 s, about 20 s of API downtime,
the first clean-up run deleted nothing, live `task-026.ps1` 5 / 5 and `task-025b.ps1` 4 / 5.
Open questions for Cowork (report, "Blocked / questions", 7): focus-ring contrast, the new colours, no `<h1>` per
page, a staging key reusable while its link lives, the `updatedAt` sort, no index on `Products.ImageKey`, and the
chunked oversize body that Funnel sometimes answers with 502 (`task-025b.ps1` steps 23–24 stay strict until decided).
Nothing of this chat is outside committed files.

## 6. Pending for the user
- Open the live dashboard once: the new colours (brand blue `#0958d9`, darker status / secondary / placeholder
  texts), the two product images on **Products**, one image upload.
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (never printed).
- Open decisions: none in the roadmap; the seven questions of report 026c are Cowork's.

## 7. Environment
- **Development:** docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals: API,
  Workers, scripts/commands. Commands: README §1, §5, §6; frontend: `cd frontend; npm run dev`.
- Ports (all `127.0.0.1`): API 5080, dev server 5173, `vite preview` 4173, PostgreSQL 5432, RabbitMQ 5672, RabbitMQ
  UI 15672, object storage 8333; production proxy 8080 (API) and 8081 (storage).
- **Production:** compose project `omnichannel-prod` (`docker/docker-compose.prod.yml`, 2 Workers); live origins and
  every procedure in [docs/runbook.md](runbook.md) (4 release / rollback, 5 migrations, 13 live values, 16 images).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID`,
    `OBJECT_STORAGE_SECRET_ACCESS_KEY`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`, `Webhooks:Channels:<slug>:Secrets:0`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`;
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`,
    `WEBHOOK_SECRET_WEBSITE`, `WEBHOOK_SECRET_POS`, `WEBHOOK_SECRET_DIGITAL_PLATFORM`, read by the scripts;
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (user-level environment variable) — for `dotnet ef database update`;
  - `docker/.env.prod` (git-ignored) — every secret of the production stack and `IMAGE_TAG`; names in
    `docker/prod.env.example`. The production Admin password is only with the user.
- Tests: `dotnet test` in `backend/` (1935, about 4.5 min; needs Docker); `npm test` in `frontend/` (526, about
  5.5 minutes, run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`.

## 8. Gotchas learned
- **A push to `master` deploys nothing**; only `production` does. → runbook "Release".
- **The downtime of a release starts at `run migrate` when a service definition changed; a changed Caddyfile needs
  `restart proxy`.** → runbook 4.
- **Through Funnel an oversize body can be answered 502 by the tunnel** (the proxy said 413). → runbook 15, report 026c q7.
- **Docker Desktop does not come back by itself after Windows upgrades WSL**: `wsl --status`, `docker desktop
  restart`. After a WSL restart Windows reserves other port ranges: throwaway ports below 49152. → runbook 11.
- **Throwaway production-like stacks have their own `IMAGE_TAG`**, project name, ports and edge subnet. → runbook 4.
- **The proxy's body limit is 8 × the API's**; change the two numbers together. → runbook 15.
- **An upload key is a staging key** (`uploads/products/...`); a product only ever refers to a copy under
  `products/...`; the clean-up deletes nothing younger than `MinAge` and nothing referenced. → runbook 16, report 026c.
- **The hardening smoke posts to the login endpoint** (10 a minute per address): it waits 61 s on a 429. → report 026c.
- **EF Core turns `OrderByDescending().Take(1)` on a correlated set into `ROW_NUMBER()` over the table.** → report
  026c, deviation 7.
- **Frontend tests:** axe-core runs after every test (serious / critical fail); wait for *each* field error and after
  a language switch (`findBy…`). → README "Frontend", report 026c A3.
- **Dark status seed colours make AntD derive grey tints**: the tints are set explicitly. → `layout.ts`.
- **A hidden Browser pane freezes CSS transitions** (colours, closing dialogs). → report 026c, C2.
- **Migrations as the migrator**: list before update, additive only, throwaway first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**; a running API / Workers process
  locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only; stderr of native tools is a terminating error under `Stop`. → reports 020, 025b.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Cowork:** review task 026c (report and diff `f363c23..bcebe73`), answer its seven questions, write the next task (027
or a follow-up). **User:** the look at the live dashboard (section 6).

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 026c (report docs/tasks/026c-storage-hygiene-forms-accessibility.report.md, đã release lên production 9712cf1), trả lời 7 câu hỏi trong report, rồi viết task tiếp theo (027 hoặc follow-up).
```

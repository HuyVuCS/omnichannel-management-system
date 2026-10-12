# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-12 21:58 (+07:00), by Claude Code, after finishing task 025b.
- Branch `master`, pushed to `origin` (`HuyVuCS/omnichannel-management-system`, public). HEAD: `e07dffb`
  "task 025b: report, runbook - reboot result" (this file is committed on top of it). Task 025b is `264270c`,
  `431c876`, `e07dffb`. Branch `production` is at `264270c` — the commit the live stack and the live frontend run.
- Working tree: clean. No development API / Workers host or throwaway stack is left running by Claude Code. Running
  on the machine: the development containers (`omnichannel`) and the **production stack** (`omnichannel-prod`).
- Newest migration: **`20261009133050_PaymentAndInvoices`** — applied to development and to production (14 migrations).
- `docs/roadmap.md` still shows 025b as open: Cowork updates the roadmap and the log in the review.

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
  writes `NNN-*.report.md`, and **applies the development steps itself** (key helper, changed containers, additive
  migrations as the migrator, smoke script on development).
- **Production** (since 025b) holds real state: a production step only when a task or the user asks, by
  [docs/runbook.md](runbook.md), after a backup. A push to `production` is a frontend release.
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps",
  "Production steps".

## 4. Progress
- 001–025 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 025b — Ops: go-live (public GitHub repository + CI, Vercel, Tailscale Funnel, backups, runbook) — **Review**
  (report status *Partially done*): [task](tasks/025b-go-live.md), [report](tasks/025b-go-live.report.md).
- 025c — Oracle Always Free VM (optional) — Planned
- 026 — Security and performance hardening — Planned
- 027 — End-to-end smoke tests against the acceptance criteria — Planned

## 5. In flight — task 025b
**Report written, awaiting Cowork review.** Nothing half-finished; everything is committed and pushed.
Live: frontend `https://omnichannel-management-system-front.vercel.app` (Vercel, branch `production`), API
`https://omni.tail70815b.ts.net`, storage `https://omni.tail70815b.ts.net:8443` (Tailscale Funnel → proxy on
`127.0.0.1:8080` / `:8081`). Verified: CI green on GitHub (5 jobs, plus one arm64 dispatch), `task-025.ps1` live
51 / 51, `task-025b.ps1` 12 / 12, Funnel measurement (client address and `https` forwarded, `Host` kept, hub over
WebSockets), browser and phone checks by the user, reboot test.
**Not done:** backups on production — the user skipped E4 (backup by hand, scheduled task) and E5 (restore drill of a
production backup) on 2026-10-12. The scripts were exercised on throwaway stacks only.
For the review (report, "Deviations" and "Blocked / questions for Cowork"):
1. Production without backups: accept for the demo, or a follow-up task?
2. The proxy forwards and logs `Tailscale-User-*` headers of tailnet devices: strip in 026?
3. The runbook's secret-rotation procedures are written but not exercised.
Notable deviations: 6 gitleaks findings (placeholders / test fixtures) accepted by the user before publication; the
SPA fallback excludes `/assets/`; images carry a revision label from `GIT_COMMIT`; `backup-prod.ps1` has no `-EnvFile`.

## 6. Pending for the user
- **Backups (skipped):** production has none. Runbook section 6: choose a destination that leaves the machine,
  `scripts\deploy\backup-prod.ps1 -Destination <folder>`, `register-backup-task.ps1 -At 03:00 -Destination <folder>`,
  then a restore drill (section 7). Required before the first release.
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (never printed).
- Open decisions: none in the roadmap; the three questions above are Cowork's to answer.

## 7. Environment
- **Development:** docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals: API,
  Workers, scripts/commands. Commands: README §1, §5, §6; frontend: `cd frontend; npm run dev`.
- Ports (all `127.0.0.1`): API 5080, dev server 5173, `vite preview` 4173, PostgreSQL 5432, RabbitMQ 5672, RabbitMQ
  UI 15672, object storage 8333; production proxy 8080 (API) and 8081 (storage).
- **Production:** compose project `omnichannel-prod` (`docker/docker-compose.prod.yml`, 2 Workers), every procedure in
  [docs/runbook.md](runbook.md) (start / stop, release, rollback, backup, restore, secrets, incidents, live values).
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
  - `docker/.env.prod` (git-ignored) — every secret of the production stack; names in `docker/prod.env.example`.
    `BOOTSTRAP_ADMIN_PASSWORD` is blank there since go-live; the production Admin password is only with the user.
  - GitHub CLI and Tailscale keep their own logins (`gh auth status`, `tailscale status`).
- Tests: `dotnet test` in `backend/` (1798, about 4 min; needs Docker); `npm test` in `frontend/` (500, 4–6 minutes,
  run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`. CI runs both on push.
- Left on production by the go-live smoke run: smoke products, one paid smoke order, a locked smoke account.

## 8. Gotchas learned
- **A push to `master` deploys nothing**; only `production` does (`frontend/vercel.json`). → runbook "Release".
- **Funnel forwards the client address and `https` and keeps `Host`**; the proxy trusts only the edge gateway, which
  is also every process on the machine. → runbook "Live values", "Client addresses".
- **Requests from this machine to the public names take the tailnet path**, not Funnel: only a phone on mobile data
  tests the public path. → report 025b.
- **Never `down -v` or `new-prod-env.ps1 -Force` on production.** → `docs/tasks/README.md` "Production steps".
- **Throwaway production-like stacks need their own project name, ports and edge subnet.** → runbook "Restore drill".
- **gitleaks reports test fixtures** (`generic-api-key`): six known findings. → report 025b.
- **Empty proxy lists mean "believe everyone" in ASP.NET Core**; nothing configured = headers not read. → report 025.
- **A presigned link is signed for its host**: storage has its own origin. → README "Production deployment".
- **Payment never changes `Order.Status` or stock**; invoice and e-mail are independent tasks. → README §10.
- **Webhooks:** the signature covers the raw bytes; identity `webhook:{slug}` has no role. → README §9.
- **Migrations as the migrator**: list before update, additive only, throwaway first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**; a running API / Workers process
  locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only; stderr of native tools is a terminating error under `Stop`; a function
  returning a one-element array hands back a string. → reports 020, 025b (`scripts/deploy/_stack.ps1`).
- **Frontend:** tests end with `await settled()`; full runs need a quiet machine. → README "Frontend", report 024b.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Cowork:** review task 025b (report + commits `264270c`, `431c876`, `e07dffb`), answer the three questions, update
the roadmap and the implementation log, then write the next task (026, or a backup follow-up first).
**User:** set up backups (section 6). **Claude Code:** `/next-task` once the next task exists.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 025b (docs/tasks/025b-go-live.report.md, các commit 264270c, 431c876, e07dffb; trạng thái Partially done vì bỏ qua backup production), trả lời 3 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task tiếp theo (026 hoặc task bổ sung backup trước).
```

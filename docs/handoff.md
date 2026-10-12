# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-12 23:55 (+07:00), by Claude Code, in the middle of task 026 (Parts A–E done, Part F waiting).
- Branch `master`, pushed to `origin` (`HuyVuCS/omnichannel-management-system`, public). HEAD: `294cb91`
  "task 026: report (Parts A-E; the release waits for the user)" (this file is committed on top of it). Task 026 is
  `c8f31d7` (code; CI run 38278736237 green) and `294cb91` (report); `0fc116b` is Cowork's review 025d / task 026.
- Branch `production` is at `264270c` — what the live frontend and the live stack still run (images `:local`).
- Working tree: clean. No host, preview server or throwaway stack of Claude Code is running. Running on the machine:
  the development containers (`omnichannel`) and the **production stack** (`omnichannel-prod`), untouched by 026.
- Newest migration: **`20261009133050_PaymentAndInvoices`** — applied to development and production (14). 026 has none.

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
- **Production** holds real state and has **no backup** (user, 2026-10-12): a production step only when a task or the
  user asks, by [docs/runbook.md](runbook.md); never `down -v`, volume removal or `-Force` over `docker/.env.prod`.
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps",
  "Production steps".

## 4. Progress
- 001–025, 025b, 025d — Done (titles in [docs/roadmap.md](roadmap.md)).
- 025c — Oracle Always Free VM (optional) — Planned
- 026 — Security hardening + first release — **Open, in progress** (report status *Partially done*):
  [task](tasks/026-security-hardening-edge-api-containers.md),
  [report](tasks/026-security-hardening-edge-api-containers.report.md).
- 026b — Database and performance — Planned
- 026c — Object storage and frontend follow-ups — Planned
- 027 — End-to-end smoke tests against the acceptance criteria — Planned

## 5. In flight — task 026
**In progress: Parts A–E implemented, verified, committed, pushed; Part F (release to production) not started.**
Done and verified (numbers and tables in the report): `IMAGE_TAG` per release, runbook without backups,
`Tailscale-User-*` stripped, API security headers, 128 KiB body limit with 413 ProblemDetails, localised binder errors,
encoded request path in the log, storage-site headers, container hardening, RabbitMQ memory, build-time CSP +
`vercel.json` headers. Backend 1825 / 1825, frontend 518 / 518 (twice + slow run), CI green; on the throwaway stack
`omni026` (removed): `task-025.ps1` 53 / 53, `task-026.ps1` 9 / 9, backup + restore, two browser sessions with zero
CSP violations.
**Left — Part F**, exactly as the task file and runbook sections 4 and 15 (plan also in the report, "Production steps"):
1. USER: `VITE_STORAGE_ORIGIN` in Vercel (section 6 below).
2. `scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod` (adds `IMAGE_TAG` = short commit of HEAD), `GIT_COMMIT`.
3. `--profile migrate build`, `run --rm migrate` (nothing pending expected), `stop objectstorage`, the one-shot
   `chown -R 1000:1000` of `omnichannel-prod_objectstorage-data` (runbook 15), `up -d --wait`, `ps`, health; report the
   downtime from `stop` to healthy.
4. Push HEAD to `production`; wait for Vercel.
5. `task-025b.ps1` live (26 steps; needs `SMOKE_STORAGE_ORIGIN`; its first full run — steps 13–15 only pass on Vercel).
6. USER: live browser check with the console open (login, dashboard, image upload, export download really saved, an order).
7. Remove `omnichannel-*:local` images by name; then finish the report (status, "Production steps (applied)") and commit.
Rollback of this release: check out `8fc1e5e` (its compose file names the `:local` images) and `up -d --wait`.
Not yet in the report: the report commit `294cb91` was pushed after the CI run named above (docs only).
Open questions for Cowork (report, "Blocked / questions"): digest pinning of third-party images; `X-Correlation-Id`
header missing on 500 answers (older than 026); `user: postgres` as a follow-up.

## 6. Pending for the user
- **Vercel:** project → Settings → Environment Variables → add `VITE_STORAGE_ORIGIN` =
  `https://omni.tail70815b.ts.net:8443`, environment Production only → Save (no redeploy). Then say so in the chat.
- **Go-ahead for production commands:** the session's permission layer refused even read-only commands against
  `omnichannel-prod` containers; Part F needs the user's explicit yes in the chat (or a permission rule), or the user
  runs the steps from the runbook.
- After the release: the live browser check (step 6 above).
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (never printed).
- Open decisions: none in the roadmap.

## 7. Environment
- **Development:** docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals: API,
  Workers, scripts/commands. Commands: README §1, §5, §6; frontend: `cd frontend; npm run dev`.
- Ports (all `127.0.0.1`): API 5080, dev server 5173, `vite preview` 4173, PostgreSQL 5432, RabbitMQ 5672, RabbitMQ
  UI 15672, object storage 8333; production proxy 8080 (API) and 8081 (storage).
- **Production:** compose project `omnichannel-prod` (`docker/docker-compose.prod.yml`, 2 Workers); live origins and
  every procedure in [docs/runbook.md](runbook.md) (sections 4 release / rollback, 13 live values, 15 containers).
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
  - `docker/.env.prod` (git-ignored) — every secret of the production stack; names in `docker/prod.env.example`
    (`IMAGE_TAG` is not in it yet: Part F adds it). The production Admin password is only with the user.
- Tests: `dotnet test` in `backend/` (1825, about 3 min; needs Docker); `npm test` in `frontend/` (518, 4–6 minutes,
  run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`. CI runs both on push.

## 8. Gotchas learned
- **A push to `master` deploys nothing**; only `production` does. → runbook "Release".
- **Throwaway production-like stacks have their own `IMAGE_TAG`** (`new-prod-env.ps1 -ImageTag drill-<name>`), project
  name, ports and edge subnet; remove their images by tag. → runbook 4, 7; report 026.
- **Caddy cannot start as non-root with `cap_drop: ALL`** (file capability on its binary): `NET_BIND_SERVICE` stays.
  **RabbitMQ as root without DAC capabilities fails its health check** (cookie): it runs as `rabbitmq`. → runbook 15.
- **A storage volume not owned by 1000:1000 makes SeaweedFS restart in a loop**: one-shot `chown`. → runbook 15.
- **A chunked oversize body through the proxy gets Caddy's bare 413**; with a Content-Length the API's problem
  document. → report 026.
- **`vite preview` against development needs `VITE_STORAGE_ORIGIN=http://127.0.0.1:8333` at build time** (CSP). → README "Frontend".
- **Binder errors now use the field key** (`items[0].stock`, body itself `$`), localised. → report 026, deviation 2.
- **Funnel forwards the client address and `https` and keeps `Host`**; requests from this machine to the public
  names take the tailnet path. → runbook "Live values"; report 025b.
- **Empty proxy lists mean "believe everyone" in ASP.NET Core**; nothing configured = headers not read. → report 025.
- **A presigned link is signed for its host**: storage has its own origin. → README "Production deployment".
- **Migrations as the migrator**: list before update, additive only, throwaway first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**; a running API / Workers process
  locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only; stderr of native tools is a terminating error under `Stop`. → reports 020, 025b.
- **Frontend:** tests end with `await settled()`; full runs need a quiet machine. → README "Frontend", report 024b.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**User:** set `VITE_STORAGE_ORIGIN` in Vercel and give the go-ahead for the production release (section 6).
**Claude Code:** then run Part F of task 026 (section 5), finish the report and commit it.
**Cowork:** review task 026 only after the report says the release is done.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: task 026 đang dở (Parts A–E xong, commit c8f31d7 + report 294cb91; Part F — release lên production — đang chờ người dùng đặt VITE_STORAGE_ORIGIN trên Vercel và cho phép chạy lệnh production). Chưa review cho đến khi report ghi release đã xong; trong lúc chờ có thể trả lời 3 câu hỏi trong report (pin digest image bên thứ ba, header X-Correlation-Id trên 500, user: postgres).
```

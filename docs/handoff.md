# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-10 18:35 (+07:00), by Claude Code, after finishing task 025.
- Branch `master` (nothing pushed; no remote is set up yet — that is task 025b). HEAD: `4c82765` "task 025: report"
  (this file is committed on top of it). Task 025 is the eight commits `f7e514b` … `4c82765`.
- Working tree: clean. No API, Workers host, preview server or throwaway stack is left running by Claude Code; the
  development PostgreSQL, RabbitMQ and object storage containers are up and were not touched by 025.
- Newest migration: **`20261009133050_PaymentAndInvoices`** — applied to development (task 024). Task 025 has none.
- `docs/roadmap.md` still shows 025 as `Open`: Cowork updates the roadmap and the log in the review.

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
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps".

## 4. Progress
- 001–024b — Done (titles in [docs/roadmap.md](roadmap.md)).
- 025 — Ops: build and run anywhere (images, production compose, reverse proxy, cross-origin frontend, storage
  origin, CI) — **Review**: [task](tasks/025-build-and-run-anywhere.md),
  [report](tasks/025-build-and-run-anywhere.report.md).
- 025b — Ops: go-live (GitHub + CI run, Vercel, tunnel or Oracle VM, backups, runbook) — Planned
- 026 — Security and performance hardening — Planned
- 027 — End-to-end smoke tests against the acceptance criteria — Planned

## 5. In flight — task 025
**Task 025: report written, awaiting Cowork review.** Nothing half-finished; everything is committed.
Verified: backend build 0 warnings, **1798 / 1798**; frontend **500 / 500** in two full runs and the slow run;
production stack brought up from an empty state locally (throwaway project `omni025`, removed afterwards with
`down -v`); `task-025.ps1` **48 / 48**; Part D checks; cross-origin browser check on the production build.
**Not verified:** the CI workflow has not run on GitHub and was not checked with actionlint (structural check only);
TLS with public host names is validated as configuration only.
For the review (report, "Deviations" and "Blocked / questions for Cowork"):
1. How the real tunnel (025b) reports client address and scheme — without `X-Forwarded-Proto: https` the API
   redirects in a loop unless `API_HTTPS_REDIRECT=false`; `PROXY_TRUSTED_PROXIES` must name the edge gateway.
2. Is configuration-only verification of TLS mode enough for 025?
3. Two Data Protection warnings at every API start (keys created, never used): leave, silence or persist?
4. The proxy's request body limit is 30 MB (Kestrel's default): tighten in 026?
5. Third-party containers (Caddy, SeaweedFS) run as root inside their containers: harden in 026?
Eleven deviations are listed in the report; the notable ones: the .NET 10 images are Ubuntu 24.04, not Debian;
settings are `ReverseProxy:*`; nothing configured = forwarded headers are not read at all.

## 6. Pending for the user
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (never printed).
- Coming with 025b (accounts only the user can create): GitHub repository, Vercel project, Tailscale or Oracle.
- Open decisions: none in the roadmap; the five questions above are Cowork's to answer.

## 7. Environment
- **Development:** docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals: API,
  Workers, scripts/commands. Commands: README §1, §5, §6; frontend: `cd frontend; npm run dev`.
- Ports (all `127.0.0.1`): API 5080, dev server 5173, `vite preview` 4173, PostgreSQL 5432, RabbitMQ 5672, RabbitMQ
  UI 15672, object storage 8333.
- **Production stack** (not running anywhere yet): README "Production deployment" — `docker/docker-compose.prod.yml`,
  env file from `scripts/deploy/new-prod-env.ps1`, migrate → up; proxy on `127.0.0.1:8080` (API) and `:8081` (storage).
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
  - `docker/.env.prod` (git-ignored, does not exist yet) — every secret of the production stack; names in
    `docker/prod.env.example`.
- Tests: `dotnet test` in `backend/` (1798, about 4 min; needs Docker); `npm test` in `frontend/` (500, 4–6 minutes,
  run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`.
- Left on development by earlier smoke runs: smoke orders, products and locked smoke users (024, 024b); no open alert.
  Locally built images `omnichannel-api|workers|migrate:local` remain in Docker.

## 8. Gotchas learned
- **Empty proxy lists mean "believe everyone" in ASP.NET Core**: with nothing in `ReverseProxy:KnownProxies` the API
  does not read forwarded headers at all; loopback is not trusted. → report 025 (deviation 2).
- **A presigned link is signed for its host**: the storage needs its own origin and `ObjectStorage:PublicServiceUrl`;
  a proxy cannot rewrite it. → README "Production deployment", "Product images".
- **Requests from the host reach the proxy from the edge gateway** (`172.30.250.1`), not from loopback. → report 025.
- **The .NET 10 default images are Ubuntu 24.04** (ICU and tzdata included); there is no Debian variant. → report 025.
- **`docker/.env.prod` is ignored, `prod.env` would not be** (`.gitignore`: `.env.*`). → report 025 (deviation 8).
- **Payment never changes `Order.Status` or stock**; invoice and e-mail are independent tasks. → README §10.
- **A malformed JSON value answers with binder keys** (`$.field`, `request`) and English text. → review 024b (026).
- **`formatMoney` uses a no-break space**; exact test comparisons need it. → `frontend-verification` notes, report 024b.
- **Webhooks:** the signature covers the raw bytes; identity `webhook:{slug}` has no role. → README §9.
- **Reconciliation never changes the source of truth.** → README §8.
- **Alerts are written only by `AlertStore`**, inside the transaction of the cause. → README "Alerts".
- **Migrations as the migrator**: list before update, additive only, throwaway first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**: run it in the background and read
  its output file; a running API / Workers process locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only; stderr of native tools is a terminating error under `Stop`. → report 020.
- **Frontend:** tests end with `await settled()`; avoid role queries over large tables inside waits; full runs need
  a quiet machine (a loaded run can time out in unrelated files). → README "Frontend", report 024b.
- **Local connections use `127.0.0.1`**, never `localhost` (except browser origins). → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Cowork:** review task 025 (report + commits `f7e514b` … `1a02cf1`), answer the five questions, update the roadmap
and the implementation log, then write task 025b (go-live).
**Claude Code:** `/next-task` once task 025b exists. **User:** nothing pending until 025b needs the accounts.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 025 (docs/tasks/025-build-and-run-anywhere.report.md, các commit f7e514b … 1a02cf1), trả lời 5 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 025b (go-live: GitHub + CI, Vercel, tunnel hoặc Oracle VM, backup, runbook).
```

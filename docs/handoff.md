# Handoff — Omnichannel Order & Inventory Management System

This file is an index and a snapshot of what is in flight. The repository documents are the memory; follow the links.

## 1. Snapshot
- Written: 2026-10-09 21:45 (+07:00), by Claude Code, after finishing task 024.
- Branch `master` (nothing pushed). HEAD: `40a9398` "task 024: report" (this file is committed on top of it; before
  it `d634737` "task 024: payment step, Invoice Worker and Notification Worker …" and `11bd1f1` "task 024 A1 …").
- Working tree: clean. No API, Workers host or preview server is left running by Claude Code; the development
  PostgreSQL, RabbitMQ and object storage containers are up.
- Newest migration: **`20261009133050_PaymentAndInvoices`** — applied to development on 2026-10-09 by Claude Code as
  the migrator (`dotnet ef migrations list`: 14 migrations, 0 pending).
- `docs/roadmap.md` still shows 024 as `Open`: Cowork updates the roadmap and the log in the review.

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
- **Claude Code** implements: `/next-task` picks the lowest-numbered task without a report, commits, writes
  `NNN-*.report.md`, and **applies the development steps itself** (key helper, changed containers, additive
  migrations as the migrator, smoke script on development).
- Details, guardrails and the report template: [docs/tasks/README.md](tasks/README.md), "Development steps".

## 4. Progress
- 001–023 — Done (titles in [docs/roadmap.md](roadmap.md)).
- 024 — Payment step, Invoice Worker, Notification Worker + Part A (review 023) — **Review**:
  [task](tasks/024-payment-invoice-email.md), [report](tasks/024-payment-invoice-email.report.md).
- 024b — Frontend: payment on the order detail, invoice, labels, `order.changed` mapping — Planned
- 025 — Ops: Dockerfiles, compose, CI, free hosting, production object storage — Planned
- 026 — Security and performance hardening — Planned

## 5. In flight — task 024
**Task 024: report written, awaiting Cowork review.** Nothing half-finished; everything is committed (`11bd1f1` A1,
`d634737` Parts B–F with the migration, `40a9398` report).
Verified: backend build 0 warnings, **1756 / 1756**; mutation run **26 of 26 killed**; smoke helpers self-test 64/64;
`task-024.ps1` **51/51** on a throwaway environment and **52/52 on development** (frontend build + tests included).
No frontend file changed: the frontend shows the new task types, error codes and events raw and has no payment
button until 024b.
For the review (report, "Deviations" and "Blocked / questions for Cowork"):
1. `AttemptNo` on `InvoiceRequest` / `EmailMessage`, `paidBy` as an actor field of the Logs API, and one shared base
   for the two new workers — accepted?
2. The simulated invoice number (`SIM-{yyyyMM}-{8 hex}`, as specified) can collide between two orders of one month;
   the unique index then fails that task. Leave, or lengthen?
3. A confirmation e-mail can be sent twice when the commit after the send fails (at-least-once). Accepted?
4. `order.changed` carries only `orderId`; the task hints of a payment already refetch the open order detail.
Ten deviations are listed in the report (none architectural).

## 6. Pending for the user
- No manual development step is open. `OMNICHANNEL_MIGRATOR_CONNECTION` is set at user level (Claude Code reads it
  from there; it is never printed).
- Optional demo: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024.ps1`, or the payment sample in
  `backend/src/Omnichannel.Api/Omnichannel.Api.http`; README section 10.
- Open decision (roadmap): before 025 — hosting and the production object-storage provider (free, no AWS).

## 7. Environment
- Start infrastructure with docker compose (PostgreSQL, RabbitMQ, object storage emulator), then three terminals:
  API, Workers, scripts/commands. Commands: README §1, §5, §6; payment, invoices and e-mails: README §10; smoke
  scripts: README "Smoke scripts"; frontend: `cd frontend; npm run dev`.
- Ports: API 5080, frontend dev server 5173 and `vite preview` 4173 (both proxy `/api`, WebSockets included),
  PostgreSQL 5432, RabbitMQ 5672, RabbitMQ UI 15672, object storage S3 API 8333 (all on `127.0.0.1`).
- Secrets, by name and location only:
  - `docker/.env` — database and RabbitMQ passwords, `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID`,
    `OBJECT_STORAGE_SECRET_ACCESS_KEY`;
  - user-secrets of `Omnichannel.Api` — `ConnectionStrings:Default`, `Jwt:SigningKey`, `BootstrapAdmin:*`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`, `Webhooks:Channels:<slug>:Secrets:0`;
  - user-secrets of `Omnichannel.Workers` — `ConnectionStrings:Default`, `RabbitMq:UserName`, `RabbitMq:Password`,
    `ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey` (the invoice provider and the mail server are
    simulated and need no secret);
  - `backend/src/Omnichannel.Api/.env` (git-ignored) — `BOOTSTRAP_ADMIN_EMAIL`, `BOOTSTRAP_ADMIN_PASSWORD`,
    `WEBHOOK_SECRET_WEBSITE`, `WEBHOOK_SECRET_POS`, `WEBHOOK_SECRET_DIGITAL_PLATFORM`, read by the scripts;
  - `OMNICHANNEL_MIGRATOR_CONNECTION` (user-level environment variable) — for `dotnet ef database update`.
- Tests: `dotnet test` in `backend/` (1756, about 4 min; needs Docker); `npm test` in `frontend/` (455, 4–7 minutes,
  run with nothing else running); `npm run test:slow`; `npm run measure` after `npm run build`.
- Left on development by the 024 smoke run: three paid smoke orders with invoices, one `InsufficientStock` order,
  no open alert; smoke users are locked after each run.

## 8. Gotchas learned
- **Payment never changes `Order.Status` or stock**; invoice and e-mail are two tasks that never read each other;
  the e-mail task exists only when the customer has an address. → README §10, log "Task 024 plan".
- **One invoice per order**: `Invoices.OrderId` is the key, inserted with `ON CONFLICT DO NOTHING` in the completion
  transaction; the provider key is `invoice:{orderId}`. → README §10, report 024 (deviation 4).
- **Development rules of the simulators** (`Rules: true` only in `appsettings.Development.json`): `SIM-INV-PERM-` /
  `SIM-INV-TEMP-` order ids, `@sim-reject.test` / `@sim-temp.test` recipients. → README §10.
- **Never log an e-mail address or text**: `EmailAddress.Mask`, also in error messages. → report 024.
- **jsonb payloads come back reformatted** (`{"taskId": "…"}`): tests compare parsed JSON. → report 024.
- **`SMOKE_SKIP_FRONTEND=1`** only with `SMOKE_BASE_URL`; refused on the local default. → README "Smoke scripts".
- **Webhooks:** the signature covers the raw bytes; nothing of the body is parsed before it; the identity
  `webhook:{slug}` has no role. → README §9, review 023.
- **Reconciliation never changes the source of truth**; tests of it use a database of their own. → README §8.
- **Transitive pinning is on** (`NU1109` at restore on a conflict). → README, *Package*.
- **Alerts are written only by `AlertStore`**, inside the transaction of the cause. → README "Alerts".
- **Migrations as the migrator**: read the user-level variable inside a script, redact the output, list before
  update, additive only, throwaway environment first. → `docs/tasks/README.md`.
- **A script that starts hosts and leaves them running hangs the calling tool**: run it in the background and read
  its output file; a running API / Workers process locks `bin/Debug`. → reports 018b, 020.
- **PowerShell 5.1:** scripts ASCII only (no repository path literal in a scratch script — pass it as a parameter);
  stderr of native tools is a terminating error under `Stop`. → report 020, deviation 11.
- **The build treats warnings as errors**: a mutant with unreachable code or a nullable warning "does not compile" —
  write a compilable variant. → reports 023, 024.
- **Frontend:** tests end with `await settled()`; request tests also run under `npm run test:slow` on a quiet
  machine; check effects in `npm run build` + `vite preview`. → README "Frontend".
- **Local connections use `127.0.0.1`**, never `localhost`. → README §3.
- **RabbitMQ 4 does not count `basic.nack(requeue)` toward `x-delivery-limit`.** → log, "Task 007 result".
- **Inventory rows are always locked in `ProductId` order.** → log, "Task 003 result".

## 9. Next action
**Cowork:** review task 024 (report + commits `11bd1f1`, `d634737`), answer the four questions, update the roadmap
and the implementation log, then write task 024b (frontend: payment on the order detail, invoice, labels).
**Claude Code:** `/next-task` once task 024b exists. **User:** nothing pending.

## 10. Resume prompts
New Claude Code chat:
```
/resume
```
(If the command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then continue with the next action in
docs/handoff.md.")

New Cowork chat:
```
Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: review task 024 (docs/tasks/024-payment-invoice-email.report.md, các commit 11bd1f1, d634737), trả lời 4 câu hỏi trong report, cập nhật roadmap và implementation log, rồi viết task 024b (frontend: thanh toán trên trang chi tiết đơn hàng, hóa đơn, nhãn).
```

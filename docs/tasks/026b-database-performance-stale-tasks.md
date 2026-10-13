# Task 026b — Release of 026; error answers on the cross-origin path; image digests; database and performance (user e-mail lookups, summary indexes, stale non-terminal tasks, product filter, load test of the reserve path)

Status: Open
Written by: Cowork, 2026-10-13
Depends on: 026 (Parts A–E accepted, Part F open — `docs/tasks/026-security-hardening-edge-api-containers.review.md`)

## Context
Doc §7 (inventory, reserve transaction), §8 (processing, retry, DLQ, reconciliation), §9 (tracking), §10 (dashboard),
§14; CLAUDE.md. Roadmap row 026b (split of 026, log "Task 026 plan"). Production has **no backup** (user,
2026-10-12): `docs/tasks/README.md` "Production steps" — the guardrails are the only protection.

Read first: review 026 (questions, findings); reviews 012 (P3 `createdByEmail`), 015 (questions: three summary
statements, `lastSucceededAt`), 017 (P3 unused alert indexes), 020, 022 (stale tasks, product filter); log "Task 015
result", "Task 022 plan/result", "Task 026 plan"; `TaskRequestMessageHandler.cs`, `ProcessingTaskService.cs`
(manual retry), `StockReconciliation.cs`, `ReconciliationWorker/`, the dashboard summary query,
`SystemEventService.cs` (per-page e-mail lookup — the pattern to reuse), `docs/runbook.md` §4, §8, §15.

### Stop and report (`Blocked`) if
- the migration of Part B contains anything but `CREATE INDEX` (no drop, no rename, no data change);
- Part C would need a new task status, a new table, or a change of the message contract;
- the load test finds an oversell, a lost order or a task left non-terminal — that is a defect: report it with the
  evidence, do not tune it away;
- a production step other than those of Part 0 and Part G would be needed.

## Part 0 — Release of task 026 (its Part F, unchanged)
1. **USER:** `VITE_STORAGE_ORIGIN` = `https://omni.tail70815b.ts.net:8443` in Vercel (Production), and the go-ahead for
   production commands in the chat. Claude Code asks for both and waits.
2. Exactly task 026 Part F steps F2–F7 (release of `master` at that moment — 026's code, nothing of 026b yet: release
   from the commit **before** any 026b commit, i.e. check out `origin/master` as it is when this task starts).
3. Results go into **026's report** ("Production steps (applied)", status `Done`), in a commit of their own;
   026b's report only links to it. Rollback, if F fails: the runbook's, report what happened.

## Part A — Follow-ups of review 026

### A1. Error answers readable on the cross-origin path (review 026 findings, question 3)
- CORS is evaluated **before** the body limit (move `RequestBodyLimitMiddleware` after `UseCors`, or `UseCors`
  earlier — keep forwarded headers, correlation id and request logging first); check the order against
  `UseHttpsRedirectionBehindProxy` and the rate limiter and say why the final order is right.
- `X-Correlation-Id` set in `OnStarting` (as `SecurityHeadersMiddleware` does), so the exception handler's 500 keeps it.
- Integration tests with an `Origin` of the configured frontend: 413 (announced length), 413 (chunked, straight to
  Kestrel), 500, 429, 401 — each carries `Access-Control-Allow-Origin`, exposes `X-Correlation-Id`, has the header, and
  the body's `correlationId` equals the header.

### A2. Third-party images pinned by digest (review 026 q2)
`postgres:17@sha256:…`, `rabbitmq:4-management@sha256:…`, `caddy:2.11@sha256:…`, `chrislusf/seaweedfs:4.48@sha256:…`
— the digests of the images in the local store now (`docker image inspect --format '{{index .RepoDigests 0}}'`),
which production runs. Also the tool image of `_stack.ps1` (`postgres:17`) — the same digest. Runbook "Updating a
third-party image": change the digest in the compose file, release; never `pull` by tag on this machine.
`docker compose config` shows the digests; the release (G) recreates nothing for an unchanged digest — check with
`docker compose … up -d --wait` output.

## Part B — Database

### B1. User e-mails without the text join (review 012, 017)
`InventoryQueryService` (`createdByEmail`), `OrderQueryService` (`paidByEmail`), `AlertService`
(`acknowledgedByEmail`) compare `u.Id.ToString() == x` per row. Replace by the per-page lookup of `SystemEventService`
(collect the ids of the page that parse as a `Guid`, one `WHERE Id = ANY(...)`, map in memory); non-user actors
(`system`, `webhook:{slug}`) keep their current output. Contracts unchanged (tests prove the same JSON for a page with
a user, a webhook actor, a system actor and a deleted user). Share the lookup if two or more call sites end up with
the same code — no new abstraction otherwise.

### B2. Summary and tracking indexes (review 015)
- Seed a throwaway database at a stated size (at least 8k orders, 24k tasks, 40k outbox rows, as in review 015;
  script or test helper, not committed data) and run `EXPLAIN (ANALYZE, BUFFERS)` of every statement of
  `GET /api/dashboard/summary`, of `GET /api/processing/tasks` with each filter (incl. B-new product filter, D), and of
  the stale-task query (C). Report before / after.
- Candidates (add only what an `EXPLAIN` shows is used and helps; justify each in the report and in the
  configuration comment):
  - `OutboxEvents` partial on `Status = 'Failed'`;
  - `ProcessingTasks` partial on non-terminal statuses (`Pending`, `InProgress`, `RetryScheduled`) over
    `COALESCE("UpdatedAt", "CreatedAt")` (or the columns the queries really use) — serves the summary **and** C;
  - `ProcessingTasks` partial `(ChannelId, UpdatedAt DESC) WHERE TaskType = 'StockSync' AND Status = 'Succeeded'`
    (`lastSucceededAt`);
  - `ProcessingTasks (ProductId, CreatedAt DESC)` if the product filter (D) needs more than the existing `ProductId`.
- Unused indexes (e.g. review 017's `IX_Alerts_TaskId`, `IX_Alerts_OrderId` if they exist): **list only**, with
  `pg_stat_user_indexes` evidence from the seeded run; no drop in this task.
- One migration (`PerformanceIndexes` or similar), EF configuration as the source; additive only. Production tables are
  tiny: a plain `CREATE INDEX` inside the migration's transaction is fine — say so; `CONCURRENTLY` is not needed.

### B3. `ReportExports` rows — decision only (log "Task 026b plan")
Rows are kept (no purge): a row is about a kilobyte, it is the audit of who exported what, and its file is already
deleted after 7 days. Revisit at 100k rows. Remove the item from the roadmap with that reason.

## Part C — Stale non-terminal tasks (review 022; runbook §8)
A task whose message is lost (RabbitMQ volume lost, outbox row `Failed` after the publisher's attempts, a message
dropped by hand) stays `Pending` / `InProgress` / `RetryScheduled` for good; reconciliation trusts it and skips the
pair, the operator cannot retry it (`task_not_failed`).

- **Sweep** in the Workers host, periodic like reconciliation (own options section `StaleTasks`: `Enabled`,
  `Interval` default 5 min, `StaleAfter` default 30 min, `MaxPerRun` default 200; validated: `StaleAfter` must exceed
  the longest consumer retry delay and the Outbox Publisher's longest backoff by a margin — compute from their options
  at start, refuse to start otherwise), one run at a time (PostgreSQL advisory lock, own key).
- A task is stale when it is non-terminal and `COALESCE(UpdatedAt, CreatedAt) < now - StaleAfter`. The sweep marks it
  **`Failed`** through the same domain path a worker uses for a non-retryable failure (so the alert
  `ProcessingFailed`, the realtime hint and the counters behave as for any failure), error code **`stale_no_progress`**,
  with a conditional update under the row lock (`FOR UPDATE`, status and age re-checked) — never a task a worker
  touched meanwhile. One `SystemEventLog` per run (`processing.stale_tasks_failed`, counts + task ids up to a cap) and
  the Logs label (vi / en); the task detail shows the error code with a label.
- **Message arriving after the sweep:** the handler already acknowledges a message for a `Failed` task as a duplicate.
  After a manual retry the task is `Pending` again with a new message id: check whether an **old** message (the
  pre-sweep one, e.g. still in a retry queue) could then be processed alongside the new one. If the handler does not
  already reject a message whose id is not the task's current one, add that check (`HandlerResult.Ack` + log) — a test
  with both messages delivered after a retry proves exactly one attempt runs.
- **Reconciliation:** nothing to change if a stale task is `Failed` by the time reconciliation looks (its skip rule
  only trusts unfinished tasks; `stale_no_progress` is not `channel_rejected`) — prove it with a test: stale sync
  task → sweep → next reconciliation creates a new sync task for that pair.
- Tests: each non-terminal status, the age boundary, a task touched between selection and update, `MaxPerRun`, two
  hosts at once (lock), disabled, invalid options.
- Runbook §8 rewritten: after a lost RabbitMQ, stale tasks fail by themselves within `StaleAfter + Interval`; then
  "Retry" on Sync & Processing.

## Part D — Product filter on processing tasks (review 022)
- `GET /api/processing/tasks?productId=` (validated `Guid`), combinable with the existing filters; OpenAPI / `.http`
  sample.
- Sync & Processing page: a product picker (reuse `ProductSelect`) in the filters, kept in the address like the others,
  cleared with them; a link "tasks of this product" from the product's row actions or detail where one exists (only if
  it fits without a new page). Labels vi / en; tests as the other filters.

## Part E — Load test of the reserve path (throwaway stack only)
- Tool: **k6 from its official container image, pinned** (nothing installed, as gitleaks in 025b); scripts under
  `scripts/load/` (committed), a README section with how to run them. Runs only against a throwaway production-like
  stack (`omni026b`, two Workers), never production; one throwaway Admin / Manager created by the stack's bootstrap.
- Scenarios (report each: requests, rate, p50 / p95 / p99 of the intake answer, p50 / p95 of intake → `Reserved` /
  rejected from the task timestamps, Worker throughput, error counts, CPU / memory of `postgres`, `rabbitmq`,
  `workers` from `docker stats`):
  1. **Contention:** 1 SKU with stock 100, 300 orders of quantity 1 as fast as the intake accepts → exactly 100
     `Reserved`, 200 rejected for insufficient stock, `Total/Reserved/Available` consistent with the movements,
     no task non-terminal at the end.
  2. **Spread:** 50 SKUs, 2,000 orders at a constant 20 / s for 100 s, random 1–3 lines → all orders terminal,
     invariants per SKU, outbox drained.
  3. **Duplicates:** 200 requests reusing 100 external order ids (two each, concurrently) → 100 orders.
- After each scenario an SQL invariant check (script): `Available = Total - Reserved >= 0` for every row; reserved sum
  = sum of the `Reserved` orders' quantities per SKU; one `InventoryMovement` per reserved line; no duplicate order per
  `(ChannelId, ExternalOrderId)`.
- No performance target is set (baseline for a demo on one machine); the numbers go into the log as the baseline.
  Anything that looks like a bottleneck → report with evidence, no tuning in this task.

## Part F — Verification
- Backend build 0 warnings, full `dotnet test`; frontend typecheck / lint / test (two full runs) / `test:slow` /
  build / `measure`; CI green.
- Migration: `dotnet ef migrations list` before; applied on the throwaway stack and the seeded database first; then on
  development as the migrator (rules of `docs/tasks/README.md`).
- `omni026b` (own project, ports, subnet, `IMAGE_TAG=drill-omni026b`): `task-025.ps1` 53/53, `task-026.ps1`, the A1
  checks through the proxy with an `Origin` header, a stale-task drill (stop RabbitMQ consumption of one queue or
  delete a message by hand on the throwaway broker, wait `StaleAfter + Interval` with short throwaway options → task
  `Failed` `stale_no_progress`, alert open, retry → `Succeeded`), Part E. Removed with `down -v` of that project only,
  images by tag.
- `scripts/smoke/task-026b.ps1` for what is not already covered (product filter, A1 headers through the proxy,
  stale-task drill if it can be automated safely on a throwaway stack).

## Part G — Release of 026b to production (runbook §4)
`set-image-tag.ps1`, `GIT_COMMIT`, build, `migrations list` via the bundle / runbook §5 (only `PerformanceIndexes`
pending), `run --rm migrate`, `up -d --wait`, push to `production`, `task-025b.ps1` and `task-026.ps1` live. Report
the downtime, the migration's duration, and that the stale-task sweep started (Workers log) and failed nothing
unexpected on production (list what it failed, if anything — production holds smoke data from go-live).

## Acceptance criteria
- [ ] Part 0: 026 released; 026's report completed and committed.
- [ ] A1 cross-origin error answers readable (tests); A2 digests pinned, runbook updated.
- [ ] B1 no `Id::text` comparison left (grep); same JSON; B2 seeded `EXPLAIN` before / after, only justified indexes,
      one additive migration; unused indexes listed; B3 recorded.
- [ ] C sweep with options validation, lock, conditional update, alert / hint / event, old-message guard, tests,
      reconciliation test, runbook §8; stale-task drill green on `omni026b`.
- [ ] D product filter API + page, address-bound, tests.
- [ ] E three scenarios with numbers and invariant checks, k6 pinned, scripts committed; no oversell / loss / duplicate.
- [ ] Suites and CI green; development migrated as the migrator; `omni026b` removed.
- [ ] G release with the migration; live checks green.
- [ ] Report with "Development steps (applied)" and "Production steps (applied)".

## Out of scope
Image orphan sweep, final image key, signed `Content-Length`, `validateMessages`, accessibility pass (026c); dropping
indexes; partitioning or archiving of large tables; a performance target; backups; Oracle VM (025c).

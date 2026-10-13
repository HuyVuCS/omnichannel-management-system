# Report 026b — Release of 026; error answers on the cross-origin path; image digests; database and performance (user e-mail lookups, summary indexes, stale non-terminal tasks, product filter, load test of the reserve path)

Status: Done — every part implemented, verified and released (production runs `a980747`). Three things need a
decision of Cowork ("Blocked / questions"): an oversize body is usually refused by the **proxy**, not by the API, so
that one error answer is still not readable cross-origin on the live path (question 1); the local backend suite has
shown a few connection timeouts per full run since today's WSL upgrade, also on the commit before this task, while CI
is green (question 4); two query shapes no index can help (question 3).
Commit: `66b6f86` (A), `69ac455` (B1), `bf5f489` (C, D backend), `0db25e1` (C, D frontend), `dafc139` (B2),
`b1adb9e` (runbook, README), `bfd0b6b` (smoke script), `4d39d7e` + `a980747` (E), `3ecd073` (README); Part 0 is
`a1a4263` (report 026). Released commit: **`a980747`** — CI run 38387301610 (`3ecd073`) and the run of `a980747` green.

## Summary
- **Part 0:** task 026 released (results in [report 026](026-security-hardening-edge-api-containers.report.md),
  "Production steps (applied)"). Before it, Docker's engine was found stopped after a WSL upgrade and restarted.
- **A:** CORS is evaluated before the body limit and `X-Correlation-Id` is set when the answer starts, so 401, 413,
  429 and 500 answers of the API are readable from the frontend origin (tests + smoke); the four third-party images
  and the tool image are pinned by digest.
- **B:** the three `Id::text` e-mail joins are one per-page primary-key lookup (`UserEmailLookup`); two measured
  indexes in one additive migration (`PerformanceIndexes`); `ReportExports` rows stay (already recorded).
- **C:** the stale-task sweep in the Workers host (`StaleTasks` options validated at start, advisory lock, failure
  through the workers' own final-failure path with `stale_no_progress`, one event per run), and a message that is not
  the one a task waits for starts no attempt (checked under the task's row lock in every worker).
- **D:** `GET /api/processing/tasks?productId=`, a product picker on Sync & Processing (address-bound), a link from a
  stock-sync task to the tasks of its product. **E:** k6 load scripts; three scenarios on `omni026b` with no oversell,
  no lost order, no duplicate, nothing unfinished. **G:** released with the migration, 42 s of downtime.

## Files changed
Backend
- `Omnichannel.Api/Program.cs`, `Middleware/CorrelationIdMiddleware.cs` — A1: `UseCors` before
  `RequestBodyLimitMiddleware` (comment says why the order is right); the correlation header in `OnStarting`.
- `Omnichannel.Infrastructure/Persistence/UserEmailLookup.cs` (new), `Inventories/InventoryQueryService.cs`,
  `Orders/OrderQueryService.cs`, `Alerts/AlertService.cs` — B1.
- `Persistence/Configurations/ProcessingTaskConfiguration.cs`, `OutboxEventConfiguration.cs`,
  `Migrations/20261013092919_PerformanceIndexes.*`, snapshot — B2 (two `CREATE INDEX`, nothing else).
- `Omnichannel.Application/Processing/StaleTaskContracts.cs` (new: options, rules, `IStaleTaskSweep`),
  `AttemptErrorCodes.cs` (`stale_no_progress`), `ProcessingEvents.cs` (`processing.stale_tasks_failed`),
  `SystemEvents/SystemEventDetails.cs` (allowed fields of the new event) — C.
- `Persistence/Processing/StaleTaskSweep.cs` (new), `ProcessingAttemptStore.cs` (`FailStaleAsync`; the message-id
  check in `StartAttemptAsync`), `DependencyInjection.cs` — C.
- `Omnichannel.Workers/StaleTaskWorker/StaleTaskSweepService.cs` (new: service + options validator against the
  retry delays and the publisher), `WorkerHostSetup.cs` — C.
- `IOrderReservationService`, `IStockSyncService`, `ITaskRequestProcessing`, `IReportExportProcessingService`, their
  four implementations and the four message handlers — C: `StartAttemptAsync` takes the id of the delivered message.
- `Application/Processing/ProcessingContracts.cs`, `Persistence/Processing/ProcessingTaskService.cs`,
  `Omnichannel.Api.http` — D.
- Tests: `Integration/CrossOriginErrorAnswerTests.cs` (new, 7), `Integration/ApiFactory.cs` (the server's cut of a body
  without a length, see deviation 1), `InventoryImportTests`, `AlertEndpointsTests`, `OrderPaymentEndpointsTests`
  (B1: a page with users, a webhook actor, a system actor, a user that is gone), `ChannelProductEndpointsTests` (D),
  `Integration/Workers/StaleTaskSweepTests.cs` (new), `ReconciliationTests.StaleTasks.cs` (new),
  `Workers/StaleTaskOptionsTests.cs` (new), `SystemEvent*Tests` (the new event), test doubles of `StartAttemptAsync`,
  `WorkersFixture.cs` (sweep off by default, as reconciliation).

Frontend
- `features/processing/ProcessingPage.tsx`, `paths.ts`, `processingApi.ts` — product filter in the address.
- `features/processing/ProcessingTaskPage.tsx` — link "tasks of this product"; `components/AttemptsTable.tsx` — label
  of `stale_no_progress`.
- `features/logs/systemEventsApi.ts`, `eventSummary.ts`, `shared/i18n/vi.json`, `en.json` — the sweep's event.
- Tests: `processing.test.tsx`, `processingTask.test.tsx`, `logs.test.tsx`, `test/systemEvents.ts` (+6 tests).

Operations and documents
- `docker/docker-compose.prod.yml`, `scripts/deploy/_stack.ps1` — A2 digests.
- `scripts/smoke/task-026b.ps1` (new), `scripts/load/run.ps1`, `reserve.js`, `invariants.sql` (new),
  `scripts/perf/seed-task-026b.sql` (new: the seed of the measured database).
- `docs/runbook.md` (section 4 "Updating a third-party image", section 8 rewritten), `README.md` (section 6 "Stale
  tasks", product filter, smoke 026b, "Load test of the reserve path").

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend, 1872 tests; 1825 before) | **not green in a single local run**: eight full runs gave 1866–1870 passed, each with 2–4 failures of *different* tests. The first run had two real ones (two tests that list every event type; fixed) — every other failure is a connection that could not be opened (`Npgsql ... The operation has timed out`, twice `too many clients`, twice RabbitMQ `connection.start was never received`); see below. **CI on GitHub: all backend tests green** on `3ecd073` and `a980747` |
| the failed tests again, alone | 78 / 78 (`--filter` of the four names + `SystemEvent`) |
| `dotnet test --filter ReconciliationTests` on a checkout of `0d7abc5` (before this task), twice | 41 / 42 and 41 / 42 — the same timeout, another test each time |
| new tests alone (`StaleTask` + the reconciliation test) | 33 / 33 |
| negative control A1 (new tests against the old `Program.cs` / middleware) | 4 of 7 fail (both 413, both 500), as they must |
| five targeted mutants of C | 4 killed (guard removed, age not re-checked under the lock, `RetryScheduled` not swept, newest first); "boundary inclusive" changed nothing a caller sees (the check under the lock draws the same line) — now pinned by `Stale == 2` |
| frontend `npm run typecheck`, `npm run lint` | exit 0, exit 0 |
| frontend `npm test` twice, `npm run test:slow` | 524 / 524, 524 / 524, 524 / 524 (518 before) |
| `npm run build`, `npm run measure` | built; first-load set 970.29 → 970.30 kB raw, 318.22 → 318.21 kB gzip |
| CI (GitHub Actions) | green: run 38387301610 (`3ecd073`), and the run of `a980747` |
| `dotnet ef migrations list` (development, before) | only `20261013092919_PerformanceIndexes (Pending)` |
| `omni026b`: `task-025.ps1` | PASSED: all 54 checks (steps 01–53 with 30b and 41b) |
| `omni026b`: `task-026.ps1` | PASSED: all 9 checks |
| `omni026b`: `task-026b.ps1` with `SMOKE_STALE_DRILL=1` | PASSED: all 29 checks |
| development: `task-026b.ps1` | PASSED: all 16 checks |
| `omni026b`: `scripts\load\run.ps1 -Scenario all` | PASSED: every expectation and every invariant (24 + 24 rows) |
| `omni026b` removed | `down -v` of that project, its three images by tag, its env file, the k6 image; nothing left (`docker ps -a`, volumes, networks, images) |

**The local backend suite.** The first two runs also showed `53300: too many clients already` in one Order Worker
test: my sweep tests each had a database of their own and so a connection pool of their own. They now share one
database per class (emptied before each test); that error did not come back. What remains are connect timeouts of
single tests in the collections that open many short connections (mostly `ReconciliationTests`, which this task does
not change). They appear on `0d7abc5` too — a commit whose suite was 1825 / 1825 yesterday — so they are the machine
since today's WSL upgrade (WSL 3.0.3, Docker restarted), not the code. Not fixed here (question 4).

### A1 — cross-origin error answers
Pipeline now: forwarded headers → correlation id → security headers → request log → exception handler → status code
pages → (HSTS) → localisation → HTTPS redirect → **CORS → body limit → rate limiter** → authentication → authorisation.
Why: the CORS middleware adds its headers when the answer starts, so everything after it that answers — the 413 of
the limit, the 429 of the limiter, a 401 / 403, and what the exception handler writes for a failure further in —
carries them; the redirect stays first (a plain-HTTP request gets no content); the limit stays ahead of the rate
limiter (a refused body is not read and uses no permit) and of everything that reads a body.

Smoke `task-026b.ps1` (development = straight to Kestrel; `omni026b` = through Caddy):

| Check | Development | `omni026b` (proxy) |
| --- | --- | --- |
| 401 from the frontend origin: `Access-Control-Allow-Origin`, exposes and carries `X-Correlation-Id`, body id = header | yes | yes |
| 413, announced length | yes, `request_body_too_large` | 413; readable **only when the API answered** (question 1) |
| 413, no length | yes (Kestrel cuts the body, the problem document is readable) | the proxy's bare 413 (known, runbook 15) |
| 413 for another origin | no CORS header | no CORS header |
| 429 of the login limit | yes | yes |
| 500 | integration test only (cannot be provoked on a live API) | — |

### B2 — seeded database, before / after
Throwaway `postgres:17` with `auto_explain` (every statement the API really sends is logged with
`ANALYZE, BUFFERS`), migrated, seeded by `scripts/perf/seed-task-026b.sql`: 8,000 orders, 24,000 tasks (240
unfinished, 40 of them stale), 24,720 attempts, 40,000 outbox rows (30 pending, 13 failed), 2,000 alerts. The
statements below are the ones captured from `GET /api/dashboard/summary`, `GET /api/processing/tasks` (16 filter
combinations) and the sweep (its text is pinned by a test), replayed with the parameters as constants, warm cache.

| Statement | Before | After | Plan after |
| --- | --- | --- | --- |
| Summary: tasks by type and status (unfinished, or finished in the range) | 5.41 ms, seq scan of 24,000 | 2.16 ms | `BitmapOr` of two ranges of `IX_ProcessingTasks_Status_UpdatedAt` |
| Summary: failed outbox rows | 9.50 ms, 882 buffers, seq scan of 40,000 | 0.07 ms, 2 buffers | index-only scan of `IX_OutboxEvents_Failed_CreatedAt` |
| Summary: stock sync per channel (`lastSucceededAt`) | 5.22 ms, seq scan | 5.90 ms, seq scan | unchanged — see question 3 |
| Sweep: unfinished and unchanged for 30 min | 3.33 ms, seq scan | 0.28 ms | index scan of `IX_ProcessingTasks_Status_UpdatedAt` (240 rows), age filtered |
| Task list `status=Pending` (count) | 1.54 ms, seq scan | 0.07 ms | index-only scan |
| Task list `status=Failed` (count) | 1.45 ms, seq scan | 0.14 ms | index-only scan |
| Task list `productId=` (page) | 0.14 ms, `IX_ProcessingTasks_ProductId` | 0.10 ms | unchanged |
| Task list `productId=&status=Failed` (count) | 0.08 ms | 0.11 ms | `BitmapAnd` of the two indexes |
| Task list unfiltered (page) | 4.59 ms, seq scan + top-N sort | 5.44 ms | unchanged — see question 3 |

Candidates of the task, measured as hypothetical indexes in a transaction that was rolled back:
- **`OutboxEvents` partial on `Status = 'Failed'`** — used, 9.5 ms → 0.07 ms. **Added** (`("CreatedAt") WHERE "Status" = 'Failed'`).
- **Partial index of the unfinished tasks** — serves the sweep (0.33 ms) and `status=Pending`, but **not the summary**:
  its statement is `unfinished OR (finished AND UpdatedAt in range)`, and with an index for only one arm of the OR
  the planner keeps the sequential scan (measured). One index `("Status", "UpdatedAt")` serves both arms, the sweep
  and the status counts — **added instead** (deviation 2).
- **Partial `(ChannelId, UpdatedAt DESC) WHERE StockSync AND Succeeded`** — **not used** by the summary's statement
  (it aggregates over every stock-sync task in one pass). **Not added.**
- **`(ProductId, CreatedAt DESC)`** — used, 0.14 → 0.08 ms at 28 tasks per product: the existing `ProductId` index is
  enough. **Not added.**

Migration: `CreateIndex` twice; plain `CREATE INDEX` inside the migration's transaction (production tables are tiny:
25 tasks, 26 outbox rows — `CONCURRENTLY` is not needed). `dotnet ef database update` took 4.0 s on the seeded
database and 4.3 s on development (tool start included).

**Unused indexes — listed only** (`pg_stat_user_indexes`, `idx_scan = 0` after the seed, both measured runs and the
replays; only the summary, the task list and the sweep ran, so this says nothing about indexes of other pages):
`IX_Alerts_TaskId` (64 kB), `IX_Alerts_OrderId` (32 kB) — review 017's two; `IX_ProcessingAttempts_Status_StartedAt`
(1,200 kB — the largest index of that table, no statement of these pages or of the workers' bookkeeping reads it);
`IX_Alerts_ChannelId`, `IX_Alerts_ProductId`, `IX_Alerts_Status_LastOccurredAt`, `IX_Orders_Status_CreatedAt` (584 kB)
belong to the alert and order lists, which were not exercised. Used: `IX_ProcessingTasks_TaskType_Status_CreatedAt`
(14 scans), `_ChannelId` (28), `_ProductId` (26), `_CorrelationId` (8), `_OrderId` (8), the new one (16).

### B3
Nothing to change: the roadmap row 026b and the log ("Task 026b plan") already say that `ReportExports` rows are kept.

### C — stale-task drill on `omni026b`
Workers recreated with a scratch override (`RabbitMq__RetryDelays` 2 / 4 / 6 / 8 s, `OutboxPublisher__MaxBackoff` 4 s,
`StaleTasks__Interval` 10 s, `StaleTasks__StaleAfter` 70 s — the shortest the validation allows there). Script steps
15–27: Workers stopped → order submitted (task `Pending`, outbox row `Pending`) → the row marked `Published` by hand
in the stack's PostgreSQL ("sent as far as the database knows, never delivered") → Workers started → the task `Failed`
by itself (`StaleTaskSweep`, `stale_no_progress`), an open `ProcessingFailed` alert, the run's event naming the task →
"Retry" 202 → `Succeeded` with exactly one Order Worker attempt after the sweep's → order `Reserved`. Workers log:
`Stale-task run ...: 1 stale, 1 failed, 0 touched meanwhile and left alone, 0 not written`.

### E — load test (run `1d47007b`, two Workers, everything on this machine)
| | Contention | Spread | Duplicates |
| --- | --- | --- | --- |
| Requests / time / rate | 300 / 0.3 s / 877 per s | 2,001 / 100 s / 20.0 per s | 200 / 0.6 s / 319 per s |
| Intake answer p50 / p95 / p99 / max | 31 / 58 / 65 / 75 ms | 8 / 22 / 39 / 270 ms | 102 / 155 / 191 / 230 ms |
| Intake answers | 202: 300 | 202: 2,001 | 202: 200 |
| Intake → finished p50 / p95 / max (task timestamps) | 2,221 / 3,041 / 3,090 ms | 316 / 612 / 716 ms | 686 / 824 / 839 ms |
| Order Worker throughput / attempts | 88 tasks/s / 300 | 20.0 tasks/s (the arrival rate) / 2,001 | 74 tasks/s / 100 |
| Orders | 100 `Reserved`, 200 `InsufficientStock` | 2,001 `Reserved` | 100 `Reserved` |
| Stock total / reserved / available | 100 / 100 / 0 | 10,000 / 6,002 / 3,998 | 1,000 / 100 / 900 |
| `postgres` CPU max / memory max | 103 % / 192 MiB | 62 % (avg 39 %) / 206 MiB | 142 % / 202 MiB |
| `rabbitmq` CPU max / memory max | 41 % / 178 MiB | 211 % (avg 40 %) / 221 MiB | 237 % / 239 MiB |
| `workers` (each) CPU max / memory max | 68–72 % / 145 MiB | 36–40 % (avg 27 %) / 147 MiB | 27–81 % / 144 MiB |
| Unfinished tasks, waiting messages, messages in a queue or DLQ | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| Invariants (8 rows of `invariants.sql`) | all 0 violations | all 0 | all 0 |

A first complete run (`8b5864b0`) gave the same outcomes with contention 518 req/s, answers p50 45 / p95 125 ms,
intake → finished p50 2,448 / p95 3,149 ms, 80 tasks/s; spread p50 503 / p95 969 ms. In the two sub-second scenarios
`docker stats` (sampled every 2 s) got one sample each: read those CPU figures as a single reading.
Nothing looks like a defect. What limits the burst is the one inventory row all 300 orders lock in turn (the reserve
transaction is serialised per product by design): 300 orders of one product take about 3.4 s end to end; no tuning.

## Deviations from the task
1. **A1, "413 chunked, straight to Kestrel" as an integration test:** the test host (TestServer) reads a body of any
   length, and a second, real host cannot be built in the test process (Serilog's bootstrap logger). A start-up filter
   of the test host does what Kestrel does (the read fails with a 413 `BadHttpRequestException` at
   `Limits.MaxRequestBodySize`), so the application's answer is tested; the real server is in the smoke on
   development (step 04, straight to Kestrel).
2. **B2 indexes:** one `("Status", "UpdatedAt")` instead of the partial index of unfinished tasks, and no
   `lastSucceededAt` index and no `(ProductId, CreatedAt)` — by the measurements above ("add only what an `EXPLAIN`
   shows is used and helps").
3. **C validation is stricter than the task's wording:** `StaleAfter` ≥ 2 × max(longest retry delay, the time the
   Outbox Publisher keeps trying one message = the *sum* of its backoffs, 511 s with the defaults — not only its
   longest backoff of 5 min): while the publisher still retries, the task is healthy. Defaults: 2 × 10 min = 20 min ≤
   30 min. It is checked for a switched-off sweep too.
4. **C, first run:** one whole `Interval` after start-up (reconciliation: `min(Interval, 60 s)`): after a time
   without Workers the queues hold the messages of tasks that look stale, and the consumers get that long to start
   them. `Interval` may be as short as 1 s (reconciliation: 10 s) — needed for a drill.
5. **C, the old-message guard** is in `ProcessingAttemptStore.StartAttemptAsync` under the task's row lock (a new
   parameter in four service interfaces), not in the handlers: there it cannot race a manual retry. A superseded
   message ends as `Ack` with a log line of the store.
6. **D, link "tasks of this product":** on the task detail page (next to the SKU of a stock-sync task). There is no
   product detail page, and the row actions of the Products and Inventory lists fill their fixed action columns.
7. **A2:** the first release with digests recreated every third-party container once — the `image:` text changed
   although the image did not. From now on an unchanged digest recreates nothing.
8. **E:** the spread scenario sends 2,001 orders (20 per second for 100 s starts an iteration at both ends); the
   script expects "orders = accepted requests, at least 2,000".
9. **Smoke 026b through a proxy** asserts only the status of the 413 for an announced length and says who answered
   (question 1); on development it asserts the whole readable answer.
10. **Sweep tests share one database per class** (emptied before each test) instead of one per test.
11. **Not in the task:** before any production command of Part 0, `docker desktop restart` (the engine was stopped
    after the WSL upgrade; no reset, all volumes intact). And during the analysis of question 1 — after Part 0's
    go-ahead, before Part G's — I sent 30 requests with an oversize body to production's local proxy port
    (`127.0.0.1:8080`, each refused with 413, nothing written but 30 access-log lines) to compare with the throwaway
    stack. Read-only like `task-025b.ps1`, but not a step of a part: said here.

## Blocked / questions for Cowork
1. **An oversize body through the proxy is mostly answered by the proxy, not by the API — so that 413 is still not
   readable cross-origin on the live path.** Caddy has the same limit and refuses first: 30 requests with
   `Content-Length: 131073` and an `Origin` through each proxy — `omni026b` (026b code): API 7, proxy's bare 413 23;
   with 1 MiB: 9 / 21; production with the 026 code: 2 / 28 (curl). The proxy's 413 has no CORS header, no body and
   no correlation id; sometimes the client only sees the connection closed. The live check `task-025b.ps1` steps
   21–23 expect the API's document and are therefore not stable: of five runs after the release two were green, two
   failed there with "no HTTP answer", one failed at step 25 because my repetitions used up the login limit (its
   single run after Part 0 was green; `HttpWebRequest` sends `Expect: 100-continue`, which lets the API win more
   often than curl does). The API side of A1 is done and tested; the rest is a proxy decision the task does not
   settle: (a) Caddy answers its 413 with the CORS headers of the frontend origin and a problem body
   (`handle_errors`; the origin would be configured twice), (b) the proxy's limit is set clearly above the API's (the
   API then decides; the test that compares the two numbers changes), or (c) accepted as it is — forms are far below
   128 KiB. Whatever is chosen, `_hardening.ps1` should stop expecting one outcome of a race.
2. **Sweep after a long time without Workers:** tasks whose messages still wait in the queues can be failed when the
   consumers do not reach them within the first interval (5 min); the operator retries them, the waiting message is
   dropped when it arrives (runbook 8). Accepted as designed, or should the sweep skip a run while a main queue still
   holds messages (it would need the broker's management API in the Workers)?
3. **Two query shapes no index helps** (evidence in B2): (a) the summary's stock-sync statement reads every
   stock-sync task (5 ms at 14,000) — `lastSucceededAt` per channel needs its own small statement to use an index;
   (b) the task list's page computes "last attempt" with `ROW_NUMBER()` over **all** attempts (seq scan + sort of
   24,720 rows: about 35 of the 47 ms of the unfiltered page, growing with the table) and sorts all tasks for the
   unfiltered page (5 ms; `(CreatedAt, TaskId)` would serve it). Follow-up task?
4. **Local backend suite and the machine:** connection timeouts to the test containers since the WSL upgrade, also on
   `0d7abc5` (above). A `wsl --shutdown` / Docker restart might cure it but stops production for a minute — the
   user's call. Also seen in passing: the CI run of `294cb91` (yesterday's report commit, documents only) is red on
   GitHub; not looked into.
5. **Unused indexes** (B2 list): `IX_ProcessingAttempts_Status_StartedAt` is the largest candidate; dropping is out
   of scope here.

## Development steps (applied)
| Step | Result |
| --- | --- |
| `dotnet ef migrations list` as the migrator (connection from the user-level variable, never printed) | only `20261013092919_PerformanceIndexes (Pending)` |
| the migration on throwaway databases first | seeded plan database (4.0 s) and `omni026b` (15 migrations from empty) |
| `dotnet ef database update` as the migrator, development | `Applying migration '20261013092919_PerformanceIndexes'. Done.` (4.3 s); both indexes exist; list shows nothing pending |
| `scripts\smoke\task-026b.ps1` on development | PASSED: all 16 checks (it started and stopped its own API) |
| containers | the development containers were stopped for some of the test runs (to rule out memory pressure) and started again (`up -d --wait`, healthy); no change of `docker/docker-compose.yml`, no storage key |

No host of mine is running; the scratch plan database, the comparison checkout and `omni026b` are gone.

## Production steps (applied)
Part 0 (release of 026): in report 026. **Part G — release of `a980747`** (runbook 4), after the user's go-ahead in
the chat ("yes, if E and CI are green"):

| # | Step (command, no secret) | Result |
| --- | --- | --- |
| 0 | `git log production..origin/master`; `git diff --stat ... Migrations`; applied migrations (runbook 5); tasks by status | 11 commits; one migration (`PerformanceIndexes`); 14 applied, 15 in the checkout; production: 25 tasks, all `Succeeded`, 26 outbox rows `Published` |
| 1 | `$env:GIT_COMMIT = git rev-parse HEAD`; `set-image-tag.ps1 -EnvFile docker\.env.prod` | `IMAGE_TAG: 0d7abc5f5f16 -> a980747a49fc` |
| 2 | `docker compose @stack --profile migrate build` | exit 0 in 5 s (cached layers of the drill build of the same code); revision label `a980747…`; `config --images` shows the four digests |
| 3 | `docker compose @stack --profile migrate run --rm migrate` (18:35:38) | recreated `postgres` (new `image:` text), `Applying migration '20261013092919_PerformanceIndexes'. Done.` — 9 s for the whole step |
| 4 | `docker compose @stack up -d --wait` | healthy at 18:36:20; `api`, `workers` x2 on `:a980747a49fc`; `objectstorage`, `rabbitmq`, `proxy` recreated once (deviation 7); restart count 0 everywhere; `/health` live 200 |
| 5 | `git push origin a980747…:refs/heads/production` | `0d7abc5..a980747`; Vercel served the new build about a minute later (new main chunk) |
| 6 | `task-026.ps1` live | PASSED: all 9 checks |
| 6 | `task-025b.ps1` live, five runs | run 1: 23 / 26 (steps 21–23, no HTTP answer to the oversize body); runs 2 and 3: **26 / 26**; run 4: 23 / 26 (the same three); run 5: 25 / 26 (step 25 answered 429 — my repeated runs had used the login limit). Question 1 |
| 7 | old images | kept: `:a980747a49fc` (running) and `:0d7abc5f5f16` (rollback); nothing older exists |

- **Downtime:** 42 s, from the start of the migrate step (which recreates `postgres`) to every service healthy
  (18:35:38 → 18:36:20).
- **Migration:** two `CREATE INDEX` on 25 and 26 rows, inside the 9 s of step 3; both indexes exist; 15 migrations applied.
- **Stale-task sweep on production:** both Workers log `Stale-task sweep started: every 00:05:00, a task without
  progress for 00:30:00 is failed, at most 200 per run`. Its first run (18:41) **failed nothing**: 25 tasks, all
  `Succeeded` before and after, no attempt of `StaleTaskSweep`, no `processing.stale_tasks_failed` event.
- API and Workers log after the start: the three expected DataProtection warnings, nothing else at Warning or above.
- Rollback: a migration ran, so only forward (runbook 4). The indexes do not prevent the previous images from
  running if it ever had to be tried (`set-image-tag.ps1 -Tag 0d7abc5f5f16` — with the checkout of that commit for
  its compose file).

## Manual steps for the user
1. Still open from 026: the live browser check with the console open (login, dashboard, upload a product image,
   download an export, open an order) — report any "Content Security Policy" message. Now also worth a look: Sync &
   Processing → the product picker, and Logs → the event type "Stale tasks failed" in the filter.
2. Decide question 4 (restart WSL / Docker to cure the test timeouts — a minute of production downtime) or leave it.
3. Nothing else: development is migrated, production is released, every throwaway environment is removed.

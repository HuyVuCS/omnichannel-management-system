# Task 027 — Acceptance end to end (doc §16): follow-ups of 026c, acceptance script, browser e2e suite, CI job, acceptance matrix, final release

Status: Open
Written by: Cowork, 2026-10-14
Depends on: 026c (accepted — `docs/tasks/026c-storage-hygiene-forms-accessibility.review.md`)

**This is the last planned task of the roadmap** (user, 2026-10-14: complete, not split further). It is not split
into 027b: if a part cannot be finished, the report says which and why (`Partially done`), and the other parts are
still finished. Only 025c (Oracle VM) stays, optional.

## Context
Doc §16 (acceptance criteria), §8 (flows), §14–15; CLAUDE.md; roadmap "Acceptance criteria coverage". Production has
**no backup**; the guardrails of `docs/tasks/README.md` "Production steps" are absolute. Part G releases to production
and runs the live acceptance subset with the user's go-ahead.

Read first: review 026c (findings F1, F2, questions 1, 3, 7); `ProductImageService.cs`, `DashboardService.cs`,
`shared/components/PageHeader.tsx`, `app/providers.tsx`, `shared/components/layout.ts`, `test/accessibility.ts`;
`scripts/smoke/_common.ps1`, `_hardening.ps1`, `task-014.ps1`, `task-017.ps1`, `task-025.ps1` (WebSocket),
`task-026c.ps1`; `scripts/load/run.ps1`, `invariants.sql`; `ChannelSimulationOptions.cs`;
`docker/docker-compose.prod.yml`; `scripts/deploy/new-prod-env.ps1`; runbook 4 (throwaway stacks), 15, 16;
`.github/workflows/ci.yml`.

### Stop and report (`Blocked`) if
- a migration other than the optional partial index of A2 appears, or any migration is not additive;
- an acceptance check fails because of a **product defect** whose fix changes a flow, an API contract or the data
  model — report it with the evidence; fix only local defects (and say so in the report);
- a check can only pass by weakening it (longer timeouts that hide a race, a skipped assertion, a retry until green)
  — the rule of log "Task 017 result" applies to the e2e suite and the scripts as to unit tests;
- Part G would write anything to production beyond what G lists.

## Part A — Follow-ups of review 026c

### A1. Repeated confirmation that loses the race (F1)
In `ProductImageService.ConfirmAsync`, when the upload is gone (`HEAD` of the staging key is null, or `CopyAsync`
returns false), read the product again: `ProductImageKey.IsFinalOf(current, uploadId)` → answer 200 with the product
(the image is that upload); otherwise the existing 400 `image_not_uploaded`. Test: two confirmations of one upload,
the second held (storage hook) after its first read until the first committed and deleted the staging object → both
200, one image, no object left over. A mutant (the re-read skipped) must be killed.

### A2. `lastSucceededAt` of a channel without a successful sync (F2)
Measure with `scripts/perf/seed-task-026b.sql` **plus** one channel with stock sync switched off and no successful
stock-sync task (`auto_explain`, three rounds after a warm-up, as in 026c A2). If the channels statement of the
summary costs ≥ 2 ms more than with successes everywhere: partial index
`("ChannelId", "UpdatedAt") WHERE "TaskType" = 'StockSync' AND "Status" = 'Succeeded'` (EF `HasFilter`), the one
migration of this task (additive; plan before / after in the report). Otherwise record the numbers, no index.

### A3. Visible focus (q1)
One global rule (in the application's stylesheet, not a theme token): `:focus-visible` → `outline: 2px solid
#0958d9; outline-offset: 2px` for the application's focusable elements, sortable column headers (`th` with
`tabindex`) included; nothing that changes `colorPrimaryBorder`. Remove only focus styles that would now double up.
Verified in the browser by the e2e suite (C4) — computed outline on a button, a link, a menu item, an input, a column
header.

### A4. One `<h1>` per page (q3)
`PageHeader` renders its title as `Typography.Title level={1}` with the **current** look (font size, weight and line
height of level 4 set on the element; nothing moves, no layout change). The login page and the route error page get an
`<h1>` too. Test (Vitest): every route of the router renders exactly one `<h1>`; the e2e axe run (C4) has
`page-has-heading-one` clean.

### A5. Chunked oversize body through the tunnel (q7, option a)
`_hardening.ps1`: the **chunked** 128 KiB + 1 step accepts 413 **or 502** only when the target is a public origin
(not loopback — decided from `BaseUrl`, one helper, commented with review 026c q7); against loopback and throwaway
stacks it stays strict (readable 413 with CORS header, problem document, correlation id). The announced-length step
stays strict everywhere. `task-025b.ps1` steps 23–24 follow.

## Part B — Acceptance script `scripts/smoke/task-027.ps1`

One script that walks doc §16 **criterion by criterion** against a running stack and prints, besides the usual step
table, a second table *criterion → steps → result*. Built on `_common.ps1` (Windows PowerShell 5.1, ASCII only; see
D for pwsh on Linux). Smoke data is named `ACC027-{n}-…`.

### B0. Modes
- `-Mode Full` (throwaway stack, also CI): every block, including the drills (B7 DLQ, B3/B4 load scenarios, B1 webhook).
- `-Mode NoDrills` (development, live): no load scenario, no container control, no DLQ drill when the simulator's SKU
  rules are off (detected, reported as `skipped: SKU rules off`, never as passed).
- The script refuses `-Mode Full` against the compose projects `omnichannel-prod` and `omnichannel` (like
  `run.ps1`) and refuses any write against a public origin unless `-Live` is given explicitly (G).

### B1. Orders from Website / POS / Digital Platform; normalised; on RabbitMQ (criteria 1, 2)
- One order through `POST /api/orders/{slug}` for each of the three slugs (Staff), each → task `Succeeded`, order
  `Reserved`, the task's message id set, an attempt by the Order Worker with its instance id.
- `Full`: one order through `POST /api/webhooks/{slug}` (signed with the throwaway stack's secret, as
  `send-webhook.ps1`) → the same normalised order shape (`GET /api/orders/{id}`: channel, customer, items by product).
- Published: the order's outbox row `Published` with `PublishedAt` (`Full`: read-only `psql` in the stack's
  PostgreSQL container; `NoDrills`: the dashboard summary's pending outbox count returns to its value before).

### B2. Duplicates (criterion 3)
Same `externalOrderId` twice on one channel → one order, the second answer as the API defines it today (assert the
documented status / body, no new behaviour); the same id on another channel → a second order. `Full`: the
`duplicates` scenario of `scripts/load/run.ps1` at a small size (≤ 100 requests).

### B3. Concurrency, stock figures, movement + outbox in one transaction (criteria 4, 5, 6)
- `Full`: the `contention` scenario of `run.ps1` at a small size (e.g. 60 orders, stock 20) → exactly stock-many
  reserved, the rest rejected.
- `invariants.sql` extended (read-only) with: every `Reserve` movement of an order has exactly one outbox event and
  every `inventory.changed` outbox event has its movement; no inventory row whose figures disagree with its movements
  (Total = sum of import movements − …, as the movement types define it — write the formula from the code, not from
  this sentence). Run in `Full` after all blocks: **0 violations** in every row.
- `NoDrills`: the product of B1: `TotalStock / ReservedStock / AvailableStock` before and after its order, by the API.
- Name, in the matrix (E1), the integration test that proves a failure inside the reserve transaction leaves neither
  movement nor outbox row (exists since 006; if it does not, write it).

### B4. Stock sync with retry and DLQ (criterion 7)
- Simulator: new SKU prefix **`SIM-DOWN-`** — `channel_unavailable` on **every** attempt (SKU rules only, like the
  other prefixes; unit test). The existing `SIM-TEMP-` stays the retry-then-success case.
- `SIM-TEMP-` mapping → task `Succeeded` with two attempts (first `channel_unavailable`).
- `Full`: `SIM-DOWN-` mapping → retries to the limit → task `Failed`, alert open, the message in
  `stock.sync.<slug>.dlq` (`rabbitmqctl list_queues` in the stack's container, read-only) → the mapping corrected and a
  manual retry (`POST .../retry`, Manager) → `Succeeded`, alert resolved. The throwaway stack runs with short retry
  delays (B8) so this takes seconds.

### B5. Near-real-time and traceability (criteria 8, 9)
- A WebSocket client on `/api/hubs/notifications` (as `task-025.ps1`) receives the hints of an order of B1
  (the hint names the code defines for it — e.g. `processing.task.changed`, `inventory.changed`) within 5 s of the
  task's end.
- `GET /api/orders/{id}` of an order of B4's failing product and its task detail: task id, type, status, worker name,
  worker instance id, attempt numbers, error code / message of the failed attempts, correlation id;
  `GET /api/system-events?correlationId=…` lists that flow's events.

### B6. Low stock, history (criteria 10, 11)
- Product with threshold 5 and stock 6 → order of 2 → low-stock alert `Open` (type, product); stock import of 10 →
  the alert resolved by the system.
- Movement history of that product: import, reserve, import, in order, with document data; processing history of its
  tasks with attempts.

### B7. Excel export, images in S3-compatible storage (criteria 12, 13)
- Inventory export (Manager) → task `Succeeded` → download link → the file is an `.xlsx` (ZIP signature), its sheet
  contains the SKU of B6 (read the sheet XML from the archive; no Office needed).
- Image: presign → `PUT` → confirm → product response with a link → `GET` returns the uploaded bytes; the product row
  stores only key / type / size (no binary column exists — asserted by the shape of the response and, in `Full`, a
  read-only `psql` of the column list).

### B8. Authentication and authorization (criterion 14)
- No token → 401; a forged token (wrong signature) → 401; an expired one if the test can make it without a secret,
  otherwise skipped with the reason.
- Role matrix, one representative endpoint each: Staff → order intake 2xx, product create 403, users 403, payment
  403, alert acknowledge 403, export 403; Manager → payment / acknowledge / export 2xx, users 403; Admin → users 2xx.
  Each cell asserted; the matrix printed. The cells are what the code defines today (log, tasks 008–024b): a cell of
  this list that disagrees with the code is reported, not "fixed".

### B9. Throwaway stack for `Full`
`docker/docker-compose.acceptance.yml` — an override used **only** for throwaway stacks and CI (never with
`omnichannel-prod`; the script and the runbook say so): Workers with `ChannelSimulation__SkuRules=true`, short
`RabbitMq:RetryDelays` (e.g. 1 s / 2 s; within the validation of 007 and the `StaleAfter` rule of 026b), everything
else as production. Fresh broker only (retry delays shape the topology).

## Part C — Browser end-to-end suite (Playwright)

### C1. Tooling — decision of this task
`@playwright/test` as an **exact-pinned dev dependency** (a version out ≥ 2 weeks, as axe-core in 026c), Chromium
only; `frontend/e2e/*.spec.ts`, `frontend/playwright.config.ts`, script `npm run e2e`. Excluded from Vitest, from the
type-check of the app build where needed, and from the bundle (`npm run measure` unchanged). Reason: criteria 1 and 8
are statements about the UI in a real browser; jsdom tests and manual browser passes (016, 026c) are not repeatable
evidence. Alternatives considered: keep manual browser passes (not repeatable, not in CI); Cypress (heavier, no gain).
Revisit: never for this project's size.

Configuration from the environment, never from files in the repository and never printed: `E2E_BASE_URL` (frontend),
`E2E_API_URL`, `E2E_ADMIN_EMAIL`, `E2E_ADMIN_PASSWORD`. The suite creates its own Staff / Manager users with random
passwords (as the smoke scripts) and its own `E2E027-…` data. Traces / screenshots only on failure; they contain no
token (check the trace settings: no storage state, no request headers with the bearer token in the HTML report — if
Playwright cannot be made to leave them out, traces off).

### C2. Specs (each starts from a fresh login)
1. **Order entry** (criterion 1): Staff enters one order in each of the three panels (Website, POS, Digital Platform);
   each shows the outcome `Reserved` (by push); the three orders are in the Orders list.
2. **Realtime** (criterion 8): dashboard open; an order created through the API in another context → the dashboard's
   figures change **without reload** within 5 s; connection indicator live. With `E2E_STACK_CONTROL=1` (throwaway
   only): `docker compose restart api` → indicator reconnecting → live → figures equal to a fresh API read (resync).
3. **Trace** (criterion 9): from an order's detail to its task and attempts (worker, instance, attempt no, error of a
   failed attempt — the `SIM-DOWN-` product when SKU rules are on), then the Logs page filtered by its correlation id.
4. **Alerts** (criterion 10): the low-stock alert on the Alerts page and the dashboard card; Manager acknowledges.
5. **Export** (criterion 12): Manager requests an inventory export on Reports → status `Succeeded` live → the
   download yields a file starting with `PK`.
6. **Image** (criterion 13): Manager uploads an image in the product dialog → thumbnail loads (`naturalWidth > 0`).
7. **Access** (criterion 14): Staff has no Users menu; `/users` by address → the forbidden page; sign-out → login;
   a protected address while signed out → login.

### C3. Stability
No fixed sleeps; web-first assertions with the default timeout; a spec that needs the realtime push asserts the push
(no reload as a fallback). Run the suite **five times in a row** against the throwaway stack: all green each time
(report the five results).

### C4. Accessibility in a real browser
After each spec's main view (and every menu page once, signed in as Admin): axe-core (the existing dev dependency,
injected with `page.addScriptTag`) **with `color-contrast` on**; `serious` / `critical` fail, with the one documented
exception of `test/accessibility.ts` (shared, not copied). This replaces the manual contrast check of 026c. Plus the
focus check of A3 and `page-has-heading-one` of A4.

## Part D — CI job `acceptance`
`.github/workflows/ci.yml`, job `acceptance` (push to `master` and `workflow_dispatch`; not on pull requests;
`timeout-minutes: 40`; needs no secret):
- a CI-only env file generated in the job with random values (`openssl rand`), never echoed, never uploaded;
- the production compose file + `docker-compose.acceptance.yml`, `migrate`, `up --wait`; first Admin from the
  generated values;
- frontend built with `VITE_API_BASE_URL` / `VITE_STORAGE_ORIGIN` of the stack, served by `vite preview`, its origin
  as `FRONTEND_ORIGIN`;
- `pwsh scripts/smoke/task-027.ps1 -Mode Full` and `npm run e2e`;
- on failure: Playwright report and `docker compose logs` (both checked to contain no secret: the env file is not in
  them, tokens are not logged — the API never logs them) as artifacts, 7 days.
Portability: `task-027.ps1`, `_common.ps1`, `run.ps1` must run under pwsh 7 on Linux as well as Windows PowerShell
5.1 — path joins, no Windows-only cmdlets, `docker` invoked the same way. If a script needs more than portability
fixes to run on the runner, the CI job runs the e2e suite only and the report says why (`Partially done` for D only).
The job must be green twice (two pushes or a dispatch) before G.

## Part E — Acceptance matrix, documents, close-out

### E1. `docs/acceptance.md` (new)
Per criterion of doc §16 (Vietnamese text of the doc + English): what proves it — the backend tests (class / test
names), frontend tests, `task-027.ps1` blocks and steps, e2e specs, load-test baseline (log "Task 026b result"), and
the live evidence of G — with the date and commit of the run. The last criterion ("tests for the important flows")
lists the flows the project instructions name (reservation, concurrent orders, oversell, idempotency, retry, DLQ,
outbox, task tracking, attempt tracking, authN/Z, API validation) each with at least one test; a flow without one is
a gap: write the test (or `Blocked` if it is not a local change).

### E2. Known limitations (in `docs/acceptance.md`)
One honest list: simulated channels / invoice provider / mail sender / payment; no production backup (user decision);
uptime = the user's machine and Tailscale Funnel; one PostgreSQL / RabbitMQ instance; the AntD listbox exception;
recorded "revisit when" items of the log (indexes, `ReportExports`, etc.) by reference.

### E3. README, runbook, roadmap
README: "Acceptance" section (how to run `task-027.ps1` and `npm run e2e` locally, on a throwaway stack, what CI runs).
Runbook: the acceptance override is for throwaway stacks only; how to run the live subset. Roadmap: no change by
Claude Code (Cowork closes it in the review).

## Part F — Verification
- Backend 0 warnings, full `dotnet test`; frontend typecheck / lint / test ×2 / slow / build / `measure`; CI green
  including `acceptance` (twice).
- Throwaway stack `omni027` (own tag / ports / subnet, production compose + acceptance override, two Workers):
  `task-025.ps1`, `task-026.ps1`, `task-026c.ps1`, `task-027.ps1 -Mode Full` (all blocks), `npm run e2e` ×5 (with
  `E2E_STACK_CONTROL=1`). Removed with `down -v` of that project only.
- Development (development steps of `docs/tasks/README.md`): migration if A2 added one; `task-027.ps1 -Mode NoDrills`;
  `npm run e2e` once against the dev server.
- Mutants: A1 (re-read skipped), B4 (`SIM-DOWN-` succeeds), one invariant of B3 deliberately broken in a throwaway
  database (the script must go red) — each killed.

## Part G — Release and live acceptance (runbook §4), with the user's go-ahead
1. Before G, tell the user: what is released (A1–A5; the A2 migration if any), the downtime expected, and that the
   live acceptance run writes `ACC027-` / `E2E027-` data (a few users, products, orders, one image, one export) into
   production and leaves it there (demo data; products / users deactivated where the API allows). Wait for the
   go-ahead.
2. Release by runbook 4 (and 5 if A2 added the index); `task-026.ps1` and `task-025b.ps1` live (A5 makes steps 23–24
   deterministic).
3. Live: `task-027.ps1 -Mode NoDrills -Live` once (SKU rules are off on production: B4's failure parts are reported
   as skipped) and `npm run e2e` once against the Vercel origin and the public API (no stack control). Results into
   `docs/acceptance.md` (E1).
4. Nothing else on production: no load scenario, no drill, no container stop, no `psql` write.

## Acceptance criteria
- [ ] A1 race answered 200, tested, mutant killed; A2 measured (index only if ≥ 2 ms); A3 focus ring visible and
      checked in the browser; A4 one `<h1>` per route, look unchanged; A5 tunnel-only leniency, strict elsewhere.
- [ ] B `task-027.ps1` covers all 15 criteria, `Full` green on `omni027`, `NoDrills` green on development and live;
      `SIM-DOWN-`; invariants extended, 0 violations.
- [ ] C Playwright suite (7 specs) green 5 × in a row on `omni027`, once on development, once live; axe in a real
      browser with contrast clean (serious / critical).
- [ ] D CI job `acceptance` green twice (or e2e-only with the reason).
- [ ] E `docs/acceptance.md` with evidence per criterion and known limitations; README, runbook.
- [ ] Release G with live checks; nothing outside G written to production.
- [ ] Report with "Development steps (applied)" and "Production steps (applied)".

## Out of scope
New features; changes of flows, contracts or data model; backups; Oracle VM (025c); a load test on production; real
channel / invoice / mail integrations; dropping indexes; visual redesign.

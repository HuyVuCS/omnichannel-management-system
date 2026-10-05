# Omnichannel Order & Inventory Management System

Architecture, data model and rules: `THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx` and `CLAUDE.md`.
Implementation decisions per step: `docs/implementation-log.md`. Task handoff between Cowork and Claude Code: `docs/tasks/README.md`. Plan to completion: `docs/roadmap.md`.

## Repository layout

```
backend/            .NET 10 solution (Omnichannel.slnx)
  src/Omnichannel.Api             ASP.NET Core Web API (controllers, auth, middleware)
  src/Omnichannel.Application     Use cases, contracts, validators
  src/Omnichannel.Domain          Entities, enums (no framework dependencies)
  src/Omnichannel.Infrastructure  EF Core/PostgreSQL, Identity, (later) RabbitMQ, S3
  src/Omnichannel.Workers         .NET Worker Service host (workers added from step 6)
  src/Omnichannel.Tests           xUnit tests
docker/             docker-compose (PostgreSQL, RabbitMQ, API), DB init scripts, Dockerfiles
frontend/           React + TypeScript dashboard (Vite, Redux Toolkit / RTK Query, Ant Design 6, i18next)
scripts/smoke/      Scripted manual checks per task (PowerShell)
```

## Prerequisites

- .NET SDK 10.0.1xx or later (`dotnet --version`)
- Docker Desktop
- Node.js 22.12 or later for the frontend (`frontend/.nvmrc` pins 24)

## First-time setup (PowerShell, from the repository root)

### 1. Infrastructure

```powershell
Copy-Item docker/.env.example docker/.env   # then edit every value in docker/.env
powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1   # keys of the object storage, for docker/.env, the API and the Workers
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
```

Three containers start: PostgreSQL, RabbitMQ and the object storage emulator for product images and export files (S3
API on `127.0.0.1:8333`, development only). Every port is bound to `127.0.0.1`.

The init script `docker/postgres/init/01-create-roles.sh` creates two roles on the **first** start of the volume:

| Role | Used by | Rights |
| --- | --- | --- |
| `omnichannel_migrator` | `dotnet ef database update` | Owns the `omnichannel` database (DDL) |
| `omnichannel_app` | API, Workers | SELECT/INSERT/UPDATE/DELETE only |

If you change the passwords later, the script does not re-run. Reset with
`docker compose -f docker/docker-compose.yml --env-file docker/.env down -v`
(**destructive: deletes all PostgreSQL and RabbitMQ data**).

### 2. Build and test

```powershell
cd backend
dotnet tool restore
dotnet build
dotnet test
```

Integration tests need Docker running (Testcontainers: PostgreSQL, RabbitMQ and - started by the first test that uses a
product image or an export file - the object storage emulator `chrislusf/seaweedfs:4.48`); `dotnet test --filter "Category!=Integration"` runs unit tests only.

### 3. API secrets (user-secrets, never appsettings)

```powershell
cd src/Omnichannel.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes, e.g. output of: openssl rand -base64 48>"
dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com"
dotnet user-secrets set "BootstrapAdmin:Password" "<min 12 chars, upper+lower+digit>"
cd ../..
```

`BootstrapAdmin` creates the first Admin on start-up if that email does not exist yet. Remove it afterwards.

Local connection settings name `127.0.0.1`, not `localhost` (here, for the Workers in section 6, for the migrator in
section 4, and `RabbitMq:HostName`): Windows tries `::1` first for `localhost`, the containers' ports are published on
`127.0.0.1` only, and the refused IPv6 attempt costs about 2 s for every new connection.

### 4. Database migrations (run with the migrator role)

The migrations already exist in `src/Omnichannel.Infrastructure/Persistence/Migrations`; apply them:

```powershell
$env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
```

Review the generated migration before applying it (check constraints, the generated `AvailableStock` column, unique indexes, seed rows for 3 channels and 3 roles).

Two ways to apply a new migration on development:

- **By hand:** the command above.
- **By Claude Code:** after a task is verified, Claude Code runs the same command itself as the migrator, under the
  guardrails of ["Development steps" in `docs/tasks/README.md`](docs/tasks/README.md#development-steps): the development
  database only, additive migrations only (`dotnet ef migrations list` must show exactly the task's migrations as
  pending; anything that drops, renames, narrows or rewrites is left to the user), and the connection is never printed.
  It reads the connection from the **user-level** environment variable `OMNICHANNEL_MIGRATOR_CONNECTION`, set once by
  the user (then restart the editor / Claude app so it is inherited):

  ```powershell
  [Environment]::SetEnvironmentVariable("OMNICHANNEL_MIGRATOR_CONNECTION", "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>", "User")
  ```

### 5. Run

```powershell
dotnet run --project src/Omnichannel.Api --launch-profile http
```

- Swagger UI (Development only): http://localhost:5080/swagger
- Health: http://localhost:5080/health
- Manual requests: `src/Omnichannel.Api/Omnichannel.Api.http`

Run the API in Docker instead: `docker compose -f docker/docker-compose.yml --env-file docker/.env --profile app up -d --build` (port 8080).

### 6. Workers (Outbox Publisher, Order Worker, Stock Sync Worker, Report Export Worker, Reconciliation)

The Workers host runs these background workers in one process:

| Worker | What it does |
| --- | --- |
| Outbox Publisher | publishes pending outbox rows to RabbitMQ (exchange `omnichannel.topic`) |
| Order Worker | consumes `order.processing`: stores the order and reserves the stock in one transaction |
| Stock Sync Worker | consumes `stock.sync.website`, `stock.sync.pos`, `stock.sync.marketplace`: pushes stock to the channels (section 7) |
| Report Export Worker | consumes `report.export`: builds the Excel file of an export and stores it in the object storage ("Reports and Excel export") |
| Export retention sweep | every hour: deletes the files of exports older than 7 days |
| Reconciliation | every 15 minutes (Development: every minute): compares the central stock with what the channels show and starts a stock sync where they differ (section 8) |

It uses the **runtime** database role and the RabbitMQ user from `docker/.env` (secrets via user-secrets, never appsettings),
and the keys of the object storage, which `scripts\setup-object-storage.ps1` (section 1) stores in its user-secrets:

```powershell
cd src/Omnichannel.Workers
dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
dotnet user-secrets set "RabbitMq:UserName" "<RABBITMQ_USER>"
dotnet user-secrets set "RabbitMq:Password" "<RABBITMQ_PASSWORD>"
cd ../..
dotnet run --project src/Omnichannel.Workers
```

- On start-up it declares the RabbitMQ topology (exchanges `omnichannel.topic` / `omnichannel.dlx`, quorum queues and their
  `.dlq` queues). If an existing queue has different arguments the host stops with a "topology mismatch" error; queues are
  never deleted automatically. If RabbitMQ is down, the host still starts and publishing resumes when the broker is back.
- Management UI: http://localhost:15672 (same RabbitMQ user). An order submitted through the API passes through queue
  `order.processing` and is consumed by the Order Worker at once; while the Workers host is stopped, messages wait in
  their queues and the tasks stay `Pending`.
- Optional settings: `OutboxPublisher:BatchSize` (100), `PollInterval` (1 s), `MaxAttempts` (10), `InitialBackoff` (1 s),
  `MaxBackoff` (5 min), `ConfirmTimeout` (10 s); `Worker:InstanceId` (default `{MachineName}-{ProcessId}`).
- Stock Sync Worker (queues `stock.sync.*`): see section 7.
- Report Export Worker (queue `report.export`) and the retention sweep: see "Reports and Excel export". The host needs
  the `ObjectStorage` settings to start (endpoint and bucket are in `appsettings.Development.json`, the keys in its
  user-secrets); a broker that already exists gets the new queue and its `.dlq` at the next start of the host.
- Order Worker (queue `order.processing`): `OrderWorker:Concurrency` (4), `OrderWorker:MaxAttempts` (5).
  Temporary failures are retried after 5 s / 30 s / 2 min / 10 min (delay queues `omnichannel.delay.*`,
  `RabbitMq:RetryDelays` to change the tiers). After the last attempt, or at once for a permanent error (bad data, check or
  foreign-key violation), the task becomes `Failed`, a `processing.failed` row is written to `SystemEventLogs`, and the message
  is moved to `order.processing.dlq` (inspect it in the management UI).
- Manual retry: a Manager/Admin calls `POST /api/processing/tasks/{id}/retry` on a `Failed` task. The original message is copied
  into a new outbox row (new message id, original routing key), the task becomes `Pending` with a fresh attempt budget
  (`OrderWorker:MaxAttempts` new attempts; attempt numbers keep counting), and `processing.retry_requested` is logged. The copy in
  the DLQ is left there for inspection. Look up the result with `GET /api/processing/tasks/{id}` or `GET /api/orders/{orderId}`.

### 7. Stock sync (Stock Sync Worker, simulated channels)

The Workers host pushes the **available stock** of a product to every channel that sells it. A channel sells a product
when the product is mapped on it (`/api/channels/{channelId}/products`, see "API conventions").

- **What triggers a sync**
  - every stock change (stock import, reservation by an order): the `inventory.changed` event reaches the three queues
    `stock.sync.website`, `stock.sync.pos` and `stock.sync.marketplace` (channel `digital-platform`); each channel with an
    Active mapping for the product gets its own `StockSync` task, the others just acknowledge the message;
  - a mapping that is created, re-activated, or whose external ids change while it is Active: the API writes the task and
    a `stock.sync.requested` message directed at the queue of that channel (the response carries `syncTaskId`);
  - a manual retry of a failed stock-sync task (`POST /api/processing/tasks/{id}/retry`): again a directed message, for
    that channel only.

  A channel created through the API has no stock-sync queue: it can have mappings, but it is not synced.
- **What is pushed**: the stock as it is when the worker runs, never the value in the message; after a push the stock is
  read again and pushed again while it differs (`StockSyncWorker:MaxPushesPerAttempt`, default 3). One consumer per
  channel queue (`StockSyncWorker:Concurrency`, default 1, at most 16), so one slow or failing channel never holds back
  the others.
- **Failures** follow the Order Worker: a temporary error (channel timeout, channel unavailable, rate limited, open
  circuit breaker, temporary database error) is retried after 5 s / 30 s / 2 min / 10 min up to
  `StockSyncWorker:MaxAttempts` (5); a rejection by the channel fails at once. A failed task is `Failed`, writes
  `processing.failed` and its message goes to `<queue>.dlq`. Look at the tasks with
  `GET /api/processing/tasks?taskType=StockSync&channelId=...`.
- **Calls to a channel** go through a timeout (`StockSyncWorker:ChannelCallTimeout`, 10 s) and a circuit breaker per
  channel (`StockSyncWorker:CircuitBreaker:FailureRatio` 0.5, `MinimumThroughput` 5, `SamplingDuration` 30 s,
  `BreakDuration` 30 s; only temporary failures and timeouts count). There is no retry inside a call: every retry is an
  attempt in the task's history.
- **The channels are simulated** (`SimulatedChannelStockClient`; no real channel accounts exist). A push is logged and
  accepted - and its value kept as the channel's stock, which reconciliation reads (section 8) - unless the
  `ChannelSimulation` section says otherwise. The section is read on every push, so a change of
  `src/Omnichannel.Workers/appsettings.Development.json` applies **without restarting** the Workers host (started with
  `dotnet run` or by the smoke script: the content root is the project folder; a host started from another folder reads
  the copy next to its binaries):

  ```json
  "ChannelSimulation": {
    "SkuRules": true,
    "Channels": {
      "website": { "Mode": "Succeed", "LatencyMs": 0 },
      "pos": { "Mode": "TransientError" },
      "digital-platform": { "Mode": "Succeed" }
    }
  }
  ```

  | `Mode` | Effect |
  | --- | --- |
  | `Succeed` (default) | the channel accepts the push |
  | `TransientError` | `channel_unavailable`: retried with delay, `Failed` + DLQ after the last attempt |
  | `PermanentError` | `channel_rejected`: `Failed` + DLQ at once |
  | `Timeout` | the channel never answers: `channel_timeout` after `ChannelCallTimeout`, then as `TransientError` |

  `LatencyMs` delays every answer of that channel. `SkuRules` (on in `appsettings.Development.json` only, off by default)
  adds failures by **external SKU** of the mapping, for a single product instead of a whole channel: an external SKU
  starting with `SIM-PERM-` is rejected (`channel_rejected`), one starting with `SIM-TEMP-` fails with
  `channel_unavailable` on attempt 1 and succeeds from attempt 2. These prefixes exist only in the simulator.
- **Demo of retry and DLQ**: with the Workers host running, set `ChannelSimulation:Channels:website:Mode` to
  `TransientError`, save the file, import stock for a product mapped on `website`, and watch the task's attempts
  (`GET /api/processing/tasks/{id}`) and the worker log (`Retry 1 of message ...: waits 00:00:05 in omnichannel.delay.5s`,
  then 30 s, ...). Set the mode back to `Succeed`: the next attempt succeeds.
- **Scripted check**: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-014.ps1` (needs the `StockSync`
  migration, section 4; uses a running API / Workers host or starts them, the Workers host in Development).
- Messages that were in the stock-sync queues before this worker existed are processed like any other: no mapping
  existed, so they are acknowledged without a task.
- A channel that still shows another value afterwards (a sync that failed for good, a lost update) is found by
  **reconciliation**: section 8.

### 8. Reconciliation (Reconciliation Worker, channel-side stock)

Stock sync pushes every change, but a channel can still end up showing something else: a sync that ran out of attempts
during a long outage, an update the channel lost, a value somebody changed on the channel's side. The Workers host
therefore **compares** the central available stock with what each channel shows, on a timer, and starts a stock sync
where they differ (architecture doc 8.5). There is no "run now": a run starts by the interval only.

- **What a run does.** For every *Active* mapping of every *Active* channel that has a stock-sync queue (section 7), in
  batches per channel: read the central `AvailableStock`, ask the channel for its values (one read per batch), and for
  every pair whose value **differs or is missing** create a new `StockSync` task with a directed
  `stock.sync.requested` message - the same path as every other stock sync (retries, DLQ, alerts, realtime hints). The
  task pushes the stock *as it is when the worker runs*, so a change between the comparison and the push costs nothing.
- **Failed syncs are repeated, also when nothing differs.** A pair whose most recently finished stock-sync task is
  `Failed` with a temporary code (anything but `channel_rejected`) gets one task **even when the channel already shows
  the central value** - the push landed before the failure was recorded, or the stock came back to the pushed value.
  Without it the pair's `StockSyncFailed` alert would stay open for good: only a task of the pair that succeeds ends
  it. These tasks are counted as `failedRetried` (in the run's log line, its event and on the Logs page: "N failed
  syncs repeated"), they are part of `tasksCreated`, and they count toward `MaxTasksPerRun` - within a batch after the
  pairs that really differ.
- **What a run never does.** It never changes the source of truth: it writes `ProcessingTasks`, `OutboxEvents` and at
  most one `SystemEventLogs` row - never stock, products, mappings, orders, movements or alerts. No business rule says
  a channel's value may correct the central one. An open `StockSyncFailed` alert of a pair still ends the usual way:
  by a task of the pair that succeeds (the reconciliation task is one).
- **Pairs a run skips:**
  - a pair with an unfinished stock-sync task (`Pending`, `InProgress`, `RetryScheduled`): that task pushes the current
    stock anyway;
  - a pair whose most recently finished stock-sync task is `Failed` with `channel_rejected` on its last attempt: a new
    task would fail the same way in every run. Its alert is open; changing the mapping (which starts a sync) or a manual
    retry is the fix. A final failure with a temporary code (`channel_circuit_open`, `channel_timeout`,
    `channel_unavailable`, `channel_rate_limited`, a database error) is **not** skipped - that is exactly the long
    outage reconciliation recovers from.
- **A channel that cannot be read** is left alone for the rest of the run; the other channels are reconciled. An
  answer of the channel (a `ChannelApiException`; reads use the same timeout and circuit breaker as pushes) is a
  Warning with the channel's error code. **Anything else a read throws** - a defect of the channel's adapter, a typing
  error in its configuration - ends that channel the same way with the code **`unexpected`** and is logged as an
  **Error with the exception**: one broken channel never stops the reconciliation of the others. Only two things end a
  whole run: the host stopping, and a failure of the run's own database work (the next round starts over).
- **One run at a time**, whatever the number of Workers instances: a PostgreSQL advisory lock
  (`pg_try_advisory_lock`) held on a connection of its own for the run; an instance that does not get it skips the round.
- **Settings** (`Reconciliation` section of the Workers host, checked at start-up):

  | Setting | Default | Meaning |
  | --- | --- | --- |
  | `Enabled` | `true` | `false` = this host never runs a reconciliation |
  | `Interval` | `00:15:00` (Development: `00:01:00`) | pause between two runs, 10 s to 1 day; the first run starts after `min(Interval, 60 s)` |
  | `BatchSize` | `100` | mappings per step = products per channel read, 1 to 100 |
  | `MaxTasksPerRun` | `500` | most tasks one run creates (for drift and for repeated failed syncs); what is beyond it is counted (`deferred`) and found again by the next run |

- **Finding a run again.** Every task, message and event of a run carries the correlation id
  **`reconciliation-{runId}`** (the run id without dashes). On *Sync & Processing* filter by that correlation id to see
  the tasks of a run; on *Logs / audit* the event `reconciliation.completed` (Warning) says what the run found - e.g.
  "12 of 340 products differ - 12 sync tasks created - not readable: POS (channel unavailable)" - and its *Trace* link
  is the same filter. The event is written **only when a run created a task or could not read a channel**; a run that
  finds everything in sync and no failed sync to repeat writes one log line (`Reconciliation run ...: N checked, ...`)
  and nothing else.
- **The simulated channels remember what they accepted.** A real channel holds its own stock values; the simulator
  keeps them in the table `SimulatedChannelStocks` (channel slug + the product as the channel knows it: `id:` +
  external product id, otherwise `sku:` + external SKU; no foreign key - it is the channel's data, not this system's).
  An **accepted** push writes the value (the last write wins), a push that fails, times out or is cancelled does not,
  and a read answers from the table (no row = the channel has no value). The `ChannelSimulation` modes of section 7
  apply to reads as well (`TransientError` -> `channel_unavailable`, `PermanentError` -> `channel_rejected`, `Timeout`
  -> `channel_timeout`); the SKU rules do not. Only the simulator knows the table: with a real channel adapter it
  stays empty.
- **The first run after the migration.** The table starts empty (no backfill): until a push reaches it, a simulated
  channel holds no value for a mapping, and that is drift. The first run therefore creates one stock-sync task per
  Active mapping (at most `MaxTasksPerRun`; pairs skipped as above excepted). After those tasks ran, the channels
  hold the central values and the runs are quiet.
- **Seeing drift in development.** `SkuRules` (on in `appsettings.Development.json`) knows one more external-SKU
  prefix: **`SIM-LOST-`** - the push is accepted and logged ("accepted, not stored"), but the channel keeps nothing.
  Map a product on a channel with an external SKU such as `SIM-LOST-1`: every run (every minute in Development)
  creates a stock-sync task for it, which succeeds and changes nothing - permanent drift. Change the external SKU
  (or deactivate the mapping) to end it.
- **Scripted check**: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-022.ps1` (needs the
  `SimulatedChannelStocks` migration, section 4; uses a running API / Workers host or starts them, the Workers host in
  Development; takes about six minutes: it waits for runs of the timer).

## Frontend

React dashboard in `frontend/` (Vietnamese by default, English switchable in the header).

```powershell
cd frontend
npm ci          # first time, and after package.json changes
npm run dev     # http://localhost:5173 - needs the API on http://localhost:5080 (section 5)
```

- `npm run dev` proxies `/api` to the API - WebSockets included (the notifications hub) -, so the browser only talks to
  its own origin (no CORS involved).
- Sign in with a user of the API (the bootstrap admin, or one created on the Users page).
- Other scripts: `npm run typecheck`, `npm run lint` (ESLint + Prettier check), `npm test` (Vitest, no API needed: requests
  are answered by MSW; **a test that ends with a request still unanswered fails**, see "Tests" below), `npm run test:slow`
  (the same suite with every answer delayed - for any task that adds tests with requests, see "Tests" below),
  `npm run build` (output in `frontend/dist`), `npm run preview`, `npm run format`,
  `npm run measure` (after a build: the first-load set - entry chunk and everything it imports statically - per file
  and in total, raw and gzip; `scripts/measure-bundle.mjs`).
- Structure: `src/app` (store, router, providers, layout), `src/shared` (API layer, components, i18n, helpers),
  `src/features/<feature>` (RTK Query endpoints, pages), `src/pages` (403, 404, route error).
- Conventions every page follows:
  - server data through RTK Query endpoints injected into `baseApi` (tags per resource); Redux slices only for client state
    (the session, the state of the realtime connection);
  - every text through i18n (`src/shared/i18n/vi.json` and `en.json` must have the same keys; a test checks this and also
    fails on hard-coded text in components). Only the language in use is downloaded: it is loaded before the first
    render, the other one on the first switch and before the language changes (`changeLanguage` of `shared/i18n`, never
    `i18n.changeLanguage` directly); a failed load keeps the language and shows a notification;
  - fixed widths from `src/shared/components/layout.ts` and truncation with a tooltip, so switching the language changes
    text only;
  - API errors: validation errors on the form fields (`applyProblemToForm`), `errorCode` translated under `errors.<code>`,
    network errors and 5xx as a notification with the correlation id;
  - list pages keep paging, sorting, search and filters in the query string (`useListQueryState`), so a reload or a
    shared link shows the same list; values in the address that are not valid are ignored, never sent; a text box bound
    to the address (`useAddressText`) follows it when it changes from outside (menu link, back/forward);
  - date filters keep local dates in the address (`from=YYYY-MM-DD`) and send the local start/end of day with the UTC
    offset;
  - form dialogs submit through `useFormSubmission`, render `<ResetFormOnOpen openCount={useOpenCount(...)} />` as the
    last child of their `Form` and give that `Form` `clearOnDestroy`: a dialog must never start with the values of the
    record it showed before, nor with what was typed and cancelled - neither when it is reopened before its close
    animation ends (the mounted form is reset, for another record and for the same one) nor after it was closed
    completely (the destroyed form is emptied; without that a production build shows the previous record - the
    development server hides it, because React's StrictMode mounts every field twice). The reset runs **inside** the
    form: the dialog library can render a closing dialog's content one commit after the component that owns it, and a
    reset in the owner's effect would then use the previous record's `initialValues`;
  - a segmented control (`VI | EN`, the dashboard's range presets) gets an explicit `name`: it is a group of radio
    inputs, and under test every generated group name is the same - two controls on one page would be one group, in
    which only one input can be checked;
  - a page is remounted whenever the path changes (the keyed `Suspense` in `AppLayout`; the query string never
    remounts it). For a detail page bound to a route parameter (order, task, channel) this means: while the next record
    loads, nothing of the previous one is on screen - neither its data nor page state such as the result of the last
    save. One test per detail page pins this;
  - no business rule is repeated in the client: flags such as `isLowStock` and normalisation such as the SKU come from
    the API; actions a role may not use are hidden, and the API stays the authority;
  - each page is its own chunk (`lazyPage` in `src/app/router.tsx`); the shell stays while a page loads. A chunk that
    cannot be loaded (a redeploy while the tab is open) or a page that throws ends on the localised route error page
    with "Reload"; inside the layout the menu stays usable;
  - confirmation dialogs are closed when the session ends (`SessionManager`); a confirmation that must follow a
    language switch is a `Modal` of its page, not `modal.confirm`;
  - products and customers are picked with `ProductSelect` / `CustomerSelect` (server-side search, no client-side list;
    shared behaviour in `useServerSearchSelect`). While a search is pending the picker offers no options, so Enter
    cannot choose an option of an older search (a background refetch of the same search keeps its options); a label
    that is cut shows its full text as the `title`;
  - a list whose table shows the API's default order passes `defaultSortDir` to `useListQueryState` (orders: newest
    first); that order is never written to the address.
- Pages so far: Dashboard (`/`), Order entry (`/order-entry`), Orders (`/orders`), Order detail (`/orders/{id}`, reached
  from the list and from an order reference in the movement history, no menu item), Inventory (`/inventory`), Stock import
  (`/inventory/import`, Manager/Admin, reached from the inventory and product lists, no menu item), Inventory movements
  (`/inventory/movements`), Products, Customers, Channels, Channel detail (`/channels/{id}`, reached from the channel
  name in the list, no menu item), Sync & Processing (`/processing`), Task detail (`/processing/tasks/{id}`, no menu
  item), Alerts (`/alerts`), Logs / audit (`/logs`, Manager/Admin), Reports & Excel export (`/reports`,
  Manager/Admin), Users. Every menu item has a page: the router's page table is typed by the menu's paths
  (`NavigationPath`), so a menu item without a page - or a page for a path that is no menu item - fails
  `npm run typecheck`. A future page is added together with its route; there is no placeholder page.
- **Realtime** (`src/features/realtime`): after sign-in the application opens one SignalR connection to
  `/api/hubs/notifications` (section "Realtime hints and dashboard summary"). The client library
  (`@microsoft/signalr`) is behind a small interface (`hub.ts`) and loaded with a dynamic `import()`, so the login page
  never downloads it; tests use a fake hub (`src/test/hub.ts`).
  - Hints carry ids only. Each hint is mapped to RTK Query cache tags (`hintTags.ts`) and the tags are invalidated at
    most once per second (a throttle): at once when nothing was invalidated in the last second, otherwise together at
    the end of that second. What is on screen is fetched again through the API, a cached list nobody shows is dropped.
    No page polls.
  - Event → tags (`tagsForEvent`; `all: true` = the same types without ids): `order.created` → order lists, summary;
    `inventory.changed` → stock, movements, product lists, **alert lists** (a low-stock alert shows the stock of now),
    summary; `processing.task.changed` → that task, task lists, its order, summary; `alert.changed` → that alert,
    alert lists, summary; `low_stock.created` → alert lists, summary (it arrives with an `alert.changed` in the same
    batch; mapped anyway). A new alert only refreshes what is on screen: there is no toast, sound or badge.
  - Every (re)connect and every `resync: true` invalidates the realtime tags (orders, stock, movements, products,
    tasks, alerts, summary) once - never the whole cache. Lists are still fetched again whenever a page is opened.
  - The connection is retried after 0, 2, 5, 10, 30 seconds, then every 30 seconds, for as long as the session lasts;
    it is closed on logout and when the token expires. A token the hub refuses (401) ends the session like a 401 of the
    API. The header shows *live* / *connecting* / *reconnecting* / *offline* (offline = only the 30-second retries are
    left; the data on screen may be outdated).
  - The client logs warnings and errors only, with `access_token` removed: the token is part of the WebSocket URL.
- Dashboard: the numbers of `GET /api/dashboard/summary` as the server computed them (no totals, thresholds or range
  limit in the client). The range is in the address: `?range=today|7d|30d` (local days; the default, the last 24 hours,
  writes and sends nothing) or `?from=...&to=...`. Non-zero numbers link to the list behind them. The card "Unresolved
  alerts" shows `summary.alerts` - open, acknowledged, and both per alert type (every type, zeros included) - as they
  are now, independent of the range (its tooltip says so); each number links to the alert list with that status and
  type.
- Alerts (`/alerts`, every role): the list of `GET /api/alerts` with links to what an alert is about (order, task,
  channel, stock import). Filters - status and type (several values each, `?status=Open&status=Resolved`), product,
  channel, a range of local days on the last occurrence - sort (the server's three fields) and page live in the
  address. **Without a status filter the request carries no status: the server's default (open and acknowledged)
  applies**, and the control says so; "Resolved" and all three are explicit choices. Details come from the response
  only: for low stock the product's available stock and threshold *as they are now*, for failures the error code
  (the API sends no message). Manager and Admin acknowledge an `Open` alert after a confirmation (it cannot be undone;
  the alert stays until the system resolves it) and get a shortcut to the stock import on unresolved stock alerts; a
  refused acknowledge (`alert_resolved`, `alert_not_found`) is said in the UI language and the list is fetched again.
  The list is fetched again when the page is opened, on "Refresh", by alert and stock hints, and after a product or
  channel change (their names and the threshold are shown). At 1280 px type, status, product, channel, details, last
  occurrence and the action column (fixed to the right edge) are visible without horizontal scrolling. Scripted check:
  `scripts\smoke\task-018.ps1`.
- Logs / audit (`/logs`, Manager and Admin; Staff has no menu item and gets the no-access page): the list of
  `GET /api/system-events` ("System events"), newest first. A row says when, how severe (a tag with icon and text),
  what (the event type with a translated label; a type this client does not know under its raw name) and, in one line
  built from the returned `details` with a translated template, what happened - e.g. the document, lines and units of
  an import, or task type, attempt and error code of a failure (its sanitised message is in the tooltip). "Who" is the
  actor's e-mail, for user management "actor → target". Links lead to the order, to the task and to the **trace**:
  the same list with the event's correlation id as its only filter (window, sort direction and page size kept) - one
  click shows every event of a flow, from the request to its processing. Filters - level and event type (several values
  each), correlation id, order id, task id (exact texts) - the sort direction and the page live in the address. An id
  that is no GUID is said under its field and nothing is asked with it. The server decides who reads what: an Admin is
  offered the `user.*` types as a filter, a Manager is not, and a `user.*` type a Manager puts into the address is
  never sent (the API would answer an empty page).
  - Time window: the presets of the dashboard (last 24 hours - the default -, today, 7 days, 30 days) or a range of
    local days. This list has no default range on the server, so "the last 24 hours" is a fixed start instant kept in
    the address (`?since=2026-10-02T01:05:09Z`, written by the page when the address names no window, and again when
    the preset is chosen or the filters are cleared): a reload and a shared link ask for the same window. The window has
    no end, so events that arrive later belong to it. The other presets are `?range=today|7d|30d`, a custom range
    `?from=…&to=…`; the 92-day limit is the server's (said on the range control).
  - No realtime: there is no hint for events. The list is fetched again whenever the page is opened and on "Refresh".
- Tests (`src/test/setup.ts`, `pendingRequests.ts`): **a test must end with its requests answered.** Every `fetch` is
  counted from the call until it settles, and a test that ends with one still open fails, naming method and URL. End
  a test on what the last answer puts on screen (`findBy…`, `waitFor`), not right after the click that sends a
  request - a request in flight is answered while the next test runs. A test whose subject is a request that is never
  answered calls `allowUnansweredRequests('<reason>')`; that is the only opt-out.
  - **`npm run test:slow`** runs the same suite with every answer 120 ms late (`TEST_ANSWER_DELAY_MS`, read by
    `pendingRequests.ts`; unset = no delay; `npm run test:slow -- <file>` for one file). On a quiet machine answers
    are instant, so a test that depends on that - ends with a request in flight, counts requests before an earlier
    refetch was answered - is red only now and then, under load; with the delay it is red in every run. **Run it for
    every task that adds tests with requests**; it must be green. The delay is real time: a fake clock does not skip it.
  - A test that counts requests starts counting on an idle cache: `await settled(store)` (`test/interactions.ts`)
    waits until no request is on its way and no query or mutation is pending. An invalidation waits for pending
    requests and then refetches (the resync after every hub connect does), so "the page is rendered" is not "nothing
    more will be asked".
  - **Pattern to avoid: a role-and-name query over a large table inside `waitFor`** (or a `findByRole` /
    `findAllByRole` with `name`, which is the same loop). Every poll computes the accessible name of every candidate
    element in the document; on a table of twenty rows one poll can take hundreds of milliseconds, under load or in the
    slow run longer than the interval - the answer the test waits for is then not rendered before the wait gives up.
    Wait on something narrow instead: a text (`findByText`), a query `within` the one row or dialog (`rowOf`,
    `findDialog`), or a plain DOM query on a stable attribute (`document.querySelectorAll('img[src="…"]')`), and make
    the role-and-name assertions once, after the wait.
  - **Conditions of a slow run:** the Browser pane closed and the machine otherwise idle (no API or Workers host, no
    backend build or test, no dev or preview server). A test that is red in such a run is **not** attributed to load
    without a second run of that file alone (`npm run test:slow -- <file>`); both results go into the task's report.
- Product images (`features/products`): the list has a thumbnail column (40 x 40, cropped, lazy, `alt` = the product
  name; a placeholder when there is no image or it cannot be loaded) for every role. The edit dialog (Manager/Admin)
  has the image block: preview, "choose image", "remove image" (with a confirmation). Both act at once, not on "Save".
  An upload is presign -> `PUT` to the storage -> confirm under one busy state; type and size are checked in the
  client first only to save a request. The `PUT` (`imageUpload.ts`) is a plain `fetch` to another origin, **not** the
  API's base query: no `Authorization`, no correlation id, only the headers the presign answer names (a test asserts
  it). API errors use the usual mapping (a 400 on `objectKey` is shown in the block, a 503 `storage_unavailable`
  globally with the reference code); a refused upload shows one message with the storage's status. The create dialog
  only says that the image is added after saving (the upload needs the product's id).
  The API signs a new link for every response and lists are refetched on every stock hint, so the link in use is kept
  per product and image version (`imageLinks.ts`, applied in `transformResponse`) until 60 s before it expires - a
  refetch does not change `src`, a new image or a link about to expire does. Tests answer uploads on a fixed storage
  origin (`src/test/storage.ts`).
  A page left open for longer than a link lives (15 minutes): a thumbnail whose image fails to load **after its
  `expiresAt`** asks for fresh links - the list page fetches its list again once, however many rows fail in the same
  burst; the preview of the edit dialog asks for its one product. A failure before `expiresAt` (the storage is down,
  the object is broken) asks for nothing, and after asking a thumbnail does not ask again until an image has loaded:
  a link that keeps failing can never become a chain of requests (`ProductThumbnail`, `linkHasExpired`).
- Reports & Excel export (`features/reports`, `/reports`, Manager and Admin; Staff have no menu item and get the
  no-access page): a request card with one form per report and the table "My exports". Nothing is decided in the
  client - roles, limits and the retention time are the API's ("Reports and Excel export" below).
  - **Request:** *Orders* has the filters of the orders list (channel, status, customer, search) and a required range
    of local days (default: the last 30 days up to today; sent as the instants of the first day's start and the last
    day's end). *Inventory* has search and "low stock only". The range is checked against `MAX_EXPORT_RANGE_DAYS` (366)
    before a request is sent - a copy of `Reports:MaxRangeDays` that only saves a request: **a deployment that changes
    the server's limit changes this constant too.** The file's times are written in the browser's time zone
    (`Intl.DateTimeFormat().resolvedOptions().timeZone`, `UTC` when the browser does not say) and its texts in the UI
    language (`Accept-Language`); both are shown on the form, neither is chosen there.
  - **Answers:** 202 -> a message, the table is fetched again, the form keeps its values. A validation error goes to
    its field (`from` / `to` to the range); `export_too_large`, `too_many_exports` and a validation error of something
    that is no field (`timeZone`) are shown above the form, the two codes in the client's own words (`errors.*`).
  - **"Export to Excel" on the Orders and Inventory pages** (Manager and Admin) opens the Reports page with the
    list's filters in the form. They travel in the router's **location state**, not in the address (the address of the
    Reports page belongs to its table); every value is checked again, and nothing is requested before the user
    submits.
  - **My exports:** page, page size, sort direction and the report filter live in the address. A row shows when the
    export was asked for, the report, its filters as one line (the range as local days, the channel and the customer
    by name - one request per customer, however many rows name it), status (`Expired` grey), rows, size and "kept
    until". `Succeeded` -> *Download*; `Failed` -> the label of the error code (unknown codes as they are) and a
    button to the task detail, where the attempts and the retry are; nothing for unfinished and expired exports.
  - **Live status:** the cache tag `ReportExport`. A `processing.task.changed` hint with `taskType: ReportExport`
    makes that export and the export lists stale (tasks of other types do not), and the tag is part of the resync
    after every hub (re)connect. While the connection is **not** live and an export on the page is not finished, the
    list is asked for every 5 s (not while the tab is hidden); otherwise nothing polls.
  - **Download:** `GET …/download` is a *mutation* (never cached); the link is followed at once through a temporary
    `<a rel="noreferrer">` (the storage serves the file as an attachment, so the browser saves it and the page stays)
    and the answer is removed from the store (`request.reset()`). The link is a credential for five minutes: it is in
    no address, store, message or log. 409 `export_expired` / `export_not_ready` and 404 -> a message and the list as
    it is now.
  - `ReportExport` is a task type like the others on the Sync & Processing page and the dashboard; the Logs page
    names `report.export_requested` (report, requester) and `report_export.message_rejected` (reason code).
  - A reconciliation run (section 8) is `reconciliation.completed` on the Logs page: "N of M products differ", the
    sync tasks it created, "N failed syncs repeated" when it repeated any (`failedRetried` > 0), what was left for the
    next run, and the channels it could not read by name with the label
    of the error code (`CHANNEL_ERROR_CODES` / `logs.channelErrors.*`; a code without a label as it is).
  - Scripted check: `scripts\smoke\task-021.ps1`.
- Channels: the list says for every channel whether stock is pushed to it at all (`stockSyncEnabled`, "API
  conventions"). The channel detail shows the channel, a link to its stock-sync tasks, and the products mapped on it
  (search, sort by SKU / created, paging - all in the address). Manager and Admin map a product and change a mapping
  (external ids, status) in a dialog; there is no delete, a mapping is deactivated. After a save the page shows what
  the API answered: with a `syncTaskId` "stock sync started" and the live status of that task with a link to it,
  without one "no stock sync was started" (the possible reasons are the API's and are listed, not guessed). Nothing
  about stock sync is decided in the client. Scripted check: `scripts\smoke\task-016b.ps1`.
- Sync & Processing: the task list (filters task type, status, channel, creation range, correlation id, error code;
  sort by created / updated) and the task detail with its message state and attempts. Manager and Admin can retry a
  `Failed` task there and on the order detail (after a confirmation). The publisher's `lastError` is never shown.
- Order entry: three panels, one per seeded channel slug (`digital-platform`, `website`, `pos`), each with its own form
  and outcome. The external order id is the order's idempotency key: it is generated when the form opens
  (`DP-` / `WEB-` / `POS-` + local date + six characters), can be edited, and stays as it is after a failed request, so a
  resend is the same order. After the API accepted an order (202) the panel follows its processing task: by push while
  the realtime connection is live (a hint for the task fetches it again, nothing is asked in between), otherwise by
  polling `GET /api/processing/tasks/{id}` every 1.5 s (not while the tab is hidden - `app/tabVisibility.ts`). Either
  way it waits at most 60 s, then offers "Check again" (one request, another 60 s); a finished task loads its order
  once. The Workers host must be running for an order to get past "waiting" (section 6). There is no price or total in
  the form: amounts come from the order.
- Stock, orders and tasks change in the background (Workers): besides the realtime hints, the inventory, movement,
  order and task lists and the detail pages are fetched again whenever the page is opened and on "Refresh", and the
  order-entry page invalidates the lists once when it sees a task finished while the realtime connection is not live
  (while it is live, the hints of the same transaction have already done that).
- The access token is kept in `sessionStorage` (cleared when the tab closes); the language choice in `localStorage`.

## API conventions

- Every endpoint requires a JWT (`Authorization: Bearer ...`) unless marked `[AllowAnonymous]`.
- Roles: `Admin`, `Manager`, `Staff`; policies `AdminOnly`, `ManagerOrAdmin`, `AnyStaff`.
- Errors are RFC 7807 `application/problem+json` with `errorCode` and `correlationId`.
- Validation errors (400, `errorCode = validation_failed`): every key of `errors` is the camelCase JSON path of the field,
  for body fields (`items[0].productId`), query-string parameters (`to`, `sortBy`) and model-binding errors (`pageSize`)
  alike. Only the body paths reported for malformed JSON start with `$` (`$.items[0].stock`).
- Send `X-Correlation-Id` to correlate a request across logs; the API echoes it (or generates one).
- Enums are serialised as strings.
- Channel responses (`GET /api/channels`, `GET /api/channels/{id}`, create, update) carry `stockSyncEnabled`: true when
  stock is pushed to the channel, i.e. its slug has a stock-sync queue (section 7). It is the capability, not the
  status: an Inactive `website` is still `true`; a channel created through the API is `false`.

### Channel product mappings and stock-sync tasks

- `GET /api/channels/{channelId}/products` (any role): paged list (`page`, `pageSize`, `search` over SKU, product name,
  external product id and external SKU; `sortBy` = `sku` (default) or `createdAt`).
- `POST /api/channels/{channelId}/products` (Manager/Admin): `{productId, externalProductId?, externalSku?}` - at least one
  of the two external ids, at most 128 characters each. 201 with the mapping and `syncTaskId`.
- **How a channel knows a product, and what is unique.** A mapping is known to its channel by its external product id
  when it has one, otherwise by its external SKU - the key of stock sync, of reconciliation and of webhooks (section
  9). Per channel, among all mappings (Inactive ones included): a product is mapped once, an external product id
  belongs to one mapping, and **an external SKU belongs to one mapping among those without an external product id**
  (partial unique index `IX_ChannelProducts_ChannelId_ExternalSku`). The same SKU may repeat next to mappings that
  have an external product id (it is only a label there) and on other channels; it is compared exactly as stored
  (trimmed, case-sensitive).
- `PUT /api/channels/{channelId}/products/{channelProductId}` (Manager/Admin): `{externalProductId?, externalSku?, status}`
  (`Active` / `Inactive`). Product and channel of a mapping cannot change; there is no delete (deactivate instead).
- `syncTaskId` is the stock-sync task the request started (section 7), or null: channel or mapping not Active, channel
  without stock sync, or nothing changed that the channel has to learn. Deactivating sends nothing to the channel.
- Errors: 404 `not_found` (channel), 404 `channel_product_not_found` (mapping, also when it belongs to another channel),
  400 on `productId` (product does not exist) or `externalProductId` (neither external id given),
  409 `channel_product_exists` (product already mapped on this channel), 409 `external_product_id_taken` (the external
  product id belongs to another product on this channel), 409 `external_sku_taken` (create or update would leave two
  mappings of the channel with this external SKU and no external product id). The dialog shows each on its field.
- `GET /api/processing/tasks` accepts `channelId`; task responses carry `channelId`, `productId` and `productSku` (set
  for `StockSync` tasks, null otherwise; the SKU comes from a left join in the same query). Attempt error codes of stock sync: `channel_timeout`, `channel_unavailable`, `channel_rate_limited`,
  `channel_circuit_open` (temporary), `channel_rejected` (permanent).
- A business conflict that the API answers with 409 (the three above, `product_sku_exists`, `stock_import_already_applied`,
  ...) is logged once at Information (`Request failed with 409: <errorCode>`). EF Core's own entries for such a unique
  violation are written at Debug; every other database failure is logged at Error as before
  (`ExpectedUniqueViolations`, `DatabaseFailureLogInterceptor`).

### Realtime hints and dashboard summary

- **Hub:** `/api/hubs/notifications` (SignalR, any role). Server to client only: a client receives the message `hints`
  and has nothing to call. Hints say *what kind of thing changed and which ids* - never data. The client refetches
  through the API (with its own authorization); PostgreSQL stays the source of truth.

  ```json
  { "events": [
      { "type": "order.created", "orderId": "...", "channelId": "...", "status": "Reserved" },
      { "type": "inventory.changed", "productIds": ["..."] },
      { "type": "processing.task.changed", "taskId": "...", "taskType": "StockSync", "status": "Failed",
        "channelId": "...", "productId": "..." } ],
    "resync": false }
  ```

  | `type` | When | Members |
  | --- | --- | --- |
  | `order.created` | an order was stored (Order Worker) | `orderId`, `channelId`, `status` |
  | `inventory.changed` | stock changed (import, reservation) | `productIds`, or `all: true` when more than 100 products changed |
  | `processing.task.changed` | a task was created or its status changed | `taskId`, `taskType`, `status`, `orderId`, `channelId`, `productId` (those that apply) |
  | `alert.changed` | an alert was opened, refreshed, acknowledged or resolved | `alertId`, `alertType`, `status`, `productId`, `channelId` (those that apply) |
  | `low_stock.created` | a low-stock alert was opened (in addition to its `alert.changed`) | `alertId`, `productId` |

  A failed task or a finished stock sync is a `processing.task.changed` with that `taskType` / `status`. Members without
  a value are left out. `all: true` on an event means "more changed than is listed: refetch everything of this type".
- **`resync: true`** means hints may have been missed (the API lost and re-established its database listener, or a burst
  was too large to list): refetch everything that is on screen. A client must do the same after its own reconnect.
- **How a hint gets there:** every `SaveChanges` that stores an order, a stock movement or a task change (API and Workers
  alike, `RealtimeHintInterceptor`) sends one `pg_notify('omnichannel_ui', ...)` on its connection: inside a transaction
  PostgreSQL delivers it at the commit and drops it on a rollback, otherwise it is sent right after the save was
  committed. Alert hints are sent by the alert writer itself, always inside the transaction of the change that causes the
  alert, as a notification of their own. Each API instance holds one `LISTEN` connection (`PostgresHintListener`; no extra database right needed) and
  pushes to its own clients, so several instances need no backplane. Hints that arrive within `Realtime:BatchWindow`
  (default `00:00:00.250`, 10 ms - 5 s) go out as one message.
- **Authentication:** the same access token as for the API. Browsers cannot set headers on WebSocket requests, so the
  hub - and only the hub - also accepts it as the query parameter `access_token`; every other path reads the
  `Authorization` header only. The security stamp is checked at connect, and a connection is closed when its token
  expires. The token value is replaced by `***` in every log event (`AccessTokenRedactionEnricher`), whatever log level
  is configured for the framework categories.
- To watch the hints in the API log, set `Serilog:MinimumLevel:Override:Omnichannel.Api.Hubs` to `Debug`
  (`Hints received: ...`, `Hint batch sent to the clients: ...`).
- **`GET /api/dashboard/summary?from&to`** (any role): the numbers of the overview page in one request. `from` / `to` are
  ISO 8601 with offset, both optional (default: the last 24 hours up to now), `from` < `to`, at most 92 days.
  - `orders` (created in the range): `total`, `byStatus` (every status), `byChannel` (every channel: `total`, `reserved`,
    `insufficientStock`), `salesAmount` (Reserved + Completed);
  - `inventory` (now): `products`, `lowStock` (the rule of the inventory list's low-stock filter), `outOfStock`;
  - `processing` per task type: `pending`, `inProgress`, `retryScheduled` (now), `failed`, `succeeded` (status reached
    in the range);
  - `stockSync` per channel with a stock-sync queue: `retryScheduled` (now), `failed` (in the range), `lastSucceededAt`;
  - `outbox` (now): `pending`, `failed`, `oldestPendingAt`;
  - `alerts` (now, unresolved only): `open`, `acknowledged`, `byType` (every alert type: `type`, `open`,
    `acknowledged`).
- Scripted check: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-015.ps1`; the frontend side (product
  SKU on tasks, build, tests): `scripts\smoke\task-016.ps1`. A smoke script starts an API or a Workers host only for
  the local default; with `SMOKE_BASE_URL` set they must already be running.

### Alerts

An alert is something that needs attention now. Alerts live in their own table (`Alerts`, migration `Alerts`) with a
lifecycle `Open` → `Acknowledged` → `Resolved`; `SystemEventLog` stays the event history.

| `type` | One unresolved alert per (`DedupKey`) | Opened / refreshed by | Resolved by |
| --- | --- | --- | --- |
| `LowStock` | product (`low_stock:{productId}`) | a stock or threshold change that leaves the product low (`AvailableStock <= LowStockThreshold`, the rule of the inventory list) | a stock or threshold change that leaves it not low |
| `InsufficientStock` | product (`insufficient_stock:{productId}`) | an order stored as `InsufficientStock`, for each product that was short | the next stock import of the product |
| `StockSyncFailed` | channel and product (`stock_sync_failed:{channelId}:{productId}`) | a stock-sync task that failed for good | any stock-sync task of the same channel and product that succeeds, or the mapping of the pair being switched off (`Active` → `Inactive`) |
| `ProcessingFailed` | task (`processing_failed:{taskId}`) | a task of another type that failed for good | that task succeeding (after a manual retry) |

- **Refresh:** a condition that occurs again while its alert is unresolved updates that row (`lastOccurredAt`,
  `occurrenceCount` + 1, latest `taskId` / `orderId` / `errorCode`) and keeps its status - an acknowledged alert stays
  acknowledged. Low stock is a state, not a counter: its alert is left unchanged while the product stays low. A condition
  that returns after its alert was resolved opens a new alert; resolved alerts are kept.
- **Resolving is always the system's decision**, taken from the source tables; there is no manual resolve. A new product
  has no stock, so it starts with a `LowStock` alert.
- **Deactivated mapping:** changing a channel product mapping from `Active` to `Inactive` resolves the `StockSyncFailed`
  alert of its channel and product in the same transaction (a pair that is not synced any more needs no attention); a
  refused change (409) resolves nothing. No other mapping change touches alerts, and deactivating a *channel* does not
  either (re-activating a channel does not re-sync its mappings, so its alerts keep their meaning). Re-activating the
  mapping starts its initial sync again, and a failure opens a new alert. Known edge: an attempt that is already running
  when the mapping is switched off can still fail for good and open the alert again; a manual retry of that task then
  completes without a push and resolves it.
- **Acknowledge** (`Manager` / `Admin`): "seen", nothing more - the alert stays until the condition is gone. It is audited
  in `SystemEventLog` (`alert.acknowledged`).
- **Consistency:** every alert row is written by one SQL statement inside the transaction of the change that causes it
  (`AlertStore`: `INSERT … ON CONFLICT` on the partial unique index of unresolved keys, conditional
  `UPDATE … RETURNING`), so an alert never exists without its cause and never survives a rollback. A code path that
  changes stock or a threshold must call `AlertStore.ReconcileLowStockAsync` for its products before it commits (today:
  stock import, reservation, product create and update; a future release / cancel path as well).
- **Hints** (sent by the alert writer in the same transaction, see "Realtime hints"): `alert.changed` (`alertId`,
  `alertType`, `status`, `productId`, `channelId`) for every alert that was opened, refreshed, acknowledged or resolved,
  and `low_stock.created` (`alertId`, `productId`) in addition when a low-stock alert was opened.
- `GET /api/alerts` (any role): paged (`page`, `pageSize`; `sortBy` = `lastOccurredAt` (default), `firstOccurredAt`,
  `occurrenceCount`, each descending unless `sortDir=asc`). Filters: `status` and `type` (both repeatable), `productId`,
  `channelId`, `from` / `to` on `lastOccurredAt` (inclusive, ISO 8601 with offset, at most 92 days). **Without `status`
  the list shows `Open` and `Acknowledged`**; resolved alerts must be asked for (`status=Resolved`).
  Item: `alertId`, `type`, `status`, `productId`, `productSku`, `productName`, `channelId`, `channelName`, `taskId`,
  `orderId`, `errorCode`, `firstOccurredAt`, `lastOccurredAt`, `occurrenceCount`, `acknowledgedAt`,
  `acknowledgedByEmail`, `resolvedAt`, and for `LowStock` the product's `availableStock` and `lowStockThreshold` as
  they are **now** (null for the other types).
- `GET /api/alerts/{alertId}` (any role): the same item; 404 `alert_not_found`.
- `POST /api/alerts/{alertId}/acknowledge` (Manager/Admin): 200 with the item - also when it was acknowledged already
  (nothing is written then); 409 `alert_resolved`; 404 `alert_not_found`.
- Scripted checks (need the Workers host in Development): `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-017.ps1`
  (lifecycle, acknowledge, summary) and `scripts\smoke\task-018.ps1` (the default list, a deactivated mapping, then the
  frontend build and tests).

### System events (Logs/Audit)

`SystemEventLog` is the business and audit history (architecture doc §5.2): one row per event, written in the
transaction of what it records, with a free-form JSON payload. `GET /api/system-events` reads it for the Logs/Audit
page; nothing in the API writes, changes or deletes an event.

- **Roles:** `Manager` and `Admin`; Staff gets 403. Events whose type starts with `user.` (user management) are the
  Admin's: for a Manager they do not exist - not in the list, not in `totalCount`, and asking for one by `eventType`
  answers an empty page (200), not an error. The rule is applied by the server from the caller's role.
- `GET /api/system-events`: paged (`page`, `pageSize`). The only sort field is `createdAt`, newest first unless
  `sortDir=asc`; events of the same instant are ordered by `logId` in the same direction. Filters, all optional:
  `level` and `eventType` (both repeatable; an event type is matched exactly), `correlationId` (exact), `orderId`,
  `taskId`, `from` / `to` on `createdAt` (inclusive, ISO 8601 with offset, at most 92 days when both are given - the
  messages of the other lists). No text search, no filter on the payload, no detail endpoint.
  Item: `logId`, `level`, `eventType`, `createdAt`, `correlationId`, `taskId`, `orderId`, `workerName`, `details`,
  `actorEmail`, `targetEmail`.
- **`details` is an allowlist, never the stored payload** (`SystemEventDetails` in `Omnichannel.Application`): only
  the top-level fields listed for the event type are returned, with their values as stored; every other field is
  dropped, and an event type without an entry has `details: null`.

  | Event type | Level | Returned fields |
  | --- | --- | --- |
  | `inventory.imported` | Information | `referenceId`, `itemCount`, `totalQuantity` |
  | `order.insufficient_stock` | Warning | `orderId`, `taskId`, `items` (`productId`, `requested`, `available` per line) |
  | `order.already_exists` | Warning | `taskId`, `orderId`, `externalOrderId` |
  | `order.message_rejected` | Error | `messageId`, `reasonCode` |
  | `stock_sync.message_rejected` | Error | `messageId`, `reasonCode`, `queue` |
  | `report_export.message_rejected` | Error | `messageId`, `reasonCode` |
  | `report.export_requested` | Information | `reportType`, `requestedBy` (the task is the event's `taskId`; never the filters of the export) |
  | `reconciliation.completed` | Warning | `runId`, `checked`, `inSync`, `drifted`, `tasksCreated`, `deferred`, `failedRetried` (of the tasks created, the ones for pairs in sync whose last sync had failed), `skippedUnfinished`, `skippedRejected`, `unreadable` (a list of `{ channel, errorCode, pairs }`: the channel's name, why it could not be read - a channel error code or `unexpected` -, how many of its pairs were left) - section 8 |
  | `processing.failed` | Error | `taskId`, `taskType`, `attemptNo`, `errorCode`, `errorMessage` (the attempt's sanitised message), `queue` |
  | `processing.retry_requested` | Information | `taskId`, `taskType`, `requestedBy`, `previousMessageId`, `newMessageId`, `attemptBudgetStart` |
  | `outbox.publish_failed` | Error | `outboxEventId`, `eventType`, `publishAttempts` |
  | `alert.acknowledged` | Information | `alertId`, `type`, `acknowledgedBy` |
  | `user.created` | Information | `targetUserId`, `actorUserId`, `role` |
  | `user.role_changed` | Information | `targetUserId`, `actorUserId`, `from`, `to` |
  | `user.locked`, `user.unlocked`, `user.password_reset` | Information | `targetUserId`, `actorUserId` |

  Never returned: `lastError` of `outbox.publish_failed` (broker or exception text) and `reason` of the three
  `…message_rejected` events (it can quote the JSON parser's exception and values of the rejected message). Both stay
  in the database and in the Workers log for whoever operates the system.
- **`reasonCode` of a rejected message** (`MessageRejectionCodes` in `Omnichannel.Application`, stored next to the
  text `reason`): one code per way a worker rejects a message. The Logs page shows its label (`logs.rejection.*`),
  an unknown code as it is, and "—" for a row written before the code existed.

  | `reasonCode` | The message … | Worker |
  | --- | --- | --- |
  | `invalid_json` | is not JSON, or not the JSON of an envelope | all |
  | `empty_body` | is the JSON value `null` | all |
  | `unexpected_event_type` | has an event type the queue does not handle | all |
  | `missing_task_id` | names no task (`order.created`, `stock.sync.requested`, `report.export.requested`) | all |
  | `missing_message_ids` | has no `messageId` or no `correlationId` | all |
  | `message_ids_too_long` | has a `messageId` or `correlationId` of more than 100 characters | stock sync |
  | `payload_not_object` | has a payload that is not a JSON object | all |
  | `invalid_payload` | has a payload that cannot be read as the payload of its event type | all |
  | `empty_payload` | has an empty payload (kept as a guard; no message reaches it) | all |
  | `task_id_mismatch` | names one task in the envelope and another in the payload | all |
  | `missing_order_fields` | is an order without channel, customer or external order id | order |
  | `no_items` | is an order without items | order |
  | `invalid_item` | has an item without product, with a quantity ≤ 0 or a negative price | order |
  | `duplicate_product` | has the same product on more than one item | order |
  | `missing_product_id` | is an inventory change without a product | stock sync |
  | `missing_sync_target` | is a stock-sync request without channel or product | stock sync |
  | `unknown_task` | names a task that does not exist | all |
  | `task_mismatch` | names a task that is not a stock-sync task for its product and channel, or not an export task with its export row | stock sync, export |
  | `task_of_other_channel` | names a task of another channel than the queue it arrived on | stock sync |

  "all" = Order Worker, Stock Sync Worker and Report Export Worker (task 020 added no code of its own). A new
  rejection path gets its own constant (listed in `MessageRejectionCodes.All`), a case in
  `MessageRejectionTests` and a label in `vi.json` / `en.json` + `REJECTION_CODES` (`features/logs/eventSummary.ts`).
- **`actorEmail` / `targetEmail`:** the e-mail address of the user named by `actorUserId`, `requestedBy` or
  `acknowledgedBy`, and by `targetUserId`. A value that is no user id (`system`, `system:order-worker`) or names a
  user that does not exist is null. Read from the returned `details` only.
- **Cost:** one `COUNT`, one page query and - only when the page names users - one user lookup for all of them
  (`WHERE Id IN (…)`), whatever the page size. The list uses the existing indexes (`CreatedAt`, `CorrelationId`,
  `OrderId`, `TaskId`, `(EventType, CreatedAt)`); the `COUNT` of a wide range reads that range (accepted, task 026).
- **Trace** (architecture doc §5.3): everything a request caused carries its correlation id - the import or the order
  intake, the task, each failure, a manual retry. `GET /api/system-events?correlationId=…&sortDir=asc` is the flow in
  order; `GET /api/processing/tasks?correlationId=…` its tasks.
- **Adding an event type:**
  1. a constant in the `…Events` class of its feature in `Omnichannel.Application` (`InventoryEvents`,
     `OrderProcessingEvents`, `ProcessingEvents`, `StockSyncEvents`, `ReconciliationEvents`, `ReportEvents`, `OutboxEvents`, `AlertEvents`,
     `UserEvents`; a new
     class must be public, static and named `…Events`), used by the code that writes the event;
  2. an entry in `SystemEventDetails.AllowedFields` with the fields that are safe to show: the application's own ids,
     codes, counts and names - never exception or broker text, never a fragment of a message the application did not
     write. A unit test fails for a constant without an entry;
  3. an actor or a target is resolved only from the fields `actorUserId` / `requestedBy` / `acknowledgedBy` and
     `targetUserId`: name them that way;
  4. a type that only an Admin may read starts with `user.`;
  5. in the frontend: the type in `EVENT_TYPES` (`features/logs/systemEventsApi.ts`), its label and its summary line
     in `vi.json` / `en.json` (`logs.eventTypes`, `logs.summary`) and its case in `eventSummary.ts`. Until then the
     page lists the event under its raw name without a summary.
- Scripted check (needs the Workers host in Development): `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-018b.ps1`
  (roles, an import, a failed stock sync found again by its correlation id, an acknowledged alert, a locked user, then
  the frontend build and tests).

### Product images

One image per product, kept in **S3-compatible object storage** - never in PostgreSQL, never passing through the API.
The database holds the object key and what was checked (`Products.ImageKey`, `ImageContentType`, `ImageSizeBytes`,
`ImageUpdatedAt`: all null or all set). The bucket is **private**; browsers upload and read with **presigned links**.
No AWS account is involved: `AWSSDK.S3` is only the client library, the provider is configuration, and development
uses a local emulator (SeaweedFS, service `objectstorage` of `docker/docker-compose.yml`).

- **Setup (once per developer machine):**
  1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` - generates the key pair, adds
     `OBJECT_STORAGE_BUCKET`, `OBJECT_STORAGE_ACCESS_KEY_ID` and `OBJECT_STORAGE_SECRET_ACCESS_KEY` to `docker/.env`
     (only entries that are missing) and stores the same pair in the user-secrets of `Omnichannel.Api` and of
     `Omnichannel.Workers` (`ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`). It prints no key and can be
     run again (after task 020: run it once more, so the Workers host gets the keys).
  2. `docker compose -f docker/docker-compose.yml --env-file docker/.env up -d` - starts the emulator next to
     PostgreSQL and RabbitMQ (S3 API on `127.0.0.1:8333`, data in the volume `objectstorage-data`). Bucket, key and
     CORS are start-up settings of the container: there is no init step, and a restart changes nothing.
  3. Migration `ProductImage` (section 4).
- **Upload in three steps** (Manager/Admin, like product create/update):
  1. `POST /api/products/{id}/image/presign` with `{ contentType, sizeBytes }` - `contentType` is `image/jpeg`,
     `image/png` or `image/webp` (no SVG: it can carry script, and the storage serves what is stored; no GIF),
     `1 <= sizeBytes <= ObjectStorage:MaxImageBytes` (5 MiB). Answer: `{ objectKey, uploadUrl, method: "PUT",
     headers: { "Content-Type": ... }, expiresAt }`. The key is generated by the server:
     `products/{productId}/{uuidv7}`. Nothing is written to the database; 404 for an unknown product.
  2. The client sends the file to `uploadUrl` with exactly `method` and `headers` - **to the storage, without the
     API's `Authorization` header**. The link is valid for 5 minutes and signs the `Content-Type`; the storage
     rejects another type, a changed signature and an expired link (403). The link stays valid for its **whole**
     lifetime, also after the confirmation of step 3: a second `PUT` with it replaces the confirmed object with
     content nobody checked (the stored size is then stale). Only a Manager/Admin holds such a link; task 026 closes
     this by copying the object to a final key at confirmation.
  3. `PUT /api/products/{id}/image` with `{ objectKey }` **confirms**. A presigned upload cannot limit size or
     content, so the API checks the stored object, in this order and outside any transaction: the key has the exact
     generated form and the prefix of **this** product; the object exists; it is not larger than the limit; its
     content type is one of the three; its first bytes are the signature of that type (JPEG `FF D8 FF`, PNG
     `89 50 4E 47 0D 0A 1A 0A`, WebP `RIFF....WEBP`). Each failure is a 400 on `objectKey` (localised), and an object
     that fails the size, type or content check is **deleted**. Then one transaction locks the product row and stores
     the image; after the commit the previous image's object is deleted (best effort: a failure is a warning and
     leaves an orphan). Answer: the product. Confirming the current image again is a 200 that changes nothing.
     Concurrent confirmations for one product run one after the other: the last one is the image, the others'
     objects are deleted.
- **`DELETE /api/products/{id}/image`** (Manager/Admin) clears the image in a transaction and deletes the object after
  the commit; 204 also when the product has no image.
- **Reading:** every product response (list, get, create, update, confirm) carries `image`: `{ url, expiresAt,
  contentType, sizeBytes, updatedAt }` or `null`, for every role that may read products. `url` is a presigned `GET`
  valid for 15 minutes, **computed locally for every response** - listing products never calls the storage, and
  works while the storage is down. Two responses carry two different links for the same image; `updatedAt` is what
  identifies the image (the frontend keeps the link it has for the same `updatedAt` until 60 s before it expires).
- **Failures of the storage** (unreachable, timeout, refused) end a confirmation as **503** `storage_unavailable`; the
  product is unchanged and the upload can be confirmed later. The problem document, the log and the exception carry
  no endpoint, no key id and no link: the log has one warning with the operation (`head`, `read`, `delete`) and the
  HTTP status / S3 error code or the exception type. Presigned links are credentials until they expire and are never
  logged.
- **Orphans:** an upload that is never confirmed, and an object whose delete failed, stay in the bucket. They are
  never referenced and never served (no link is ever signed for them); there is no sweep yet (task 026).
- **Configuration** (`ObjectStorage` section; `ObjectStorageOptions`, validated at start-up for presence and ranges -
  the API never connects to the storage at start-up):

  | Setting | Default | |
  | --- | --- | --- |
  | `ServiceUrl` | - (`http://127.0.0.1:8333` in Development) | Endpoint of the S3 API. Links are built on it: it must be the address the users' browsers reach too |
  | `Region` | `us-east-1` | Region the requests are signed for |
  | `ForcePathStyle` | `true` | `{endpoint}/{bucket}/{key}`; `false` for providers that need `{bucket}.{endpoint}` |
  | `BucketName` | - (`omnichannel-dev` in Development) | Must exist; the application never creates it |
  | `AccessKeyId`, `SecretAccessKey` | - | Environment (`ObjectStorage__AccessKeyId`, ...) or user-secrets, never `appsettings*.json` |
  | `UploadUrlLifetime` | `00:05:00` | 1 s to 7 days |
  | `DownloadUrlLifetime` | `00:15:00` | 1 s to 7 days |
  | `MaxImageBytes` | `5242880` | 1 to 100 MiB. The frontend's early size check is the constant `MAX_IMAGE_BYTES` (`frontend/src/features/products/imageUpload.ts`): **raising** this setting needs that constant raised too, or the client refuses files the server would take. A lower setting needs nothing: the presign answers 400 with the configured limit |
  | `RequestTimeout` | `00:00:10` | Per request to the storage; a failed request is tried once more |

- **CORS of the bucket** (not of the API): the browser sends the upload from the frontend's origin to another origin
  with a `Content-Type`, so the storage must answer the preflight for `PUT` with that header from that origin. The
  emulator allows the dev server (port 5173) and `vite preview` (port 4173), each as `http://localhost:…` and as
  `http://127.0.0.1:…`, through `-s3.allowedOrigins` in the compose file - an origin is compared as text, so the two
  names of the local machine are two origins. A container that already runs keeps its old list: recreate it once
  (`up -d --force-recreate objectstorage`; the bucket and its objects stay). `<img>` needs no CORS. A CSP, when one is
  added (task 026), must allow the storage origin in `img-src` and `connect-src`.
- **Switching the provider** (task 025; any S3-compatible service): set `ServiceUrl`, `Region`, `BucketName`, the key
  pair and - if the provider wants virtual-hosted addressing - `ForcePathStyle=false`; create the bucket **private**;
  set its CORS for the frontend origin (`PUT`, `GET`; header `content-type` - some services compare header names
  case-sensitively and browsers send them lower-case); give the key read, write, delete **and list** on that bucket
  only. List is needed although the application never lists: several S3-compatible services answer the `HEAD` of a
  missing object with **403** instead of 404 when the key may not list the bucket, and the confirmation would then
  report "not uploaded" as a 503 `storage_unavailable`. The provider check of task 025 must show a 404 for a `HEAD` of
  a key that does not exist. The
  application only uses presigned `PUT`/`GET`, `HeadObject`, ranged `GetObject`, `DeleteObject` and - for export files -
  `PutObject`. The SDK's default
  request checksums are left on (the emulator accepts them); a provider that rejects them needs
  `RequestChecksumCalculation` / `ResponseChecksumValidation` = `WHEN_REQUIRED` on the client
  (`S3ObjectStorage`) - found with Garage in the spike of task 019.
- **Both hosts use the storage** (`AddObjectStorage`, the same `ObjectStorage` section and validation): the API for
  images and download links, the Workers host for export files ("Reports and Excel export"). The interface has one
  more operation since task 020, `PutAsync` (a seekable stream with its `Content-Type` and `Content-Disposition`),
  used only by the Report Export Worker; the provider key therefore needs `PutObject` as well.
- **`docker compose --profile app`:** the containerised API reaches the storage at `http://objectstorage:8333`, and
  the links it hands out carry that address, which a browser on the host cannot resolve. Images work with the API
  run from the IDE / `dotnet run`; the address for a deployed API is decided in task 025.
- **Emulator notes:** SeaweedFS 4.48 (`chrislusf/seaweedfs`, Apache-2.0), single process (`weed mini`), only the S3
  port is published and only on `127.0.0.1`. To start over with an empty bucket:
  `docker compose -f docker/docker-compose.yml --env-file docker/.env rm -sfv objectstorage` and remove the volume
  `omnichannel_objectstorage-data`. After changing the keys in `docker/.env`, recreate the container
  (`up -d --force-recreate objectstorage`) and run the setup script again.
- **Scripted check:** `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-019.ps1` (needs the emulator up
  and migration `ProductImage`).

### Reports and Excel export

Orders and inventory can be exported as Excel files (`.xlsx`). **Every export is built in the background** - there is no
synchronous path, also for a small result: the API only stores the request and answers; the Report Export Worker
(Workers host) builds the file with ClosedXML and stores it in the object storage; the requester downloads it with a
short-lived link. So an export is traceable, retried, dead-lettered, manually retryable, alerted and pushed to the UI
like every other asynchronous task.

```
POST /api/reports/…/export ─► one transaction: ProcessingTask(ReportExport) + ReportExports row
        202 { taskId }                         + OutboxEvent(report.export.requested) + SystemEventLog(report.export_requested)
Outbox Publisher ─► omnichannel.topic ─► queue report.export ─► Report Export Worker
        attempt (committed first) ─► read the rows in ONE read-only snapshot, building the workbook
        ─► upload exports/{taskId}.xlsx (after that transaction) ─► one transaction: result + attempt + task Succeeded ─► ACK
GET /api/reports/exports/{taskId}/download ─► { url (presigned GET, 5 min), expiresAt, fileName }
```

- **Setup** (once, after pulling task 020): run `scripts\setup-object-storage.ps1` again - it now stores the storage
  keys in the user-secrets of the Workers host too -, apply migration `ReportExports` (section 4), restart the Workers
  host. Without the keys the Workers host does not start (`ObjectStorage:AccessKeyId is required`).
- **Roles:** `Manager` and `Admin` may export; Staff gets 403. **An export belongs to the user who asked for it**: list,
  detail and download answer for the requester only. For anybody else - an administrator included - the export does
  not exist (404 `export_not_found`, never 403). Exports hold customer names and phone numbers and can be produced
  again at any time, so nobody needs another user's file.
- **`POST /api/reports/orders/export`** - body: the filters of `GET /api/orders` with the same meaning (`channel` slug,
  `status`, `customerId`, `search`; all optional), `from` and `to` (**required**, on the order's creation time,
  inclusive, at most `Reports:MaxRangeDays` apart) and `timeZone` (**required**, an IANA name such as
  `Asia/Ho_Chi_Minh`). Rows: the orders and their item lines.
  **`POST /api/reports/inventory/export`** - body: `search`, `lowStockOnly` (the filters of `GET /api/inventory`) and
  `timeZone`. Rows: the products.
  Both use the filter code of the lists (`OrderFilters`, `InventoryFilters`): a file contains what the page shows for
  the same filters. Answer **202** `{ taskId }` with `Location: /api/reports/exports/{taskId}`.
- **What a request checks, in this order:**
  1. the fields: 400 `validation_failed` on the field, in the language of `Accept-Language` (range missing, `to`
     before `from`, range too long, unknown status, unknown or missing time zone);
  2. the requester's unfinished exports (`Pending`, `InProgress`, `RetryScheduled`): `Reports:MaxUnfinishedPerUser` or
     more → **409 `too_many_exports`**. This is check-then-insert without a lock: two requests of one user at the same
     moment can both pass (one export above the limit, accepted);
  3. a `COUNT` with the same filters: more than `Reports:MaxRows` rows → **400 `export_too_large`**, nothing is
     written. The `detail` of both answers is localised (vi/en).
- **The message carries only `{ taskId }`**: the worker reads the parameters from `ReportExports` (PostgreSQL is the
  source of truth; a retry copy can never carry stale parameters). The stored `Parameters` (jsonb) are the normalised
  request - slug lower-case, blank filters as null, the range in UTC - plus `timeZone` and `language` (`vi` or `en`,
  the language of the request: the texts of the file are written in it).
- **Reading** (the requester only): `GET /api/reports/exports` (paged; `sortBy` only `createdAt`, newest first unless
  `sortDir=asc`; filter `reportType`), `GET /api/reports/exports/{taskId}`. Item: `taskId`, `reportType`, `status`,
  `parameters`, `fileName`, `rowCount`, `sizeBytes`, `createdAt`, `completedAt`, `expiresAt`, `errorCode`.
  `status` is the status of the task (`Pending`, `InProgress`, `RetryScheduled`, `Succeeded`, `Failed`) or
  **`Expired`**: the file was deleted, or the export is finished (`Succeeded` / `Failed`) and its `expiresAt` has
  passed - **from `expiresAt` on, whether or not the sweep has run yet** (the retention time is a promise about
  personal data, and the worker applies the same rule). An export that is still waiting or running past its
  `expiresAt` keeps the status of its task: the worker fails it as `export_expired`. `errorCode` is the code of the
  last attempt of a `Failed` export, otherwise null (also for `Expired`) - never an error text. `rowCount` = data rows
  of the file (orders + item lines, or products).
- **Download:** `GET /api/reports/exports/{taskId}/download` → `{ url, expiresAt, fileName }`: a presigned `GET` valid
  for `Reports:DownloadLinkLifetime`, computed locally (no call to the storage), to be opened by the browser without
  the API's token. 409 `export_expired` once the file was deleted or `expiresAt` has passed (checked first, for
  every export), 409 `export_not_ready` unless the export `Succeeded`. A link handed out shortly before `expiresAt`
  stays valid for its own lifetime. The file is stored with `Content-Type` `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`
  and `Content-Disposition: attachment; filename="<fileName>"`; the storage serves both from the stored object (checked
  with the emulator), so nothing is added to the link. The link is a credential: it is returned and never logged.
  A download is not audited; the request is (`report.export_requested` with `reportType` and `requestedBy`).
- **The file** (`orders_20261005-1030.xlsx`: the report and the local time of the request in its time zone):

  | Sheet | Columns |
  | --- | --- |
  | *Orders* (one row per order, by creation time) | created at, channel, external order id, customer name, customer phone, status (label), lines, total amount, order id |
  | *Items* (one row per item line, by order then SKU) | order created at, external order id, channel, SKU, product name, quantity, unit price, line total (a value), order id |
  | *Inventory* (one row per product, by SKU) | SKU, name, price, total, reserved, available, low-stock threshold, low stock (yes/no, the rule of the inventory list), expiry date, stock updated at |
  | *Info* (both reports) | report, the filters as entered (channel and customer by name), time zone, generated at, requested by (e-mail), row counts |

  No customer e-mail and no address. Header row bold, frozen, with an auto-filter; column widths fixed (never measured
  over the data). Numbers are numbers (money `#,##0`), dates are dates, and a point in time is an Excel date-time
  **converted to the requested time zone** (daylight saving time included) with the zone in the column header - Excel
  has no time zone. **Every text is written as a value, never as a formula**: a customer called `=1+1` or `@SUM(A1)` is
  the text `=1+1`. Sheet names, headers and labels are in the language of the request (`ReportTexts.resx` /
  `ReportTexts.vi.resx`).
- **Reading consistency:** the worker reads in one `REPEATABLE READ, READ ONLY` transaction, streamed and without
  change tracking: orders and item lines come from the same snapshot (an order committed in between is in neither
  sheet). Nothing outside the database is called in that transaction.
- **Failures** follow the other workers: the object storage not answering (`storage_unavailable`) and temporary
  database errors are retried after 5 s / 30 s / 2 min / 10 min up to `ReportExportWorker:MaxAttempts` (5); then the
  task is `Failed`, `processing.failed` is written, a `ProcessingFailed` alert is opened and the message goes to
  `report.export.dlq`. Not retried: more rows than `Reports:MaxRows` at the worker (`export_too_large` - the data grew
  after the request), an export whose retention time is over before it was built (`export_expired`), parameters the
  host cannot read (`export_parameters_invalid`, e.g. a time zone the Workers host does not know). The key of the
  file depends on the task alone, so a redelivery after the upload and before the commit stores the same object again -
  no orphan. A host that stops while it builds leaves the message for redelivery.
- **Reading a failed export on the Sync & Processing page:** filter the task type `ReportExport` ("Excel export"),
  open the task (the Reports page links a failed export to it): its attempts show worker, instance, error code and the sanitised message; the
  alert of the failure is on the Alerts page (`ProcessingFailed`). "Retry" there
  (`POST /api/processing/tasks/{id}/retry`) republishes the request with a fresh attempt budget; when it succeeds the
  alert resolves. The same task id is the export's id, so `GET /api/reports/exports/{taskId}` shows `Failed` with its
  `errorCode` to the requester. **A retry of an export past its retention time is refused**: 409 `export_expired`
  (localised `detail`) when the file was deleted or `expiresAt` has passed - nothing is written (no message, no
  audit event). Such an export could only fail again, and nothing would ever end the alert of that failure.
- **Retention:** the file is kept `Reports:Retention` (7 days) from the request (`expiresAt`). The Workers host sweeps
  every `Reports:CleanupInterval` (first round at most 30 s after start-up): for each export past `expiresAt` whose
  task has ended it deletes `exports/{taskId}.xlsx` (outside any transaction; a missing object is success - this also
  removes the file of a failed export whose upload happened) and then, **in one transaction**, marks the row
  (`DeletedAt`) and resolves the `ProcessingFailed` alert of the same task (with its `alert.changed` hint): an
  expired export leaves nothing to act on, so the alert of a failed export ends at most one sweep interval after its
  retention time. A delete that fails is a warning with the task id and is tried again in the next round; an export
  whose task is still waiting is left to the worker (which fails it as `export_expired`; a later round then takes
  it, alert included). Several instances may sweep at the same time. The rows themselves are kept.
- **Configuration** (`Reports` section, read by the API and the Workers host, validated at start-up):

  | Setting | Default | Range | |
  | --- | --- | --- | --- |
  | `MaxRows` | `100000` | 1 - 1,000,000 | Rows of one export (orders + item lines, or products), checked at the request and by the worker |
  | `MaxUnfinishedPerUser` | `3` | 1 - 100 | Waiting or running exports per user |
  | `MaxRangeDays` | `366` | 1 - 3660 | Longest range of an orders export. The Reports page checks the range early against the constant `MAX_EXPORT_RANGE_DAYS` (`frontend/src/features/reports/exportRequest.ts`): **raising** this setting needs that constant raised too; a lower setting needs nothing (the API answers 400 on the range) |
  | `DownloadLinkLifetime` | `00:05:00` | 1 s - 7 days | Validity of a download link |
  | `Retention` | `7.00:00:00` | 1 min - 366 days | How long a file is kept |
  | `CleanupInterval` | `01:00:00` | 1 s - 1 day | Pause between two sweeps |
  | `CleanupBatchSize` | `100` | 1 - 1000 | Exports per batch of a sweep |

  Workers host only: `ReportExportWorker:Concurrency` (1; 1 - 8) and `ReportExportWorker:MaxAttempts` (5; 1 - 20). The
  workbook is built **in memory**, so the memory of the Workers process grows with `MaxRows` and with `Concurrency`.
  Measured at the default limit (task 020, one export at a time, Workers host idle at 95 MiB): 100,000 rows of an
  orders export (25,000 orders + 75,000 lines) took 6 s, a 4.1 MiB file and a peak working set of 255 MiB; 100,000
  products took 5.5 s, a 5.1 MiB file and 336 MiB. Raising `MaxRows` or `Concurrency` multiplies that. More throughput
  comes from more Workers instances, not from a higher concurrency. For large files over a slow link the Workers host
  may need a larger `ObjectStorage:RequestTimeout` than the default 10 s (one request uploads the whole file; at
  most 2 minutes).
- **Time zones:** `timeZone` is valid when the host finds it (`TimeZoneInfo.TryFindSystemTimeZoneById`). IANA names
  work on Windows and in the Linux runtime image of `docker/api.Dockerfile` (checked for task 020; the image carries
  the time zone data and needs no font for writing a workbook). A host also accepts the names of its own platform
  (Windows names such as `SE Asia Standard Time`); clients should send IANA names. The API host validates the name
  and the Workers host converts with it, so both hosts must know the same zones - an export whose zone the Workers
  host does not know fails with `export_parameters_invalid`.
- **Package:** `ClosedXML` 0.105.1 (MIT), pinned in `Directory.Packages.props`, referenced by
  `Omnichannel.Infrastructure` only. It brings `ClosedXML.Parser` 2.0.0, `DocumentFormat.OpenXml` and
  `DocumentFormat.OpenXml.Framework` 3.1.1, `ExcelNumberFormat` 1.1.0, `RBush.Signed` 4.0.0 and `System.IO.Packaging`
  8.0.1 (all MIT) and `SixLabors.Fonts` 1.0.0 (Apache-2.0). `SixLabors.Fonts` changed its licence with version 2
  (Six Labors Split License), so it is **pinned centrally at 1.0.0** (`CentralPackageTransitivePinningEnabled` in
  `Directory.Packages.props`): a package that needs 2.0 or later fails the restore with `NU1109` instead of raising
  it silently. Do not raise the pin without a licence decision. Transitive pinning holds for every version in that
  file: a package that only comes in through another one never resolves above the version listed there.
- **Reports page:** the UI for all of this is `/reports` (Manager and Admin) - request forms, "My exports" with live
  status, download; "Export to Excel" on the Orders and Inventory pages opens it with the list's filters. Details:
  "Frontend", *Reports & Excel export*. The client always sends the browser's IANA time zone and the UI language.
- **Scripted check** (needs the Workers host, the emulator and migration `ReportExports`):
  `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-020.ps1` (the API) and `scripts\smoke\task-021.ps1`
  (what the Reports page asks for, then the frontend build and tests). Manual requests: the last section of
  `src/Omnichannel.Api/Omnichannel.Api.http`.

### Users and token revocation

- `/api/users` (Admin only): list/search, create (`{email, password, role}`), `PUT {id}/role`, `POST {id}/lock`, `POST {id}/unlock`,
  `POST {id}/reset-password` (`{newPassword}`, a temporary password set by the admin). Every user has exactly one role.
- Guards (409): `last_admin` (the last unlocked Admin cannot be demoted or locked), `cannot_modify_self` (an admin cannot change
  their own role or lock themselves).
- Access tokens carry the user's security stamp (`sstamp`). A role change, lock or password reset issues a new stamp, so tokens
  issued before are rejected with 401: at once on the API instance that made the change, and within
  `TokenRevocation:CacheDuration` (default 30 s, max 5 min, `00:00:00` = no cache) on other instances. A temporary lockout after
  failed logins only blocks new logins; it does not end an active session. Tokens issued before this feature (no `sstamp`) are
  not accepted: log in again.
- Validation messages are returned in the language of `Accept-Language` (`vi` or `en`, default `en`). Error keys and
  `errorCode` values are the same in both languages. FluentValidation localises its built-in messages; every message
  this application writes itself (custom rules, service-level checks, the Identity password policy) comes from
  `Omnichannel.Application/Resources/ValidationMessages.resx` (English) and `ValidationMessages.vi.resx`, read through
  `ValidationMessages.Get(...)`. A new custom message needs a key in both files; a unit test fails otherwise, and also
  when a validator carries a literal message.
- Messages name a field the way the UI labels it, in the request language (`'Ngưỡng cảnh báo tồn kho' phải ...`), never
  with the technical name. The names are in `Resources/FieldNames.resx` / `FieldNames.vi.resx`, keyed by the JSON member
  name (`lowStockThreshold`; one key per name, shared by every request that has such a field). A request field with a
  validation rule needs a name in both files; a unit test fails otherwise.
- Every operation writes a `user.*` row to `SystemEventLogs` with `targetUserId` and `actorUserId`; logs contain user ids only.

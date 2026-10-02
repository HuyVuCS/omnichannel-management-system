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
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
```

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

Integration tests need Docker running (Testcontainers PostgreSQL); `dotnet test --filter "Category!=Integration"` runs unit tests only.

### 3. API secrets (user-secrets, never appsettings)

```powershell
cd src/Omnichannel.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes, e.g. output of: openssl rand -base64 48>"
dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com"
dotnet user-secrets set "BootstrapAdmin:Password" "<min 12 chars, upper+lower+digit>"
cd ../..
```

`BootstrapAdmin` creates the first Admin on start-up if that email does not exist yet. Remove it afterwards.

### 4. Database migrations (run with the migrator role)

The migrations already exist in `src/Omnichannel.Infrastructure/Persistence/Migrations`; apply them:

```powershell
$env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
```

Review the generated migration before applying it (check constraints, the generated `AvailableStock` column, unique indexes, seed rows for 3 channels and 3 roles).

### 5. Run

```powershell
dotnet run --project src/Omnichannel.Api --launch-profile http
```

- Swagger UI (Development only): http://localhost:5080/swagger
- Health: http://localhost:5080/health
- Manual requests: `src/Omnichannel.Api/Omnichannel.Api.http`

Run the API in Docker instead: `docker compose -f docker/docker-compose.yml --env-file docker/.env --profile app up -d --build` (port 8080).

### 6. Workers (Outbox Publisher, Order Worker, Stock Sync Worker)

The Workers host runs three background workers in one process:

| Worker | What it does |
| --- | --- |
| Outbox Publisher | publishes pending outbox rows to RabbitMQ (exchange `omnichannel.topic`) |
| Order Worker | consumes `order.processing`: stores the order and reserves the stock in one transaction |
| Stock Sync Worker | consumes `stock.sync.website`, `stock.sync.pos`, `stock.sync.marketplace`: pushes stock to the channels (section 7) |

It uses the **runtime** database role and the RabbitMQ user from `docker/.env` (secrets via user-secrets, never appsettings):

```powershell
cd src/Omnichannel.Workers
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
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
  accepted unless the `ChannelSimulation` section says otherwise. The section is read on every push, so a change of
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

## Frontend

React dashboard in `frontend/` (Vietnamese by default, English switchable in the header).

```powershell
cd frontend
npm ci          # first time, and after package.json changes
npm run dev     # http://localhost:5173 - needs the API on http://localhost:5080 (section 5)
```

- `npm run dev` proxies `/api` to the API, so the browser only talks to its own origin (no CORS involved).
- Sign in with a user of the API (the bootstrap admin, or one created on the Users page).
- Other scripts: `npm run typecheck`, `npm run lint` (ESLint + Prettier check), `npm test` (Vitest, no API needed: requests
  are answered by MSW), `npm run build` (output in `frontend/dist`), `npm run preview`, `npm run format`.
- Structure: `src/app` (store, router, providers, layout), `src/shared` (API layer, components, i18n, helpers),
  `src/features/<feature>` (RTK Query endpoints, pages), `src/pages` (dashboard placeholder, 403, 404).
- Conventions every page follows:
  - server data through RTK Query endpoints injected into `baseApi` (tags per resource); Redux slices only for client state
    (the session);
  - every text through i18n (`src/shared/i18n/vi.json` and `en.json` must have the same keys; a test checks this and also
    fails on hard-coded text in components);
  - fixed widths from `src/shared/components/layout.ts` and truncation with a tooltip, so switching the language changes
    text only;
  - API errors: validation errors on the form fields (`applyProblemToForm`), `errorCode` translated under `errors.<code>`,
    network errors and 5xx as a notification with the correlation id;
  - list pages keep paging, sorting, search and filters in the query string (`useListQueryState`), so a reload or a
    shared link shows the same list; values in the address that are not valid are ignored, never sent; a text box bound
    to the address (`useAddressText`) follows it when it changes from outside (menu link, back/forward);
  - date filters keep local dates in the address (`from=YYYY-MM-DD`) and send the local start/end of day with the UTC
    offset;
  - form dialogs submit through `useFormSubmission` and reset through `useResetFormOnOpen` (a dialog reopened before its
    close animation ends must not show the previous record);
  - no business rule is repeated in the client: flags such as `isLowStock` and normalisation such as the SKU come from
    the API; actions a role may not use are hidden, and the API stays the authority;
  - each page is its own chunk (`lazyPage` in `src/app/router.tsx`); the shell stays while a page loads. A chunk that
    cannot be loaded (a redeploy while the tab is open) or a page that throws ends on the localised route error page
    with "Reload"; inside the layout the menu stays usable;
  - confirmation dialogs are closed when the session ends (`SessionManager`); a confirmation that must follow a
    language switch is a `Modal` of its page, not `modal.confirm`;
  - products and customers are picked with `ProductSelect` / `CustomerSelect` (server-side search, no client-side list;
    shared behaviour in `useServerSearchSelect`). While a search is pending the picker offers no options, so Enter
    cannot choose an option of an older search; a label that is cut shows its full text as the `title`;
  - a list whose table shows the API's default order passes `defaultSortDir` to `useListQueryState` (orders: newest
    first); that order is never written to the address.
- Pages so far: Order entry (`/order-entry`), Orders (`/orders`), Order detail (`/orders/{id}`, reached from the list
  and from an order reference in the movement history, no menu item), Inventory (`/inventory`), Stock import
  (`/inventory/import`, Manager/Admin, reached from the inventory and product lists, no menu item), Inventory movements
  (`/inventory/movements`), Products, Customers, Channels, Users. The other menu items show a "coming in task NNN"
  placeholder.
- Order entry: three panels, one per seeded channel slug (`digital-platform`, `website`, `pos`), each with its own form
  and outcome. The external order id is the order's idempotency key: it is generated when the form opens
  (`DP-` / `WEB-` / `POS-` + local date + six characters), can be edited, and stays as it is after a failed request, so a
  resend is the same order. After the API accepted an order (202) the panel follows its processing task
  (`GET /api/processing/tasks/{id}` every 1.5 s, at most 60 s, not while the tab is hidden - `app/tabVisibility.ts`),
  then loads the order once; "Check again" starts another 60 s. The Workers host must be running for an order to get
  past "waiting" (section 6). There is no price or total in the form: amounts come from the order.
- Stock and orders change in the background (Order Worker): the inventory, movement and order lists and the order detail
  are fetched again whenever the page is opened and on "Refresh", and the order-entry page invalidates them once when a
  task is finished. Realtime updates come with SignalR in a later task.
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

### Channel product mappings and stock-sync tasks

- `GET /api/channels/{channelId}/products` (any role): paged list (`page`, `pageSize`, `search` over SKU, product name,
  external product id and external SKU; `sortBy` = `sku` (default) or `createdAt`).
- `POST /api/channels/{channelId}/products` (Manager/Admin): `{productId, externalProductId?, externalSku?}` - at least one
  of the two external ids, at most 128 characters each. 201 with the mapping and `syncTaskId`.
- `PUT /api/channels/{channelId}/products/{channelProductId}` (Manager/Admin): `{externalProductId?, externalSku?, status}`
  (`Active` / `Inactive`). Product and channel of a mapping cannot change; there is no delete (deactivate instead).
- `syncTaskId` is the stock-sync task the request started (section 7), or null: channel or mapping not Active, channel
  without stock sync, or nothing changed that the channel has to learn. Deactivating sends nothing to the channel.
- Errors: 404 `not_found` (channel), 404 `channel_product_not_found` (mapping, also when it belongs to another channel),
  400 on `productId` (product does not exist) or `externalProductId` (neither external id given),
  409 `channel_product_exists` (product already mapped on this channel), 409 `external_product_id_taken` (the external
  product id belongs to another product on this channel).
- `GET /api/processing/tasks` accepts `channelId`; task responses carry `channelId` and `productId` (set for `StockSync`
  tasks). Attempt error codes of stock sync: `channel_timeout`, `channel_unavailable`, `channel_rate_limited`,
  `channel_circuit_open` (temporary), `channel_rejected` (permanent).
- A business conflict that the API answers with 409 (the two above, `product_sku_exists`, `stock_import_already_applied`,
  ...) is logged once at Information (`Request failed with 409: <errorCode>`). EF Core's own entries for such a unique
  violation are written at Debug; every other database failure is logged at Error as before
  (`ExpectedUniqueViolations`, `DatabaseFailureLogInterceptor`).

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

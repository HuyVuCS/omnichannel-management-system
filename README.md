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

### 6. Workers (Outbox Publisher)

The Workers host publishes pending outbox rows to RabbitMQ. It uses the **runtime** database role and the RabbitMQ user from
`docker/.env` (secrets via user-secrets, never appsettings):

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
- Management UI: http://localhost:15672 (same RabbitMQ user). After submitting an order through the API, the message appears
  in queue `order.processing` (no consumer yet).
- Optional settings: `OutboxPublisher:BatchSize` (100), `PollInterval` (1 s), `MaxAttempts` (10), `InitialBackoff` (1 s),
  `MaxBackoff` (5 min), `ConfirmTimeout` (10 s); `Worker:InstanceId` (default `{MachineName}-{ProcessId}`).
- Order Worker (queue `order.processing`): `OrderWorker:Concurrency` (4), `OrderWorker:MaxAttempts` (5).
  Temporary failures are retried after 5 s / 30 s / 2 min / 10 min (delay queues `omnichannel.delay.*`,
  `RabbitMq:RetryDelays` to change the tiers). After the last attempt, or at once for a permanent error (bad data, check or
  foreign-key violation), the task becomes `Failed`, a `processing.failed` row is written to `SystemEventLogs`, and the message
  is moved to `order.processing.dlq` (inspect it in the management UI).
- Manual retry: a Manager/Admin calls `POST /api/processing/tasks/{id}/retry` on a `Failed` task. The original message is copied
  into a new outbox row (new message id, original routing key), the task becomes `Pending` with a fresh attempt budget
  (`OrderWorker:MaxAttempts` new attempts; attempt numbers keep counting), and `processing.retry_requested` is logged. The copy in
  the DLQ is left there for inspection. Look up the result with `GET /api/processing/tasks/{id}` or `GET /api/orders/{orderId}`.

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
  - list pages keep paging, sorting and search in the query string (`useListQueryState`), so a reload or a shared link
    shows the same list;
  - form dialogs submit through `useFormSubmission` and reset through `useResetFormOnOpen` (a dialog reopened before its
    close animation ends must not show the previous record);
  - no business rule is repeated in the client: flags such as `isLowStock` and normalisation such as the SKU come from
    the API; actions a role may not use are hidden, and the API stays the authority;
  - each page is its own chunk (`React.lazy` in `src/app/router.tsx`); the shell stays while a page loads.
- Pages so far: Products, Customers, Channels, Users. The other menu items show a "coming in task NNN" placeholder.
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

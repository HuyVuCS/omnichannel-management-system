# Omnichannel Order & Inventory Management System

A production-oriented system that takes orders from several sales channels (Website, POS, digital marketplace), keeps one
authoritative stock per product, and processes everything that follows an order — stock reservation, stock
synchronisation to the channels, invoices, e-mails, reports — asynchronously, with every task traceable and retryable.

[![CI](https://github.com/HuyVuCS/omnichannel-management-system/actions/workflows/ci.yml/badge.svg)](https://github.com/HuyVuCS/omnichannel-management-system/actions/workflows/ci.yml)

## Highlights

- **No overselling.** Stock is `TotalStock` / `ReservedStock` / `AvailableStock`; a reservation is one PostgreSQL
  transaction (row locks taken in `ProductId` order, so concurrent orders cannot deadlock). No external call happens
  inside that transaction.
- **Reliable messaging.** Transactional outbox → RabbitMQ → dedicated Worker Service. Messages are treated as possibly
  duplicated: workers are idempotent, retry with backoff, and dead-letter after the configured limit. A message is
  acknowledged only after its result is persisted.
- **Duplicate-safe intake.** `UNIQUE(ChannelId, ExternalOrderId)` stops the same channel order from being processed twice.
- **Every async task is traceable.** `ProcessingTask` / `ProcessingAttempt` record `OrderId`, `TaskId`, `CorrelationId`,
  `MessageId`, worker name and instance, and attempt number. Invoice, e-mail and stock-sync tasks have their own status;
  one order status never stands in for them.
- **Near-real-time UI.** SignalR pushes hints only; PostgreSQL stays the source of truth and the UI refetches from the API
  after a reconnect.
- **Operations built in.** Alerts, system event log / audit, stale-task sweep, channel reconciliation, Excel export,
  structured logs (Serilog), resilience policies (Polly).
- **Security.** ASP.NET Core Identity + JWT with revocation, role-based access (`Admin`, `Manager`, `Staff`), validated
  input, signed webhooks, rate limiting, least-privilege database roles, secrets kept out of source control.

## Architecture

```
 Website / POS / Marketplace
        │  (order-entry UI · signed webhooks)
        ▼
 ┌───────────────┐   one transaction    ┌──────────────────┐
 │  ASP.NET Core │ ───────────────────▶ │   PostgreSQL     │  business source of truth
 │   Web API     │  order + task + outbox│ (EF Core/Npgsql) │
 └──────┬────────┘                      └────────▲─────────┘
        │ SignalR hints                           │ reserve stock, record attempts
        ▼                                         │
 ┌───────────────┐        ┌──────────────┐   ┌────┴─────────────────────────────┐
 │ React + TS UI │        │   RabbitMQ   │◀──│ Workers: Outbox Publisher, Order, │
 │ RTK Query     │        │ retry · DLQ  │──▶│ Stock Sync, Invoice, Notification,│
 └───────────────┘        └──────────────┘   │ Report Export, Reconciliation     │
                                             └────┬─────────────────────────────┘
                                                  ▼
                                    S3-compatible object storage
                                    (product images, export files)
```

| Layer | Technology |
| --- | --- |
| Frontend | React 19, TypeScript, Redux Toolkit + RTK Query, Ant Design 6, i18next (vi/en), Vite |
| API | ASP.NET Core Web API (.NET 10), ASP.NET Core Identity + JWT |
| Background processing | .NET Worker Service host |
| Database | PostgreSQL, Entity Framework Core, Npgsql |
| Messaging | RabbitMQ (topic exchange, per-queue DLQ) |
| Realtime | ASP.NET Core SignalR |
| Storage | S3-compatible object storage (SeaweedFS in development and production; AWS SDK as client) |
| Observability / resilience | Serilog, Polly |
| Reports | ClosedXML (Excel) |
| Delivery | Docker, GitHub Actions CI, Vercel (frontend) |

### Repository layout

```
backend/            .NET 10 solution (Omnichannel.slnx)
  src/Omnichannel.Api             Web API: controllers, auth, middleware, SignalR hub
  src/Omnichannel.Application     Use cases, contracts, validators
  src/Omnichannel.Domain          Entities and enums (no framework dependencies)
  src/Omnichannel.Infrastructure  EF Core, Identity, RabbitMQ, object storage, migrations
  src/Omnichannel.Workers         Worker Service host (all background workers)
  src/Omnichannel.Tests           xUnit unit + integration tests (Testcontainers)
frontend/           React + TypeScript dashboard (Vitest, Playwright e2e)
docker/             Compose files (development, production, acceptance), Dockerfiles, DB init, proxy config
scripts/            Setup, deploy, load-test and smoke/acceptance scripts (PowerShell)
docs/               Operations guide, runbook, acceptance evidence, implementation log
.github/workflows/  CI: backend, frontend, image builds, end-to-end acceptance
```

## Getting started (development)

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0.1xx or later
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Node.js](https://nodejs.org/) 22.12 or later (`frontend/.nvmrc` pins 24)
- PowerShell (the setup and smoke scripts target Windows PowerShell / PowerShell 7)

The commands below are PowerShell, run from the repository root. They use `127.0.0.1` rather than `localhost` on purpose:
the containers publish ports on IPv4 only, and the refused IPv6 attempt costs about 2 s per new connection on Windows.

### 1. Start the infrastructure

```powershell
Copy-Item docker/.env.example docker/.env        # then replace every value in docker/.env
powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1   # generates the object-storage keys
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
```

This starts PostgreSQL (`5432`), RabbitMQ (`5672`, management UI `15672`) and the object-storage emulator (`8333`), all
bound to `127.0.0.1`. On the first start the init script creates two database roles: `omnichannel_migrator` (owns the
schema, runs migrations) and `omnichannel_app` (DML only, used by the API and Workers).

### 2. Build and test the backend

```powershell
cd backend
dotnet tool restore
dotnet build
dotnet test      # integration tests need Docker; add --filter "Category!=Integration" for unit tests only
```

### 3. Configure secrets (user-secrets, never `appsettings`)

```powershell
cd src/Omnichannel.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes, e.g. openssl rand -base64 48>"
dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com"
dotnet user-secrets set "BootstrapAdmin:Password" "<12+ chars, upper + lower + digit>"

cd ../Omnichannel.Workers
dotnet user-secrets set "ConnectionStrings:Default" "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
dotnet user-secrets set "RabbitMq:UserName" "<RABBITMQ_USER>"
dotnet user-secrets set "RabbitMq:Password" "<RABBITMQ_PASSWORD>"
cd ../../..
```

`BootstrapAdmin` creates the first Admin on API start-up if that e-mail does not exist yet; remove it afterwards. The
object-storage keys were already stored in both user-secrets by `setup-object-storage.ps1`.

### 4. Apply the database migrations

```powershell
$env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=127.0.0.1;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
cd backend
dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
```

### 5. Run the API and the Workers (two terminals)

```powershell
cd backend
dotnet run --project src/Omnichannel.Api --launch-profile http     # http://localhost:5080
dotnet run --project src/Omnichannel.Workers
```

- Swagger UI (Development only): <http://localhost:5080/swagger> · health: <http://localhost:5080/health>
- The Workers host runs the Outbox Publisher, Order, Stock Sync, Report Export, Invoice and Notification workers, the
  Reconciliation worker, and the maintenance sweeps (stale tasks, export retention, image clean-up).

### 6. Run the frontend

```powershell
cd frontend
npm ci
npm run dev        # http://localhost:5173, proxies /api (and the SignalR hub) to http://localhost:5080
```

Sign in with the bootstrap Admin from step 3, then create `Manager` and `Staff` users from the Users page.

## Roles

| Role | Access |
| --- | --- |
| `Admin` | Everything, including user management and the user-related audit events |
| `Manager` | Operational data plus Logs/Audit and Reports/Excel export |
| `Staff` | Operational views and order entry; no Users, Logs or Reports pages (the API answers 403) |

Policies in code: `AdminOnly`, `ManagerOrAdmin`, `AnyStaff`.

## Testing

| What | Command |
| --- | --- |
| Backend unit + integration tests | `cd backend; dotnet test` (Docker required) |
| Frontend typecheck, lint, unit tests | `cd frontend; npm run typecheck; npm run lint; npm test` |
| Frontend end-to-end (Playwright, with axe accessibility checks) | `cd frontend; npm run e2e` |
| Scripted end-to-end acceptance | `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-027.ps1 -Mode NoDrills` |
| Load test of the stock-reservation path | `scripts\load\run.ps1` |

CI (`.github/workflows/ci.yml`) builds and tests the backend and frontend, builds the container images, and runs the full
acceptance script plus the Playwright suite against a throwaway stack. Evidence per acceptance criterion:
[docs/acceptance.md](docs/acceptance.md).

## Deployment

The production stack (`docker/docker-compose.prod.yml`) runs PostgreSQL, RabbitMQ, the object storage, the API, the
Workers and a Caddy reverse proxy on one machine; only the proxy publishes host ports. Migrations are applied by a
one-shot `migrate` image (an EF Core migration bundle). The frontend is a static build deployed from the `production`
branch to Vercel; a push to `master` deploys nothing.

Start with [docs/operations-guide.md](docs/operations-guide.md) ("Production deployment"), then use
[docs/runbook.md](docs/runbook.md) for day-to-day operation: start/stop, release and rollback, migrations, secrets,
incidents and known limitations.

## Known limitations

- Channels, the invoice provider and the e-mail sender are **simulated**; no real marketplace integration exists.
- Orders enter through the order-entry UI and signed webhooks. Periodic **polling** of channels is not implemented.
- The production stack has no automated backup (a deliberate decision; the backup and restore scripts exist but are not scheduled).

The complete list is in [docs/acceptance.md](docs/acceptance.md) and [docs/runbook.md](docs/runbook.md).

## Documentation

| Document | Contents |
| --- | --- |
| [docs/operations-guide.md](docs/operations-guide.md) | Detailed setup, every worker, stock sync, reconciliation, webhooks, payments/invoices/e-mails, production deployment, API and frontend conventions |
| [docs/runbook.md](docs/runbook.md) | Operating the live system: release, rollback, migrations, secrets, incidents |
| [docs/acceptance.md](docs/acceptance.md) | Acceptance criteria and the evidence for each |
| [docs/implementation-log.md](docs/implementation-log.md) | Design decisions recorded while building the system |

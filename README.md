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
frontend/           React + TypeScript (not scaffolded yet)
```

## Prerequisites

- .NET SDK 10.0.1xx or later (`dotnet --version`)
- Docker Desktop

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

## API conventions

- Every endpoint requires a JWT (`Authorization: Bearer ...`) unless marked `[AllowAnonymous]`.
- Roles: `Admin`, `Manager`, `Staff`; policies `AdminOnly`, `ManagerOrAdmin`, `AnyStaff`.
- Errors are RFC 7807 `application/problem+json` with `errorCode` and `correlationId`.
- Send `X-Correlation-Id` to correlate a request across logs; the API echoes it (or generates one).
- Enums are serialised as strings.

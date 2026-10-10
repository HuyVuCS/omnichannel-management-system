# Task 025 — Ops: build and run anywhere (images, production compose, reverse proxy, cross-origin frontend, storage origin, CI)

Status: Open
Written by: Cowork, 2026-10-10
Depends on: 024b (accepted)

## Context
Doc §4 (Docker), §6 (HTTPS, CORS, rate limiting, secrets), §12 (realtime), §14 (deployment / observability); CLAUDE.md.
**Hosting decision (user, 2026-10-10):** the frontend goes to **Vercel Hobby** (another origin); API, Workers,
PostgreSQL, RabbitMQ and object storage run as **one docker compose stack on one always-on machine** — the user's own
machine (Windows, Docker Desktop, amd64) behind a tunnel, or an **Oracle Always Free** VM (Linux, **arm64**).
Production object storage is **SeaweedFS in the stack**. This task makes the stack build and run anywhere and verifies
it **locally**; going live (GitHub, Vercel, tunnel / VM — accounts of the user) is **025b**.

Every decision is in the implementation log, "Hosting decision" and "Task 025 plan" — read them first.

Read first: log "Hosting decision", "Task 025 plan", "Task 019 plan/result" (storage, review 019 q1
`PublicServiceUrl`, CORS, checksums), "Task 020 plan/result" (stored `Content-Disposition`, upload timeout, sweep),
"Task 015 plan" (hub, `LISTEN`), "Task 016 result" (WebSockets behind a proxy), "Task 023 result" (rate limits,
`ForwardedHeaders`), "Task 017 result" (frontend test stability, worker count in CI); README §1–§6, "Product images",
"Reports and Excel export", "Smoke scripts", "Frontend"; `docker/docker-compose.yml`, `docker/api.Dockerfile`,
`docker/postgres/init/01-create-roles.sh`; backend `Program.cs` (API and Workers), `AuthServiceCollectionExtensions`
(hub token), `RateLimitPolicies`, `ObjectStorageOptions` and the S3 client registration, `OmnichannelDbContextDesignTimeFactory`,
`BootstrapAdminSeeder`, both `appsettings*.json`; frontend `shared/api/baseApi.ts`, `features/realtime/signalrHub.ts`,
`vite.config.ts`, `scripts/measure-bundle.mjs`; `scripts/setup-object-storage.ps1`, `scripts/smoke/_common.ps1`; `.gitignore`.

**Stop and report (`Blocked`)** if: an image cannot run as non-root, or the Workers image cannot resolve
`Asia/Ho_Chi_Minh` / the `vi` culture without switching to another base image family; a presigned link cannot be made
valid for the public storage origin without changing how objects are signed for the API's own calls; the API needs
a secret in a file or image layer; a change of the domain, the schema or an API contract seems necessary; anything
would need a new external service or account; or a decision is needed that is not in this file or the log.

## Part A — Images

### A1. API and Workers images
- `docker/api.Dockerfile` (exists — review and keep its pattern) and `docker/workers.Dockerfile`: multi-stage, restore
  layer cached by project files, `dotnet publish -c Release`, official .NET 10 **Debian** runtime images, non-root
  (`$APP_UID`), no secret in any layer, `.dockerignore` so `bin/`, `obj/`, tests and secrets never enter the context.
- Both build for `linux/amd64` and `linux/arm64` (`docker buildx build --platform …`; the arm64 build may run under
  emulation locally — record its time).
- **Workers image checks** (a test or a scripted `docker run`, in the report): `TimeZoneInfo` finds
  `Asia/Ho_Chi_Minh`; `CultureInfo("vi")` formats as on the development host (not invariant mode); an orders export in
  `vi` with the time zone above builds and opens (ClosedXML without installed fonts — roadmap item "font-less
  workbook"; if column auto-fit needs a font, decide the smallest fix and report it).
- Data protection: find out whether anything uses ASP.NET Core Data Protection keys (Identity token providers,
  antiforgery, cookies). If yes, persist the key ring to a volume of the API; if no, say so in the report with the
  evidence.

### A2. Migration bundle image
`docker/migrate.Dockerfile`: builds `dotnet ef migrations bundle` for the current migrations (self-contained for the
target platform) into a small runtime image; it runs with the connection of the **migrator** role from an environment
variable and applies pending migrations, nothing else. The tasks README rules (additive only, list before update) are
unchanged for any environment the user owns; this task only applies it to its own throwaway environment.

## Part B — Production compose

### B1. `docker/docker-compose.prod.yml` (the dev file stays as it is)
- Services: `postgres` (same init script, volume), `rabbitmq` (management UI **not** published), `objectstorage`
  (SeaweedFS 4.48 as in dev; volume; bucket and CORS origins from the environment), `migrate` (one-shot, profile or
  `run --rm` — documented order: migrate, then up), `api`, `workers`, `proxy` (Caddy).
- **Only `proxy` publishes host ports**, bound to `127.0.0.1` by default (a tunnel on the same host connects there);
  a documented switch publishes on all interfaces with TLS for a VM with its own public name.
- Health checks and `depends_on: condition: service_healthy` for every dependency; `restart: unless-stopped`; memory
  limits sized for the smallest target (12 GB VM shared by everything; state the numbers and the measured idle use);
  log rotation for the json-file driver.
- `ASPNETCORE_ENVIRONMENT=Production` for API and Workers: Development-only simulator rules off, Swagger off, compact
  JSON logs. Simulated invoice provider and e-mail sender stay (no real accounts) — say so in the README.
- `workers` can be scaled (`--scale workers=2`): each instance has its own `WorkerInstanceId` (check how it is derived
  in a container — the hostname is fine, a constant is not).

### B2. Secrets and configuration
- An environment template next to the compose file (mind `.gitignore`: `.env.*` is ignored except `.env.example` —
  choose a name that is committed, e.g. `docker/prod.env.example`) with every variable by name and purpose, no value.
- `scripts/deploy/new-prod-env.ps1` (PowerShell 5.1, ASCII): writes a git-ignored env file with fresh random values for
  every password / key (database roles, RabbitMQ, JWT signing key ≥ 256 bits, object-storage key pair), keeps an
  existing file unless `-Force`, never prints a value. The bootstrap admin e-mail is a parameter; its password is
  generated and shown **once** with a warning, or read from a prompt — choose and document.
- No secret in `appsettings*.json`, images or compose files; everything through environment variables.

### B3. Reverse proxy (`docker/proxy/Caddyfile`)
- Two sites: the **API origin** (`/api/*` and `/health` → `api:8080`; WebSocket upgrades for `/api/hubs/*`; request
  body limit ≥ the largest the API accepts; nothing else served) and the **storage origin** (→ `objectstorage:8333`,
  **Host header preserved**, no path rewrite, no buffering limits that break a 5 MiB PUT).
- Mode by environment: hostnames set → automatic HTTPS; otherwise plain HTTP on two local ports (a tunnel terminates
  TLS in front). `X-Forwarded-For` / `-Proto` set by Caddy; headers arriving from outside are trusted only from a
  configured upstream (the tunnel on loopback), otherwise overwritten.
- Access log without query strings (the hub's `access_token` — log "Token in log events", review 015).

## Part C — Application changes for the deployment (backend + frontend, small)

### C1. API behind the proxy
- `UseForwardedHeaders` first in the pipeline, `KnownProxies` / `KnownNetworks` = the proxy only (configuration, not
  "trust all"), `ForwardLimit` 1. Effects, each tested: client address of a request through the proxy is the real one
  (webhook rate limit, request log); `Request.Scheme` is `https` when the edge says so; **no redirect loop**:
  `UseHttpsRedirection` does not redirect such a request, and a plain-HTTP production-like local run works (document
  the setting that allows it — default stays "redirect").
- `Cors:AllowedOrigins` from the environment (the frontend origin only); a preflight from another origin gets no CORS
  headers (test).

### C2. Storage public origin (review 019 q1)
- `ObjectStorage:PublicServiceUrl` (optional): when set, presigned PUT / GET links are built by a **presign-only**
  client for that origin; the API's and Workers' own calls (HEAD, ranged GET, PUT of exports, DELETE) keep using
  `ServiceUrl` inside the network. Unset = today's behaviour (dev unchanged). Options validation (absolute
  http/https URL) and tests: a link is signed for the public host; a link of the internal host is never handed out
  when the public one is set.
- Bucket CORS origins from the environment (the frontend origin); README "Product images" updated.

### C3. Frontend on another origin
- `VITE_API_BASE_URL` (build time): `baseApi` and the hub URL use it; empty or unset = `window.location.origin` as
  today. Absolute URL only, no trailing-slash surprises (test both forms). The access token still goes in the
  `Authorization` header and, for the hub only, in `access_token` (redaction unchanged).
- Tests: base URL and hub URL with and without the variable; the existing suite unchanged otherwise.
- No `vercel.json` here (025b).

## Part D — Checks against the production stack (roadmap items from reviews 016, 019, 020, 023)
Run on the production-like local stack (B1 with C1–C3), results in the report:
1. Storage provider checks (review 019/020): `HEAD` of a missing object → 404; CORS response headers for the
   frontend origin on `PUT` / `GET` (header case as the browser needs); default SDK request checksums accepted;
   the stored `Content-Disposition` comes back on a presigned GET; one export at `MaxRows` uploaded within
   `ObjectStorage:RequestTimeout` (measure; add the Workers-only `UploadTimeout` only if it needs more than half).
   Keep the export sweep (no lifecycle rule in SeaweedFS needed) — say so.
2. WebSockets: the hub connects **with the WebSocket transport** through the proxy (not only long polling).
3. Multi-instance: with `--scale workers=2`, a stock import that fans out into several stock-sync tasks completes each
   task exactly once, attempts show two different `WorkerInstanceId`s, no task is processed twice (log "Task 014":
   per-channel queues).
4. A restart of `api` and of `workers` (`docker compose restart`) loses no task: an order submitted while `workers` is
   down is processed after it comes back (outbox + durable queues).
5. Nothing but the proxy listens on host ports (`docker compose ps` / `netstat`).

## Part E — CI (`.github/workflows/ci.yml`)
- On push to `master` and on pull requests; `concurrency` cancels superseded runs.
- Job **backend**: `actions/setup-dotnet` from `global.json`, restore, `dotnet build` (warnings are errors already),
  `dotnet test` (Testcontainers on the runner's Docker); test results as an artifact on failure only (500 MB artifact
  quota on private repositories).
- Job **frontend**: Node from `.nvmrc`, `npm ci`, typecheck, lint, `npm test` with an explicit `--maxWorkers` sized for
  the 2-core runner (log "Task 017 result"), build.
- Job **images**: `docker buildx` builds of the API, Workers and migrate images for `linux/amd64` (no push); the arm64
  builds only on `workflow_dispatch`.
- No secret is needed by any job; no deployment. Validate the workflow locally with `actionlint` (or the closest
  available check) — it cannot run on GitHub until 025b; say so, do not claim a run.

## Part F — Smoke and browser check
- `scripts/smoke/task-025.ps1` against the production-like stack (`SMOKE_BASE_URL` = the proxy's API origin; the
  frontend steps run as usual, not skipped — the frontend changed): health through the proxy; admin login; CORS
  preflight from the configured origin allowed, from another origin refused; a request through the proxy is not
  redirected; hub negotiation offers WebSockets; product image presign → `PUT` to the **public storage origin** → the
  product's image link served from it; an inventory export → `Succeeded` → download link on the public storage origin →
  bytes start with `PK`; one order → `Reserved` → payment → invoice and e-mail tasks `Succeeded`; with two Workers
  instances the stock-sync attempts carry two instance ids; smoke users locked.
- **Cross-origin browser check** (production build of the frontend with `VITE_API_BASE_URL` = the proxy's API origin,
  served by `vite preview` on another origin; 1280 px): login, dashboard live (a change made through the API appears
  without Refresh, WebSocket transport in the network panel), product image upload and display, an export downloaded,
  the payment of an order with live invoice / e-mail status. No `Authorization` header on any storage request.

## Acceptance criteria
- [ ] Commits by part (A, B, C backend, C frontend, D/F, E may be grouped sensibly; C1–C2 backend and C3 frontend
      separate).
- [ ] Backend build 0 warnings, all tests pass; new tests for C1 and C2.
- [ ] Frontend typecheck, lint, test, build pass; **2 full `npm test` runs + one `npm run test:slow`** green (C3 adds no
      new async behaviour — log "Task 022 plan" rule); first-load set before/after.
- [ ] Images: sizes and build times (amd64; arm64 under emulation) in the report; containers run as non-root
      (`docker exec … id`); A1 Workers checks; data-protection finding.
- [ ] `docker-compose.prod.yml` up from an empty state on the development machine with a fresh env file from
      `new-prod-env.ps1` (a **throwaway** project name and volumes — the development stack is not touched), `migrate`
      first, then up; idle memory per service measured.
- [ ] Part D results; `task-025.ps1` table; cross-origin browser check; then the throwaway stack is removed
      (`down -v` is allowed **only** for this task's own throwaway project name — say which).
- [ ] CI workflow validated locally; no run claimed.
- [ ] README: new section "Production deployment" (the stack, the env template and generator, migrate → up, modes
      without / with TLS, the two origins, scaling Workers, what is simulated in production, what 025b adds);
      "Product images" (`PublicServiceUrl`, CORS); "Frontend" (`VITE_API_BASE_URL`).
- [ ] "Development steps (applied)": none on the development stack expected; "Manual steps for the user": none.

## Out of scope
Creating any account or remote (GitHub, Vercel, Tailscale, Oracle); `vercel.json`; publishing images to a registry;
deployment from CI; real invoice / e-mail providers; backups and restore (025b); security headers / CSP, login
throttling and the rest of 026; Kubernetes, a second machine, a load balancer; replacing SeaweedFS.

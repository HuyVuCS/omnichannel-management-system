# Report 025 — Ops: build and run anywhere (images, production compose, reverse proxy, cross-origin frontend, storage origin, CI)

Status: Done
Commit: `f7e514b` (A images), `6b3fcfd` (C1–C2 backend), `ad66852` (C3 frontend), `85fc4b8` (B stack), `c1ac3d3` (F smoke),
`44b2908` (E CI), `1a02cf1` (README); this report in its own commit

## Summary
The whole backend runs as one docker compose stack with a single entry: `docker/docker-compose.prod.yml` (PostgreSQL,
RabbitMQ, SeaweedFS, a one-shot `migrate` bundle, API, Workers, Caddy), brought up **from an empty state on the
development machine** under the throwaway project name `omni025` with an env file from the new generator: migrate →
up, healthy in 31 s. Images for API, Workers and the migration bundle build for `linux/amd64` and `linux/arm64` and run
as non-root. The API believes forwarded headers from the configured proxy only and can sign presigned links for a
public storage origin; the frontend can call an API on another origin (`VITE_API_BASE_URL`). Checked on the stack:
smoke 48 of 48 (frontend steps included), Part D, and a cross-origin browser session on the production build (login,
live dashboard over a WebSocket, image upload, export download, payment). CI workflow written and checked
structurally — **not run on GitHub, and not checked with actionlint** (deviation 5). The throwaway stack was removed
with `down -v`; the development stack was not touched. No migration, no new package, no API contract change.

Nothing was blocking. Five points need a decision or a look of Cowork (questions 1–5).

## Files changed
**A (`f7e514b`)** — `docker/api.Dockerfile` (cross-compiling build stage, `-a $TARGETARCH`), `docker/workers.Dockerfile`
(new; `runtime:10.0`), `docker/migrate.Dockerfile` (new; EF bundle, self-contained, `runtime-deps:10.0`),
`backend/.dockerignore` (tests, launch settings, `.env.*` out of the context).

**C1–C2 (`6b3fcfd`)**
- `Api/Middleware/ReverseProxy.cs` (new) — `ReverseProxyOptions` (`KnownProxies`, `KnownNetworks`, `RedirectToHttps`),
  validator, `AddReverseProxySupport`, `UseReverseProxySupport`, `UseHttpsRedirectionBehindProxy`.
- `Api/Program.cs` — forwarded headers first in the pipeline; HTTPS redirection through the new method.
- `Api/Auth/RateLimitPolicies.cs` — comment only.
- `Infrastructure/S3/ObjectStorageOptions.cs` — `PublicServiceUrl` + validation; `S3ObjectStorage.cs` — presign-only
  client for it.
- Tests (new, 39 in all): `Middleware/ReverseProxyTests.cs`, `Integration/ReverseProxyEndpointsTests.cs`,
  `Integration/StoragePublicOriginTests.cs`; `Integration/ApiFactory.cs` (a trusted proxy address and a frontend
  origin for the test host).

**C3 (`ad66852`)** — `frontend/src/shared/api/apiOrigin.ts` (new), `apiOrigin.test.ts` (new, 14), `baseApi.ts`,
`features/realtime/signalrHub.ts`, `vite-env.d.ts`.

**B (`85fc4b8`)** — `docker/docker-compose.prod.yml`, `docker/docker-compose.prod.tls.yml`, `docker/proxy/Caddyfile`,
`docker/prod.env.example`, `scripts/deploy/new-prod-env.ps1`, `.gitattributes` (LF for the Caddyfile and the env
template), `docker/docker-compose.yml` (one comment).

**F (`c1ac3d3`)** — `scripts/smoke/task-025.ps1`. **E (`44b2908`)** — `.github/workflows/ci.yml`.
**README (`1a02cf1`)** — new section "Production deployment"; "Product images" (`PublicServiceUrl`, what was checked in
production); "Frontend" (`VITE_API_BASE_URL`); repository layout.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend, state of `6b3fcfd`) | 0 errors, **0 warnings** |
| `dotnet test` (backend, state of `6b3fcfd`) | **1798 passed**, 0 failed (4 m 2 s) — 39 more than 024b |
| `npm run typecheck`, `npm run lint` | clean |
| `npm run build` + `npm run measure` | first-load set **969.84 → 970.27 kB raw** (+0.43), 318.00 → 318.20 kB gzip; main chunk 170.96 → 170.94 kB |
| `npm test`, full, run 1 / run 2 | **500 passed (32 files)** each — 14 more than 024b; 348 s / 363 s |
| `npm run test:slow`, full | **500 passed**, 366 s, first attempt |
| `docker buildx build --platform linux/amd64` × 3 | built: API 28 s / 376 MB, Workers 19 s / 333 MB, migrate 65 s / 397 MB (base images already pulled; sizes as `docker image ls` shows them) |
| `docker buildx build --platform linux/arm64` × 3 | built: 29 s, 20 s, 65 s — no emulation is involved in the build (the SDK stage cross-compiles and the final stages have no `RUN`); the arm64 images **run** under emulation here (`uname -m` = `aarch64`, runtime 10.0.12 listed) |
| `docker compose … config -q`, also with the TLS override | valid; the override replaces the proxy's ports by 80 / 443 |
| `caddy validate` (caddy 2.11.7, in its container) | "Valid configuration" |
| `scripts\deploy\new-prod-env.ps1` | file written, 14 values set; second run without `-Force` refused; an origin with a path refused, nothing written |
| Stack from an empty state: `build` (92 s) → `up -d --wait postgres` (8 s) → `--profile migrate run --rm migrate` (6 s, 14 migrations, "Done.") → `up -d --wait` (31 s) | every service healthy |
| `scripts\smoke\task-025.ps1` against the stack | **48 of 48** (table below) |
| Part D script against the stack | 17 of 17 (below) |
| CI workflow | parsed with PyYAML and checked structurally (triggers, concurrency, three jobs, every step `uses` xor `run`, no secret reference). **Not run on GitHub; actionlint not run** |

**Containers run as non-root** (`docker compose exec <service> id`): `api` and `workers` `uid=1654(app)`; the migrate
image the same (`docker run --entrypoint id`). The third-party images (`proxy`, `objectstorage`, `postgres`,
`rabbitmq`) start as root as their publishers ship them (question 5).

**Workers image checks** (a console program mounted into the image and run with its runtime, on amd64 and on arm64;
the same program on the Windows host): the output is **byte-identical** in all three —
```
zone: Asia/Ho_Chi_Minh offset 07:00:00 -> 2026-10-11 03:04:05 +07:00
invariant mode: False; cultures known: True
culture: vi | Tiếng Việt | Vietnamese
number N2: 1.234.567,89
money C0: 1.234.567 ₫
date d: 10/10/2026
date D: Thứ Bảy, 10 tháng 10, 2026
month names: Tháng 1, Tháng 2, Tháng 3
day names: Chủ Nhật, Thứ Hai, Thứ Ba
sort a < á < b: True
```
An orders export requested with `Accept-Language: vi` and `Asia/Ho_Chi_Minh` was built by a Workers container, opened
(read as a zip): sheets "Đơn hàng", "Dòng hàng", "Thông tin", Vietnamese headers and status texts, and the creation
times of the three orders are the database's UTC times + 7 h (10:14:14Z → 17:14:14). No font is needed: the workbook
has fixed column widths (`customWidth`), nothing is auto-fitted — no fix required.

**Data protection finding:** nothing in the application protects data with ASP.NET Core Data Protection — no
`IDataProtect*` reference, `AddIdentityCore` without token providers (the password reset removes and adds the
password, no token), no antiforgery, no cookie. The framework registers it with authentication and creates a key at
every start (two start-up warnings: keys in `/home/app/.aspnet/DataProtection-Keys`, not persisted). The key is never
used, so **no volume was added** (question 3).

**`WorkerInstanceId` in a container:** `{container host name}-{process id}`, e.g. `3e35796656a0-1` and
`4ea02ecc077c-1` for two replicas — unique per instance.

**Smoke `task-025.ps1`** (stack `omni025`, two Workers instances, API `http://localhost:18080`, storage
`http://localhost:18081`, frontend origin `http://localhost:4173`):
```
OK    Step
True  01 /health through the proxy -> 200
True  02 a path that is not /api or /health is not served -> 404
True  03 an API request through the proxy is answered, not redirected (401 without a token)
True  04 the bootstrap admin logs in through the proxy
True  05 preflight from the frontend origin (http://localhost:4173) -> allowed
True  06 ... with credentials (the hub negotiates with them)
True  07 preflight from another origin -> no CORS header
True  08 create smoke manager
True  09 hub negotiation through the proxy -> 200
True  10 ... offers the WebSockets transport
True  11 a WebSocket to the hub is established through the proxy
True  12 ... the hub accepts the handshake
True  13 the website channel exists
True  14 create 8 smoke products
True  15 map them on website (each starts an initial stock sync)
True  16 ... every initial sync succeeds
True  17 one import of 10 units for each of the 8 products
True  18 ... its hint arrives over the WebSocket (no polling)
True  19 the import became 8 stock-sync tasks on website
True  20 ... all Succeeded
True  21 ... each with exactly one attempt (no task was processed twice)
True  22 ... processed by two Workers instances (two instance ids in the attempts)
True  23 presign a product image -> 200
True  24 ... the upload link is on the public storage origin (http://localhost:18081)
True  25 PUT to the storage through the proxy (no Authorization header) -> 200
True  26 ... with the CORS header a browser on the frontend origin needs
True  27 confirm the image (the API reads the object inside the network) -> 200
True  28 ... the image link is on the public storage origin
True  29 GET the image link -> 200 with the uploaded bytes
True  30 ... with the CORS header
True  31 ... and none for another origin
True  32 inventory export -> 202
True  33 the export is built within 90 s
True  34 download link -> 200
True  35 ... on the public storage origin
True  36 GET the link -> 200, the bytes start with PK (an xlsx file)
True  37 ... served as an attachment (the stored Content-Disposition)
True  38 create smoke customer
True  39 submit an order -> 202
True  40 the Order Worker reserves it
True  41 the manager pays it -> 200
True  42 the invoice task succeeds (Invoice Worker)
True  43 the e-mail task succeeds (Notification Worker)
True  44 the order has its invoice number
True  45 deactivate the smoke mappings
True  46 lock smoke account
True  48 npm run build
True  49 npm test
PASSED: all 48 checks.
```
(Expected and actual were equal in every row; step 47 is the helper's reserved `npm ci` step. The frontend steps ran
next to the stack's containers and were green; a first run of the script with the frontend step skipped was 47 of 47.)

**Part D on the stack**
1. Storage: confirming an image that was presigned but never uploaded → **400 `validation_failed`**, not a 5xx (the
   `HEAD` of a missing object is "not there", not a storage failure). CORS headers on `PUT` and `GET` for the frontend
   origin, none for another origin (smoke 26, 30, 31; a browser did both, below). The SDK's default request checksums
   are accepted (every export `PUT` and every `HEAD` of this run used them). The stored `Content-Disposition` comes
   back (`attachment`, smoke 37). **Export at `MaxRows`:** the database was seeded to exactly 100,000 products
   (throwaway database, SQL as superuser); the inventory export — 100,001 rows, 4.19 MB — took **6.6 s for the whole
   attempt** (query + workbook + upload), one attempt, inside the Workers' 768 MB limit. A 4 MiB `PUT` to the storage
   through the proxy took 2.2 s the first time and 0.16–0.25 s afterwards, so the upload is far below half of
   `RequestTimeout` (10 s): **no `UploadTimeout` added**. The export sweep stays; no lifecycle rule in SeaweedFS.
2. WebSockets: a WebSocket to the hub through the proxy (smoke 11–12, 18); the browser's hub connection was
   `ws://localhost:18080/api/hubs/notifications`, open, with messages (below). The proxy's access log shows the upgrade
   as `"uri":"/api/hubs/notifications"` — no query string; `access_token` and `X-Amz-Signature` occur 0 times in the
   proxy's log and a token 0 times in the API's.
3. Multi-instance (`--scale workers=2`): smoke 19–22.
4. Restarts: `restart api` → healthy again, a token issued before still accepted. `stop workers` → an order is
   accepted (202), its task stays `Pending` with no attempt and an unpublished message → `start workers` → `Succeeded`
   with one attempt, order `Reserved`. `restart workers` right after another order → `Succeeded`, one attempt; stock
   10 total / 2 reserved (each order reserved exactly once).
5. Host ports: only the proxy — `127.0.0.1:18080->8080`, `127.0.0.1:18081->8081`; `netstat` shows exactly these two
   listeners; no container port on `0.0.0.0`. Requests from the host reach the proxy from the edge gateway
   (`172.30.250.1`).

**Idle memory** (two Workers instances, two minutes after the last request): API 179 MiB, Workers 129 + 144 MiB,
storage 150 MiB, RabbitMQ 131 MiB, PostgreSQL 91 MiB, proxy 16 MiB — **≈ 0.84 GiB** against limits of 1 GiB
(PostgreSQL, RabbitMQ, storage), 768 MiB (API, each Workers instance), 128 MiB (proxy), 512 MiB (migrate): 5.2 GiB at
most with two Workers, on a 12 GB target.

**TLS at the proxy** (the API site as `https://localhost:8080` with Caddy's local certificate, API redirect **on**):
`/health` 200, `/api/auth/me` 401 — answered by Kestrel through Caddy, **no redirect**; a client's own
`X-Forwarded-Proto: http` / `X-Forwarded-For` over that connection changes nothing (401, the proxy logs the real
peer). Automatic certificates for public host names and the override file's ports 80 / 443 were **not** exercised
(no public name on this machine): they are validated as configuration only.

**Cross-origin browser check** — production build with `VITE_API_BASE_URL=http://localhost:18080`, served by
`vite preview` on `http://localhost:4173`, 1280 × 800, a throwaway smoke Manager. The pane was not displayed: values
were read from the DOM and from wrappers around `fetch`, `XMLHttpRequest` and `WebSocket` installed before the login;
one screenshot (product dialog with the image).
- **Login through the form** (`POST http://localhost:18080/api/auth/login`, cross-origin) → dashboard, header "Trực
  tiếp"; one WebSocket `ws://localhost:18080/api/hubs/notifications`, open.
- **Dashboard live:** an order submitted through the API at 10:25:38.981 → "Số đơn hàng" 4 → 5 and the sales figure
  changed at 10:25:39.409 / 10:25:40.564, without Refresh.
- **Image:** file chosen in the product dialog → presign (API) → `PUT http://localhost:18081/omnichannel/products/…`
  (`credentials: omit`, no `Authorization`) → confirm → "Đã lưu ảnh."; the image (64 × 64) is shown in the dialog and
  in the list, loaded from `http://localhost:18081`.
- **Export:** "Yêu cầu xuất" on the Reports page → row "Đang chờ" → "Thành công" 1.3 s later, live → "Tải về": the
  link is on `http://localhost:18081` (`rel=noreferrer`), a `fetch` of it without credentials answered 200, 9,818
  bytes starting with `PK`. The anchor click was intercepted — the browser's own save was not exercised.
- **Payment:** "Ghi nhận thanh toán" → "Đã thanh toán" 0.28 s after the click and the invoice number 0.56 s after
  it, without Refresh; "E-mail xác nhận: Thành công" was on the card when it was read a few seconds later.
- **Storage requests with an `Authorization` header: 0** of the session's storage requests; every other request went
  to `http://localhost:18080`. No horizontal scroll at 1280 px.

## Deviations from the task
1. **The .NET 10 images are Ubuntu 24.04, not Debian.** `mcr.microsoft.com/dotnet/aspnet:10.0` and `runtime:10.0`
   report "Ubuntu 24.04.5 LTS": the default tags of .NET 10 are Ubuntu-based. They are the full glibc images the log
   means ("not chiseled / Alpine") and carry ICU (`libicuuc.so.74`) and the time zone database — verified above. I did
   not treat this as the Blocked case: nothing had to be switched to make the time zone or the culture work.
2. **Empty proxy lists mean "believe nobody".** ASP.NET Core's forwarded-headers middleware reads empty
   `KnownProxies` / `KnownNetworks` as "believe everyone" — a test of mine caught it. With nothing configured the
   headers are therefore not read at all (`ForwardedHeaders.None`); loopback is not trusted either.
3. **Settings are named `ReverseProxy:*`** (`KnownProxies`, `KnownNetworks`, `RedirectToHttps`), not
   `ForwardedHeaders:*`: the section also carries the redirect switch. The redirect skips `/health` (the container
   health check asks it without TLS from inside).
4. **The proxy gives the API exactly one client address** (`header_up X-Forwarded-For {client_ip}`) instead of
   Caddy's appended list: with `ForwardLimit` 1 the API reads the last entry only, which would be the tunnel, not the
   client, once a tunnel is trusted.
5. **actionlint was not run.** It is not installed here and I did not download a tool for it without asking. The
   workflow was parsed and checked structurally with a script instead; expressions and conditions used are `github.ref`,
   `github.event_name`, `inputs.arm64`, `matrix.image`. Its first real validation is the first run on GitHub (025b).
6. **Request body limit of the proxy: 30 MB** — Kestrel's own default (28.6 MB) rounded up. The API has no smaller
   explicit limit (webhooks limit themselves to 64 KiB); a tighter edge limit would be a new rule (question 4).
7. **Webhook secrets are generated for the three seeded channels** and required by the compose file (a variable that
   is passed on empty would fail the API's start-up validation). Not listed in B2.
8. **The generated file is `docker/.env.prod`** (git-ignored by `.env.*`); the template is `docker/prod.env.example`
   (`prod.env` itself would not be ignored). The bootstrap admin password is generated and shown once;
   `-PromptAdminPassword` asks for it instead.
9. **The edge network has a fixed subnet** (`172.30.250.0/28`, gateway `.1`, proxy `.2`, dynamic addresses from
   `.8`), all four overridable. Needed for "the proxy only": the API trusts the proxy's fixed address.
10. `VITE_API_BASE_URL` that is no origin (a path, a relative address) **stops the application at start** with a
    message naming the variable, instead of falling back silently.
11. Step numbering of the smoke script: none of Part F's steps is missing; "browser" items are in the browser check.

## Blocked / questions for Cowork
1. **Tunnel and client addresses (025b).** Behind a tunnel on the same machine every request reaches the proxy from
   the edge gateway. Rate limits are per real client only when `PROXY_TRUSTED_PROXIES=172.30.250.1/32` **and** the
   tunnel sends `X-Forwarded-For` / `-Proto`. If it does not send `-Proto: https`, the API redirects to HTTPS in a
   loop unless `API_HTTPS_REDIRECT=false`. Which of the two applies must be checked with the actual tunnel
   (Tailscale Funnel) in 025b; trusting the gateway also trusts every process on the host that can reach the port.
2. **TLS mode is verified as configuration only** (deviation list, TLS paragraph). Accept for 025, or should 025b
   start with it?
3. **Data Protection warnings at every API start** (keys created, never used). Leave, silence the category, or
   persist the unused ring to make the warnings go away? I changed nothing.
4. **Edge body limit** 30 MB (deviation 6): keep, or set a small one (e.g. 1 MB) as part of 026?
5. **Third-party containers run as root** inside their containers (Caddy, SeaweedFS; PostgreSQL and RabbitMQ drop to
   their own users after start). Only `proxy` has host ports. Hardening them (`user:`, read-only root, dropped
   capabilities) is not in this task — 026?

## Out of scope / follow-ups
- `RABBITMQ` memory watermark is the image default relative to its 1 GiB limit — not tuned.
- The stack builds its images locally; `omnichannel-*:local` images remain on this machine after the run.
- `scripts/smoke/task-025.ps1` needs two Workers instances for step 22 — by design; it says so in the step.
- The dev compose profile `app` still hands out links on `http://objectstorage:8333`; `PublicServiceUrl` would fix
  that too (not changed: the dev file "stays as it is").

## Development steps (applied)
None on the development stack: no migration, no changed service, no storage key. The development containers
(`omnichannel-postgres-1`, `-rabbitmq-1`, `-objectstorage-1`) ran untouched the whole time.

Throwaway environment of this task: compose project **`omni025`** (ports 18080 / 18081, env file
`docker/.env.prod.025`). Removed at the end with `docker compose -p omni025 … --profile migrate down -v` — its six
containers (one Workers instance at that point), two networks and five volumes are gone (checked: 0 containers, 0 volumes, 0 networks of that project);
the env file and the six `omnichannel-*:025-*` image tags were deleted. No host of this repository is left running.

## Manual steps for the user
None.

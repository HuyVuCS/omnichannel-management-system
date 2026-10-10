# Review 025 — Ops: build and run anywhere (images, production compose, reverse proxy, cross-origin frontend, storage origin, CI)

Reviewer: Cowork, 2026-10-10
Verdict: **Accepted** — no P0–P1 finding. One P2 is a verification gap that only matters once the storage origin is
on the internet: it goes into **025b** (Part A). All eleven deviations accepted. Questions 1–5 answered below
(1 → 025b, 2 → accepted, 3 → leave, 4 and 5 → 026).

## Checked
- Report and commits `f7e514b` (A), `6b3fcfd` (C1–C2), `ad66852` (C3), `85fc4b8` (B), `c1ac3d3` (F), `44b2908` (E),
  `1a02cf1` (README), `4c82765` (report). No migration, no new package, no API contract change.
- Read in full: `Middleware/ReverseProxy.cs`, the diffs of `Program.cs`, `RateLimitPolicies.cs`,
  `ObjectStorageOptions.cs`, `S3ObjectStorage.cs`; `docker-compose.prod.yml`, `docker-compose.prod.tls.yml`,
  `proxy/Caddyfile`, the three Dockerfiles, `backend/.dockerignore`, `prod.env.example`, `new-prod-env.ps1`;
  `apiOrigin.ts` and the `baseApi.ts` / `signalrHub.ts` diffs; `.github/workflows/ci.yml`; README "Production
  deployment"; the header and credential handling of `task-025.ps1`; the test names of the three new test files.
- Checked by Cowork against the code:
  - **Trust boundary of forwarded headers is correct and fail-closed.** `KnownProxies` / `KnownIPNetworks` are cleared
    (loopback no longer implicit) and only configured entries are added; nothing configured → `ForwardedHeaders.None`
    (deviation 2 — the framework's "empty = everyone" trap, caught by a test). `ForwardLimit` 1 together with Caddy's
    `header_up X-Forwarded-For {client_ip}` (deviation 4) means the API reads exactly one address that the proxy
    determined. Options are validated on start without echoing values. Tests cover spoofed headers from a
    non-proxy, a client-sent `X-Forwarded-For`, network membership, blank entries, the health path, both redirect modes.
  - **Pipeline order:** forwarded headers first, then correlation id and request logging; the redirect skips only
    `/health`. Rate limits and the webhook log therefore see the client address (integration tests).
  - **Presign-only client:** a second `AmazonS3Client` used only for `GetPreSignedURL` (no request is sent), protocol
    taken from the public URL, disposed separately; `PublicServiceUrl` blank = unchanged behaviour. Test: a link of the
    internal host is never handed out when the public one is set; https links whatever the inner scheme.
  - **Compose:** only `proxy` publishes ports (`127.0.0.1` default); `backend` network `internal: true` (database,
    broker, storage and Workers have no route out — correct while every provider is simulated); the proxy has a fixed
    address that is the only `KnownProxies` entry; all secrets are `${VAR:?}` from the env file; `migrate` behind a
    profile with the migrator role; health checks gate start order; log rotation and memory limits present.
  - **Caddy:** two sites, Host and path untouched on the storage site, `admin off`, access log without query string,
    `Authorization`, `Cookie`, `Referer`, and with the query stripped from `Location`; the health site is not
    published; `trusted_proxies` defaults to nobody reachable.
  - **Images:** SDK stage on `$BUILDPLATFORM` with `-a $TARGETARCH` (no emulated compilation), restore layer cached,
    `USER $APP_UID`, `.dockerignore` keeps tests, launch settings and `.env*` out. The migration bundle gets its
    connection only at run time.
  - **Env generator:** CSPRNG with rejection sampling, alphanumeric (safe in compose and Npgsql strings), refuses to
    overwrite, prints only the admin password once (or prompts).
  - **Frontend:** `VITE_API_BASE_URL` validated as an origin, fails loudly at start otherwise (deviation 10), page
    origin when unset; REST and hub share `apiUrl`.
  - **CI:** no secret, `permissions: contents: read`, `pull_request` (not `pull_request_target`) — safe for the public
    repository decided for 025b; arm64 only on dispatch.
  - **History before publication** (names only, no content printed): no `.env`, key or certificate file was ever
    tracked; `docker/.env.example` holds placeholders only; `SigningKey` is empty in every committed `appsettings`.
    The full content audit is 025b A1.
- **Verification** (not re-run by Cowork): backend 0 warnings, 1798/1798; frontend typecheck / lint / build clean,
  500 tests, two full runs and the slow run green; first-load set +0.43 kB raw; images for both architectures,
  non-root; stack from an empty state (`omni025`) healthy in 31 s; smoke 48/48 with two Workers instances; Part D
  17/17 including the 100,000-row export in 6.6 s and restart without lost tasks; cross-origin browser session on the
  production build; throwaway project removed with `down -v` (own project only), development stack untouched.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | .NET 10 images are Ubuntu 24.04, not Debian | Accepted — the task's "Debian" was a wrong assumption about .NET 10 tags; what mattered (full glibc image with ICU and tzdata, not chiseled / Alpine) holds and is verified byte-identical on amd64 / arm64 / Windows. Not the Blocked case. |
| 2 | Nothing configured = forwarded headers not read | Accepted — fail-closed; the framework default would trust everyone. |
| 3 | Section `ReverseProxy:*`; `/health` never redirected | Accepted — one section for trust and redirect; the health check runs without TLS inside the network. |
| 4 | Proxy sends exactly one client address | Accepted — required for `ForwardLimit` 1 once a tunnel is trusted. |
| 5 | actionlint not run | Accepted — not downloading tools unasked was right. 025b A2 runs it (container image), before the first push. |
| 6 | Edge body limit 30 MB | Accepted for now — question 4. |
| 7 | Webhook secrets generated and required | Accepted — the API refuses to start with an empty secret; the generator must produce them. |
| 8 | `docker/.env.prod` / `docker/prod.env.example`; admin password shown once or prompted | Accepted. Follow-up in the 025b runbook: blank `BOOTSTRAP_ADMIN_PASSWORD` after the first login (the seeder skips blank values), so it no longer sits in the container environment. |
| 9 | Fixed edge subnet, proxy `.2`, gateway `.1` | Accepted — the API's trust needs a fixed proxy address; all four overridable. |
| 10 | Invalid `VITE_API_BASE_URL` stops the app at start | Accepted — a wrong build must not send tokens somewhere unexpected. |
| 11 | Smoke numbering vs Part F | Accepted. |

## Questions of the report
1. **Tunnel and client addresses → 025b, measured first, with a decision rule.** The Funnel spike of 025b (Part C2)
   reads what actually reaches the proxy (Caddy's access log already records `remote_ip`, `client_ip`, `Host` and the
   `X-Forwarded-*` headers) for a request from outside. Rule:
   - Funnel sends `X-Forwarded-Proto: https` and a public client address in `X-Forwarded-For` →
     `PROXY_TRUSTED_PROXIES=172.30.250.1/32`, `API_HTTPS_REDIRECT=true`.
   - `-Proto` missing → `API_HTTPS_REDIRECT=false`. Safe here: Funnel accepts TLS only, so the only plain-HTTP
     requests are local ones to `127.0.0.1`.
   - No usable client address → keep trusting nobody and **record the consequence**: the login limit (10/min per
     address) and the webhook limit become one shared bucket for all clients — a cheap denial of login. Accepted for
     go-live of a demo system only; 026 (login throttling per account + address) and the TLS-mode host (025c) are the
     fixes.
   Trusting the gateway also trusts every process on the host (Docker Desktop delivers host-local connections from
   the gateway) — accepted on a single-user machine, written in the runbook.
   Also measured in the spike: whether Funnel keeps the `Host` header on the storage port — a presigned link is
   signed for `host:8443`; a rewritten Host breaks every upload (025b C2 has the allowed fix).
2. **TLS mode as configuration only → accepted for 025.** 025b goes live through Funnel, which ends TLS itself, so
   Caddy's TLS mode stays unused; the README must say "validated as configuration, not exercised" until the first
   host with public names (025c).
3. **Data Protection warnings → leave as they are, no volume, no silencing.** Nothing uses the key ring (finding with
   evidence in the report). Silencing the category would also hide the warning on the day something does use it;
   persisting an unused ring adds a volume for nothing. 025b runbook lists them as two expected start-up warnings.
   Revisit: anything that uses Data Protection (Identity token providers, cookie authentication — the 026
   httpOnly-cookie revisit) → persist the ring to a volume then.
4. **Edge body limit → 026.** Set Kestrel's `MaxRequestBodySize` application-wide to what the largest legitimate body
   needs (measure the stock import and order bodies; images and exports never pass through the API) with
   per-endpoint exceptions if any, and the proxy's `request_body` to the same; webhooks keep their 64 KiB.
5. **Third-party containers as root → 026**, as part of container hardening for all services: `no-new-privileges`,
   `cap_drop: [ALL]` with the minimum added back, read-only root file systems with `tmpfs` where possible, Caddy as a
   non-root user in tunnel mode (ports 8080/8081 need no capability), SeaweedFS as a non-root user with the volume
   owned accordingly. Not urgent: only `proxy` has host ports and it runs no shell-reachable code.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | **Anonymous access to the storage origin was not verified.** Smoke 31 checks CORS for another origin, not that an unsigned `GET` of an object, an unsigned bucket listing and an unsigned `PUT` are refused. Harmless on `127.0.0.1`; in 025b the storage site is on the internet and the whole S3 API of SeaweedFS is behind it. | **025b A4**: three refusal steps in `task-025.ps1`, run locally and live. |
| P3 | `BOOTSTRAP_ADMIN_PASSWORD` stays in `docker/.env.prod` and in the API container's environment (`docker inspect`) for the life of the stack. | **025b runbook**: blank it after the first login, `up -d api`. |
| P3 | `--scale workers=2` must be repeated on every `up`, or compose goes back to one instance (and smoke 22 fails). | **025b A6**: `deploy.replicas: ${WORKERS_REPLICAS:-1}`; production sets 2. |
| P3 | Third-party tags float within their line (`postgres:17`, `rabbitmq:4-management`, `caddy:2.11`, SeaweedFS pinned). `up` does not pull an image that exists, so nothing changes silently. | No change. Runbook: updating a third-party image is a deliberate step, after a backup. |
| — | `workers` has no route out (`backend` is internal). | Correct while providers are simulated. Revisit with the first real provider (log row). |
| — | RabbitMQ memory watermark not tuned to the 1 GiB limit (report follow-up). | 026 (performance). |
| — | Dev compose profile `app` still signs links for `objectstorage:8333`. | No action unless that profile is used again; the fix is `PublicServiceUrl`. |
| — | Commit author address in all 196 commits is `dotranhuyvu@.com` (no domain): not a real address, so nothing personal leaks with a public repository, but GitHub will not link the commits to an account. | User's choice in 025b B1 (GitHub no-reply address for future commits); history is **not** rewritten. |

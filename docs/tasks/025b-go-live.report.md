# Report 025b — Ops: go-live (public GitHub repository + CI, Vercel, backend through Tailscale Funnel, backups, runbook)

Status: Partially done
Commit: `264270c` (Part A), `431c876` (runbook: live values), runbook reboot result + this report in the commit after it

## Summary
The system is live: frontend on Vercel (`https://omnichannel-management-system-front.vercel.app`, branch `production`),
backend on the user's machine through Tailscale Funnel (API `https://omni.tail70815b.ts.net`, storage
`https://omni.tail70815b.ts.net:8443`), public repository `HuyVuCS/omnichannel-management-system` with green CI.
Funnel sends the client's public address and `X-Forwarded-Proto: https` and keeps `Host` on both ports: case 1 of
review 025 q1 applies, the Caddyfile needed no fix, the hub connects with WebSockets through Funnel.
**Not done: backups on production** — the user decided on 2026-10-12 to skip E4 (backup by hand, scheduled task) and
with it E5 (restore drill of a production backup). The scripts exist and were exercised on throwaway stacks only.
Production has no backup.

## Files changed
- `frontend/vercel.json` — SPA fallback, `immutable` for `/assets/*`, deployments from `production` only
- `docker/docker-compose.prod.yml` — `workers.deploy.replicas: ${WORKERS_REPLICAS:-1}`; image label `org.opencontainers.image.revision: ${GIT_COMMIT:-unknown}` on the three builds
- `docker/prod.env.example` — `WORKERS_REPLICAS`
- `scripts/deploy/_stack.ps1`, `backup-prod.ps1`, `restore-prod.ps1`, `register-backup-task.ps1` — new
- `scripts/smoke/task-025.ps1` — steps 32–35 (unsigned GET, listing, PUT refused; key absent afterwards), later steps renumbered; `SMOKE_ENV_FILE`
- `scripts/smoke/_common.ps1` — `SMOKE_ENV_FILE`: the admin is read from the stack's env file
- `scripts/smoke/task-025b.ps1` — new, read-only live check
- `docs/runbook.md` — new
- `README.md` ("Production deployment"), `docs/tasks/README.md` ("Production steps", report template)
- No product code changed (backend, frontend sources untouched), no migration, no package.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| gitleaks v8.28.0 (container, read-only mount), `git --log-opts=--all --redact=100` | 196 commits scanned before Part A, 197 after: **6 findings**, all triaged as not secrets (below); accepted by the user before the push |
| secret-variable assignment check (scratch script, every added line of all 197 commits) | 69 assignments (env / yaml / appsettings / connection strings): 65 empty or placeholders, 4 connection strings in test files (fixtures) |
| tracked files over 1 MB / blobs over 1 MB in history | none |
| actionlint 1.7.7 (container) on `.github/workflows/ci.yml` | no finding, exit 0 |
| `dotnet build` (backend) | 0 errors |
| `npm run build` (frontend), `prettier --check vercel.json` | built; formatted |
| `scripts\smoke\_assert-selftest.ps1` | 64 / 64 |
| `task-025.ps1` on throwaway `omni025b` (`WORKERS_REPLICAS=2`, no `--scale`) | 51 / 51 |
| `backup-prod.ps1` on `omni025b` | exit 0; dump 86,693 B, archive 225,280 B, manifest with commit and 14 migrations; storage down 14.4 s |
| `restore-prod.ps1` into `omni025b-r` | healthy after 40 s, "migrate: nothing pending"; row counts identical (Orders 1, OrderItems 1, Inventories 8, InventoryMovements 9, ProcessingTasks 21, OutboxEvents 21, AspNetUsers 2; 102 storage files); login with the source stack's admin; stored image read through the restored storage (200, 70 bytes) |
| `restore-prod.ps1` a second time into `omni025b-r` | refused (7 containers, 5 volumes), exit 1 |
| retention test (44 fabricated folders in a temporary destination) | 31 removed; kept: 7 most recent days (two backups of one day both kept), 4 Sundays; a folder of another project, one without manifest and a foreign folder untouched |
| `omni025b`, `omni025b-r`: `--profile migrate down -v` (those projects only) | removed, env files deleted; development stack untouched |
| First CI run on GitHub (push `264270c`) | success: Backend 4.7 min, Frontend 4.4 min (`--maxWorkers=2` kept), Image api 0.6, workers 0.8, migrate 1.1 min |
| CI by `workflow_dispatch` with `arm64=true` | success: Backend 4.2, Frontend 5.4, Image api 0.9, workers 1.0, migrate 1.8 min; "Build for linux/arm64" succeeded in all three (the CLI answered HTTP 500 to the dispatch, the run was created anyway) |
| `task-025.ps1` live (`SMOKE_BASE_URL=https://omni.tail70815b.ts.net`, storage `:8443`, frontend origin of Vercel, `SMOKE_ENV_FILE=docker\.env.prod`, frontend steps skipped) | **51 / 51** (from this machine = tailnet path) |
| `task-025b.ps1` live (`SMOKE_EXPECT_HSTS=true`) | **12 / 12**; plain HTTP on the API host: not served |
| Backend / frontend test suites | not run locally: no product code changed; both ran green on GitHub (above) |

gitleaks findings (rule `generic-api-key` in all six; no value shown):

| File | Line | Commit | Triage |
| --- | --- | --- | --- |
| `docker/.env.example` | 23 | `281108c` | placeholder `replace-with-…` |
| `backend/src/Omnichannel.Tests/Integration/ApiFactory.cs` | 20 | `b16c026` | test-only signing key of the test host |
| `backend/src/Omnichannel.Tests/Realtime/RealtimeHintUnitTests.cs` | 391 | `f74251b` | fabricated token of a redaction test |
| `backend/src/Omnichannel.Tests/Integration/UserManagementTests.cs` | 393 | `567e3b0` | made-up wrong password |
| `frontend/src/features/auth/auth.test.tsx` | 45 | `0b360ec` | password of a mocked login |
| `frontend/src/features/users/users.test.tsx` | 289 | `0b360ec` | password of a mocked reset |

Names list (A1): `docs/tasks/014-channel-product-mapping-and-stock-sync.report.md` holds one `C:\Users\ACER\…` temp-log
path; `docs/implementation-log.md` and review 025 name the commit address; author of every commit `HuyVuCS`.

Repository settings read back with `gh api`: visibility public; secret scanning and push protection enabled;
Dependabot alerts enabled (`PUT vulnerability-alerts` → 204), security updates disabled; Actions default token `read`,
`can_approve_pull_request_reviews: false`; fork pull requests `first_time_contributors`; ruleset 24930227 "protect
master and production" active: `deletion`, `non_fast_forward` on both branches. No required pull request or status
check: one developer, Claude Code pushes to `master`.

Vercel: `git.deploymentEnabled` per https://vercel.com/docs/project-configuration/git-configuration (minimatch
patterns; a branch deploys when at least one matching rule is true) — `{"**": false, "production": true}`; rewrites
are applied after the filesystem (https://vercel.com/docs/project-configuration/vercel-json). GitHub lists one
deployment (Production, `264270c`); the later push of `431c876` to `master` created none.

Funnel measurement (proxy access log; client addresses not reproduced here):

| Path | Port | `remote_ip` | `client_ip` after trust | `Host` | `X-Forwarded-For` | `-Proto` | `-Host` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Funnel (phone, mobile data; 33 requests) | 443 | 172.30.250.1 | the phone's public address | `omni.tail70815b.ts.net` | same public address | https | as Host |
| Funnel | 8443 | 172.30.250.1 | the phone's public address | `omni.tail70815b.ts.net:8443` | same | https | as Host |
| Tailnet (this machine) | 443 / 8443 | 172.30.250.1 | 100.x tailnet address | as above | 100.x | https | as Host |

Before trusting the gateway: `client_ip` = 172.30.250.1 and `GET /api/auth/me` → 307. After
`PROXY_TRUSTED_PROXIES=172.30.250.1/32` (`API_HTTPS_REDIRECT=true` kept): 401, `Strict-Transport-Security` present.
Funnel path, from the log: login 200; presign 200 → `PUT` to the storage 200 → confirm 200 (image uploaded from the
phone); image `GET` 200; `GET /api/hubs/notifications` → **101** (WebSockets) on both paths.
Browser checks were done by the user (Claude Code does not type a production password): desktop — login, export
created and downloaded (storage 200), payment 200, deep-link refresh; phone over mobile data — login, order, image
upload and display. Confirmed by the user and by the matching requests in the access log; no screenshots.

## Deviations from the task
1. **gitleaks found 6 lines** ("anything found → Blocked"). All are placeholders or test fixtures; shown to the user by
   file, commit and rule; the user accepted publication (2026-10-12). History not rewritten.
2. **E4 / E5 skipped by the user**: no production backup, no scheduled task, no drill on a production backup.
   Retention and restore were verified on throwaway stacks.
3. `backup-prod.ps1` has no `-EnvFile`: it uses `docker` by compose labels and reads no env file at all.
4. The fallback rewrite excludes `/assets/`: a missing hashed file answers 404 instead of HTML that the `immutable`
   rule would cache for a year.
5. Image label `org.opencontainers.image.revision` from `GIT_COMMIT` (compose `build.labels`) — the only way the
   manifest can name the commit of the running images.
6. Retention is "every backup of the 7 most recent days with a backup + the newest of each of the 4 most recent
   Sundays" (a second backup of a day, e.g. before a release, is kept).
7. Vercel project name differs from the first plan (`…-front`); the user created `production` from `master` in D1.
8. E3 browser check by the user instead of Claude Code (credentials); no screenshots.
9. Bootstrap password blanked without a preceding backup (backups skipped; change = one env line, `up -d api`).
10. New commits carry the user's GitHub no-reply address (repository-local `git config`).

## Blocked / questions for Cowork
- Production without backups: accept for the demo, or make E4 / E5 a follow-up task?
- The proxy forwards `Tailscale-User-*` headers of tailnet devices to the API and logs them: strip in 026?
- Secret-rotation procedures of the runbook are written but not exercised (marked so in the runbook).

## Development steps (applied)
None (no migration, no changed development service).

## Production steps (applied)
| Step | Result |
| --- | --- |
| `gh repo create HuyVuCS/omnichannel-management-system --public --source . --remote origin`; `git push -u origin master` | created, pushed |
| `gh api -X PUT …/vulnerability-alerts`; `gh api -X POST …/rulesets` | 204; ruleset active |
| `tailscale funnel --bg --https=443 http://127.0.0.1:8080`, `--https=8443 http://127.0.0.1:8081` | both "Funnel on" (after the user enabled Funnel for the tailnet) |
| `docker/.env.prod` by the user (`new-prod-env.ps1`); `WORKERS_REPLICAS=2` | present; never opened by Claude Code beyond its non-secret lines and variable names |
| `GIT_COMMIT=264270c…`; `--profile migrate build`; `up -d --wait postgres`; `--profile migrate run --rm migrate`; `up -d --wait` | healthy after 43 s; 14 migrations; 2 Workers |
| `PROXY_TRUSTED_PROXIES=172.30.250.1/32` in the env file; `up -d --wait` | proxy recreated; measurement above |
| `task-025.ps1` once against production | 51 / 51; leaves smoke products, one paid order, locked smoke account (accepted once) |
| `BOOTSTRAP_ADMIN_PASSWORD=` (blank); `up -d --wait api` | `docker inspect`: `BootstrapAdmin__Password` empty; health 200 |
| Reboot by the user | nothing started by hand: containers running 62 s after boot, Funnel on, login from the internet 112 s after boot |

## Manual steps for the user
- **Backups** (skipped): choose a destination that leaves the machine, then runbook section 6 —
  `backup-prod.ps1 -Destination <folder>` and `register-backup-task.ps1 -At 03:00 -Destination <folder>`; a restore
  drill (section 7). Required before the first release ("Production steps").
- Docker Desktop "Start when you sign in" and no sleep on AC are set (reboot test passed; AC sleep index 0).

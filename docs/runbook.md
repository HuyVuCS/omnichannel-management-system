# Runbook — operating the live system

For the person who runs the system. Every procedure is a list of commands from the **repository root** in Windows
PowerShell. Background and reasons: README "Production deployment", `docs/implementation-log.md` ("Hosting decision",
"Task 025 result", "Task 025b plan"). Rules for who may run what on production: `docs/tasks/README.md` "Production steps".

```powershell
# Used by every procedure below.
$stack = '--env-file', 'docker/.env.prod', '-f', 'docker/docker-compose.prod.yml'
```

`docker/.env.prod` holds every secret of the stack. It is git-ignored, never copied into a backup, never pasted
anywhere. Losing it does not lose data (a restore works with a new one), but see "Restore".

## 1. What runs where

```
 browser ──HTTPS──> Vercel (static build of frontend/, branch "production")
    │
    ├──HTTPS──> https://<host>        ── Tailscale Funnel :443  ──> 127.0.0.1:8080 ─┐
    └──HTTPS──> https://<host>:8443   ── Tailscale Funnel :8443 ──> 127.0.0.1:8081 ─┤
                                                                                    ▼
   this machine, Docker Desktop, compose project "omnichannel-prod"          proxy (Caddy)
                                                                         API site │ storage site
                                             ┌────────────────────────────────────┘        │
                                             ▼                                             ▼
                                            api ──> postgres <── workers (x2)        objectstorage
                                             │                     │   │              (SeaweedFS)
                                             └── LISTEN/NOTIFY     │   └── S3 API ─────────┘
                                                                rabbitmq
```

| Piece | Where | Data |
| --- | --- | --- |
| Frontend | Vercel project, production branch `production`, `VITE_API_BASE_URL` = the API origin | none |
| API origin | `https://<host>` — Funnel port 443 → proxy `127.0.0.1:8080` → `api:8080` (`/api/*`, `/health`) | — |
| Storage origin | `https://<host>:8443` — Funnel port 8443 → proxy `127.0.0.1:8081` → `objectstorage:8333` | — |
| PostgreSQL | volume `omnichannel-prod_postgres-data` | **source of truth** |
| Object storage | volume `omnichannel-prod_objectstorage-data` | product images, export files |
| RabbitMQ | volume `omnichannel-prod_rabbitmq-data` | messages in transit only |

Only the proxy has host ports, and only on `127.0.0.1`. TLS ends at Funnel; inside the machine everything is plain HTTP.
The channels, the invoice provider and the e-mail sender are **simulated** in production too (README sections 7 and 10):
no order leaves the system, no invoice is issued, no e-mail is sent.

## 2. Start, stop, status, logs

```powershell
docker compose @stack ps                          # every service "healthy" (workers has no health check: "Up")
docker compose @stack up -d --wait                # start (or apply a changed docker/.env.prod)
docker compose @stack stop                        # stop, keep containers and volumes
docker compose @stack logs --since 15m api        # api | workers | proxy | postgres | rabbitmq | objectstorage
docker compose @stack logs --since 15m proxy      # the access log of both sites (JSON; no query string, no credentials)
tailscale funnel status                           # the two public ports
Invoke-WebRequest https://<host>/health -UseBasicParsing | Select-Object StatusCode
```

- **Never** `docker compose @stack down -v` and never `docker volume rm omnichannel-prod_*`: that deletes the data.
  `down` without `-v` keeps the volumes but is not needed for anything in this runbook.
- API and Workers write compact JSON; every line carries `CorrelationId` — the same value is in the browser's request
  header `X-Correlation-Id`, in the task on "Sync & Processing" and in the proxy's access log.
- **Two expected warnings at every API start** (`Microsoft.AspNetCore.DataProtection`: keys stored in a directory that
  may not persist; keys not encrypted at rest). Nothing uses that key ring (review 025). They become relevant only when
  cookie authentication or Identity token providers are introduced.
- Funnel on / off:

```powershell
tailscale funnel --bg --https=443 http://127.0.0.1:8080
tailscale funnel --bg --https=8443 http://127.0.0.1:8081
tailscale funnel --https=443 off ; tailscale funnel --https=8443 off     # take the system off the internet
```

## 3. After a reboot

| Comes back | When |
| --- | --- |
| Tailscale and the two Funnel ports (`--bg` is stored in Tailscale's configuration) | by themselves; measured together with the stack ("Live values") |
| Docker Desktop | **after the user logs on** ("Start Docker Desktop when you sign in") |
| Every service of the stack (`restart: unless-stopped`) | with Docker Desktop, in dependency-free order; the API retries until PostgreSQL answers |
| The backup task | at its next time, or right after logon when a run was missed |

Nothing has to be typed. Until the logon the public names answer with an error from Funnel (nothing listens behind
them). Check afterwards: `docker compose @stack ps`, `tailscale funnel status`, `https://<host>/health`.
A service that was stopped by hand (`stop`) stays stopped: `docker compose @stack up -d --wait`.

## 4. Release (new version of the code)

Frontend and backend always run **the same commit**. `master` is where work lands; nothing a user sees changes until
this procedure runs.

```powershell
# 0. What is going out, and does it migrate?
git fetch origin ; git log --oneline production..origin/master
git diff --stat production origin/master -- backend/src/Omnichannel.Infrastructure/Persistence/Migrations

# 1. Backup of this moment (section 6). Stop here when it fails.
powershell -ExecutionPolicy Bypass -File scripts\deploy\backup-prod.ps1 -Destination <backup folder>

# 2. The code and the images. GIT_COMMIT labels the images; the next backup manifest names it.
git checkout master ; git pull --ff-only
$env:GIT_COMMIT = git rev-parse HEAD
docker compose @stack --profile migrate build

# 3. Migrations: look, then apply (section 5). Skip "run" when nothing is pending.
docker compose @stack --profile migrate run --rm migrate

# 4. The new containers, then health.
docker compose @stack up -d --wait
docker compose @stack ps
Invoke-WebRequest https://<host>/health -UseBasicParsing | Select-Object StatusCode

# 5. The same commit to the frontend. Vercel builds and deploys the branch "production".
git push origin "$($env:GIT_COMMIT):refs/heads/production"

# 6. Live check, read-only (values: section 13).
$env:SMOKE_BASE_URL = 'https://<host>' ; $env:SMOKE_FRONTEND_ORIGIN = 'https://<project>.vercel.app' ; $env:SMOKE_EXPECT_HSTS = '<true|false>'
powershell -ExecutionPolicy Bypass -File scripts\smoke\task-025b.ps1
```

- Between step 4 and the end of the Vercel build (about a minute) the old frontend talks to the new API. An API change
  that the old frontend cannot live with needs a maintenance moment: say so in the task that makes the change.
- `scripts\smoke\task-025.ps1` is **not** part of a release: it writes smoke products, orders and users. It ran once at
  go-live (decision in the log) and runs on production again only when a task says so.
- Updating a third-party image (`postgres:17`, `rabbitmq:4-management`, `caddy:2.11`, SeaweedFS) is its own deliberate
  release: backup, `docker compose @stack pull <service>`, `up -d --wait`, check. `up` alone never pulls a newer image.

### Rollback

- **Frontend only:** Vercel → project → Deployments → the previous production deployment → "Instant Rollback".
  The branch `production` is never moved back (the ruleset refuses a force push): revert the change on `master` and
  release that. Vercel may keep new deployments from becoming production until one is promoted after a rollback —
  check the deployment list after the next release.
- **Backend, no migration ran since the previous release:** `git checkout <previous commit>`, `$env:GIT_COMMIT = git
  rev-parse HEAD`, `build`, `up -d --wait`, then the frontend as above; `git checkout master` afterwards.
- **Backend, a migration ran:** the old code does not know the new schema and migrations are never taken back
  (`docs/tasks/README.md`). Fix forward with a new release; if the data itself is wrong, restore the backup of step 1
  (section 7, "in place of the live stack") — everything written since that backup is lost.

## 5. Migrations

```powershell
# What is applied (the helper sends the statement as a file: no quoting through the shells):
. scripts\deploy\_stack.ps1 ; Get-AppliedMigrations (Get-ServiceContainer 'omnichannel-prod' 'postgres')
# What the code of this checkout has:
Get-ChildItem backend\src\Omnichannel.Infrastructure\Persistence\Migrations\*_*.cs -Exclude *.Designer.cs | Select-Object Name
```

Rules (unchanged, `docs/tasks/README.md`): the pending list must be exactly what the release brings; **additive only**
— a migration that drops or renames a table or column, narrows a type or rewrites rows is not applied without a
decision of the user and a backup; never `database update <earlier migration>`. The `migrate` service applies what is
pending as the role `omnichannel_migrator` and exits; the application role cannot change the schema.

## 6. Backup

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\backup-prod.ps1 -Destination <backup folder>
```

Writes `<backup folder>\omnichannel-backup-<yyyyMMdd-HHmmss>\` with `postgres.dump` (`pg_dump -Fc`, taken inside the
container as the superuser over the local socket), `objectstorage.tar` (the storage volume) and `manifest.json` (time,
commit of the running images, applied migrations, size and SHA-256 of both files), and one line in
`<backup folder>\backup.log`. Exit code 0 only when everything was written; a failed run leaves no folder.

- **The storage service is stopped while its volume is archived** (measured: see "Live values"; about a quarter of a
  minute). An image upload or an export download in that moment fails visibly in the browser and can be repeated; an
  export that a Workers instance stores in that moment fails its attempt and is retried by the normal retry rules.
- The backup contains customers, orders and password hashes. The destination is the user's private storage; it should
  leave the machine by itself (a folder that a cloud drive synchronises). The default is
  `%USERPROFILE%\OmnichannelBackups`.
- It contains **no password or key of the stack** and never the env file. Keep a copy of `docker/.env.prod` in the
  password manager if the same secrets must survive the machine (not needed for a restore).
- Retention after every successful run: every backup of the 7 most recent days that have one, and the newest backup
  of each of the 4 most recent Sundays; older ones are removed. Only folders the script created are ever removed.
- RabbitMQ is not backed up (section 8).
- **Daily task:** `powershell -ExecutionPolicy Bypass -File scripts\deploy\register-backup-task.ps1 -At 03:00
  -Destination <backup folder>` registers "Omnichannel backup (omnichannel-prod)" for the current user: daily, only
  while logged on, a missed run is made up at the next logon. `-Unregister` removes it. Look at `backup.log` from time
  to time: a line `FAILED ...` (Docker Desktop not running, disk full) means there is no backup of that day.

## 7. Restore

`restore-prod.ps1` restores **only into a stack that does not exist** (no container, no volume of that project name).

### Restore drill (do it after every change to the backup scripts, and a few times a year)

```powershell
# A throwaway stack beside production: own name, own ports, own network, own env file.
powershell -ExecutionPolicy Bypass -File scripts\deploy\new-prod-env.ps1 -AdminEmail drill@example.com -Path docker\.env.prod.drill `
    -FrontendOrigin https://<project>.vercel.app -StoragePublicUrl http://localhost:18181
# In docker\.env.prod.drill set: API_PORT=18180, STORAGE_PORT=18181, API_HTTPS_REDIRECT=false,
#   EDGE_SUBNET=172.30.252.0/28, EDGE_GATEWAY=172.30.252.1, EDGE_DYNAMIC_RANGE=172.30.252.8/29, PROXY_IP=172.30.252.2
powershell -ExecutionPolicy Bypass -File scripts\deploy\restore-prod.ps1 -BackupPath <backup folder>\omnichannel-backup-<...> `
    -ProjectName omni-drill -EnvFile docker\.env.prod.drill
# Look: http://localhost:18180/health, log in with a production account, compare a few numbers. Then remove the drill:
docker compose -p omni-drill --env-file docker\.env.prod.drill -f docker/docker-compose.prod.yml --profile migrate down -v
Remove-Item docker\.env.prod.drill
```

The `down -v` names the drill project explicitly. Read the project name twice before pressing Enter.

### After the machine is lost (new machine, no stack)

Install Docker Desktop and Tailscale (same tailnet, same machine name → same public name), clone the repository, put
`docker/.env.prod` back from the password manager **or** generate a new one (`new-prod-env.ps1` with the same
`-FrontendOrigin` / `-StoragePublicUrl`), build the images (`$env:GIT_COMMIT = <commit of the manifest>`), then
`restore-prod.ps1 -BackupPath ... -ProjectName omnichannel-prod -EnvFile docker\.env.prod`, Funnel on (section 2),
backup task (section 6). With a **new** env file: every session ends (new JWT key), webhook senders need the new
secrets, the users keep their passwords (they are in the database).

### In place of the live stack (decision of the user, never a script)

1. Backup of the present state, even if it is the broken one. 2. Funnel off. 3. `docker compose @stack stop`.
4. Restore the wanted backup into a second project (`omnichannel-prod2`, the drill's ports and network) and check it.
5. Only then decide: either point Funnel at the second project's ports (it becomes production; the compose `name:`
   and this runbook change with it), or remove the old volumes by hand and restore into `omnichannel-prod`.
Removing a production volume is the one command in this runbook that cannot be undone: it is typed by the user.

## 8. After a restore: tasks whose messages are gone

A backup holds PostgreSQL and the storage, not RabbitMQ. What that means:

- Outbox events that were not yet published at backup time are published by the Outbox Publisher after the restore —
  nothing to do.
- A task that was **already published but not finished** at backup time (`Pending`, `Processing` or waiting for a
  retry) has no message any more. It stays in that status and nothing picks it up: open **Sync & Processing**,
  filter by those statuses, and use **Retry** on each (Manager / Admin) once it shows as failed — a task that never
  fails by itself is the known gap "stale non-terminal tasks" (roadmap 026). Until 026 the reliable way to find them
  is the filter plus the creation time (older than the restore).
- Everything written after the backup is lost: orders, stock movements, uploads. Channels that sent webhooks in that
  window must send them again (the same external order id is accepted once — idempotency).
- Export files whose rows are newer than the backup do not exist; export again.

## 9. Secrets

All in `docker/.env.prod`; a change takes effect with `docker compose @stack up -d --wait` (compose recreates the
services whose environment changed). **Backup first** for anything that touches PostgreSQL or the storage.

| Secret | Procedure | Effect |
| --- | --- | --- |
| `JWT_SIGNING_KEY` | new value (64 letters / digits) in the file, `up -d --wait` | **every session ends**; all users log in again |
| `WEBHOOK_SECRET_<CHANNEL>` | without downtime: give the API the new secret as a second one first — a local, uncommitted override file with `services: api: environment: Webhooks__Channels__<slug>__Secrets__1: <new>`, added with a second `-f`; `up -d --wait`; switch the sender; then put the new value into the env file, drop the override, `up -d --wait` (README section 9 "Rotation") | none when done in this order |
| `OMNI_APP_PASSWORD` | 1. `docker compose @stack exec postgres psql -U postgres -c '\password omnichannel_app'` (typed at the prompt, on no command line) 2. the same value in the file 3. `up -d --wait` | API and Workers fail database logins between 1 and 3: seconds |
| `OMNI_MIGRATOR_PASSWORD` | the same with `omnichannel_migrator`; no service uses it between releases | none |
| `POSTGRES_PASSWORD` | `\password postgres` the same way, then the file (the variable is only read when the volume is created) | none |
| `RABBITMQ_PASSWORD` | 1. the new value in the file 2. `up -d --wait rabbitmq` (the container now carries the new value; the broker still has the old one - the default user is only created with the volume) 3. `docker compose @stack exec rabbitmq sh -c 'rabbitmqctl change_password "$RABBITMQ_DEFAULT_USER" "$RABBITMQ_DEFAULT_PASS"'` (the value comes from the container's environment, not from the command line) 4. `up -d --wait` | Workers cannot connect between 2 and 4; messages stay; the outbox keeps what is not published |
| `OBJECT_STORAGE_ACCESS_KEY_ID` / `_SECRET_ACCESS_KEY` | new values (20 / 40 letters / digits) in the file, `up -d --wait` (storage, API and Workers restart together) | links handed out before stop working (they live 5 to 15 minutes anyway) |
| `BOOTSTRAP_ADMIN_PASSWORD` | **blank after the first login** (it is only used to create the first Admin): empty value in the file, `up -d --wait api`. `docker inspect` then shows it empty. | none; the admin keeps the password |

Exercised at go-live: the `BOOTSTRAP_ADMIN_PASSWORD` row. The other rows are procedures by design that have not been
run against a live stack yet: do each one first on a drill stack (section 7).

Values are letters and digits only (they are substituted into the compose file and into connection strings). Never
`new-prod-env.ps1 -Force` over `docker/.env.prod`: it would replace passwords that the volumes keep.
A forgotten password of the only Admin: set `BOOTSTRAP_ADMIN_EMAIL` to a **new** address and `BOOTSTRAP_ADMIN_PASSWORD`
to a new value, `up -d --wait api` (the seeder creates that second Admin), reset the first one under Users, lock the
helper account, blank the variable again.

## 10. Client addresses, rate limits, trust

Measured at go-live ("Live values"): Funnel reports the client's public address and `https`, so the proxy trusts the
edge gateway and the API sees every client's own address.
In every case: the API believes forwarded headers from the proxy only, the proxy only from `PROXY_TRUSTED_PROXIES`.
Trusting the edge gateway (`172.30.250.1/32`) also trusts **every process on this machine** — Docker Desktop delivers
all host-local connections from that address. Accepted on a single-user machine.

## 11. Incidents

| Symptom | Likely cause | Do |
| --- | --- | --- |
| The frontend loads, every request fails ("cannot reach the server") | machine asleep or off; nobody logged on after a reboot; Docker Desktop not started; Funnel off | wake / log on; Docker Desktop; `docker compose @stack ps`; `tailscale funnel status`; section 3 |
| `https://<host>/health` fails from outside, works on `http://127.0.0.1:8080/health` | Funnel or Tailscale | `tailscale status`; Funnel on again (section 2); the node attribute `funnel` in the tailnet policy; Tailscale's status page |
| `127.0.0.1:8080/health` fails | proxy or API down | `docker compose @stack ps`, `logs --since 15m api proxy`; `up -d --wait` |
| Login answers 429 | more than 10 login attempts a minute from that client address (section 10) | wait a minute; the sender is in the proxy's access log (`client_ip`); 026 adds throttling per account |
| Uploads fail, pages work | storage stopped (a backup is running: a quarter of a minute), storage unhealthy, or disk full | `ps`, `logs objectstorage`; free disk space |
| Disk full | Docker's virtual disk (images, build cache), logs (bounded: 5 x 10 MB per container), backups | `docker system df`; `docker builder prune` (build cache only — **never** `docker system prune --volumes`); old backups are removed by the retention |
| A dead-letter queue fills up / tasks `Failed` | a real failure of a worker or of the storage / database | Alerts and **Sync & Processing** show the task and its attempts (`ErrorCode`, `CorrelationId`); `logs workers`; fix the cause, then **Retry** |
| Tasks stay `Pending` | Workers down, or RabbitMQ down | `ps`; `logs workers rabbitmq`; `up -d --wait` |
| The API restarts in a loop | wrong or missing value in the env file (the message names the setting, never the value) | `logs api`; correct the file; `up -d --wait` |
| The dashboard shows "reconnecting" forever | the hub cannot connect through Funnel | reload; `logs proxy` for `/api/hubs/notifications`; the data is still correct after a refresh (SignalR is only a hint) |

## 12. Known limitations

- **Uptime is this machine's**: asleep, off, logged out or without network means the backend is down. No monitoring
  beyond `/health`; nobody is told when it goes down (an uptime monitor is a follow-up: it needs an account).
- **Funnel**: a beta feature of Tailscale with unpublished bandwidth limits; the public name contains the machine and
  tailnet names and appears in certificate transparency logs.
- **Simulated providers**: channels, invoices and e-mails (section 1).
- **Backups** are as far from the machine as the chosen destination is.
- Security headers / CSP on the frontend, body limits, container hardening, login throttling per account: roadmap 026.
- TLS mode of the proxy (public host names, `docker-compose.prod.tls.yml`) is validated as configuration only.

## 13. Live values

Go-live of task 025b (2026-10-12); public information only.

| What | Value |
| --- | --- |
| API origin | `https://omni.tail70815b.ts.net` |
| Storage origin | `https://omni.tail70815b.ts.net:8443` |
| Frontend origin | `https://omnichannel-management-system-front.vercel.app` |
| Repository | `HuyVuCS/omnichannel-management-system` (public) |
| `PROXY_TRUSTED_PROXIES` / `API_HTTPS_REDIRECT` | `172.30.250.1/32` / `true` |
| Client address available to the API | yes - Funnel sends the public address in `X-Forwarded-For` |
| `SMOKE_EXPECT_HSTS` for `task-025b.ps1` | `true` |
| Hub transport through Funnel | WebSockets (`GET /api/hubs/notifications` -> 101 on the Funnel path and on the tailnet path) |
| The API host without TLS (`http://`) | not served |
| Storage downtime during a backup | about 14 s (measured on a throwaway stack) |
| After a reboot (2026-10-12) | nothing was started by hand: all containers running 62 s after the boot (logon included), both Funnel ports on, first login from the internet 112 s after the boot. Docker Desktop - and with it the stack - starts with the logon; whether Tailscale alone serves before a logon was not measured |
| Backups | **none yet** (skipped by the user at go-live): no backup task is registered and no backup of production exists - section 6 before the first release |
| Backup destination | chosen by the user (not written here) |

What reaches the proxy (its access log), measured at go-live:

| Path | Port | `remote_ip` | `Host` | `X-Forwarded-For` | `X-Forwarded-Proto` | `X-Forwarded-Host` | Also |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Funnel (from the internet) | 443 | `172.30.250.1` | `omni.tail70815b.ts.net` | the client's public address | `https` | as `Host` | `Tailscale-Funnel-Request` |
| Funnel (from the internet) | 8443 | `172.30.250.1` | `omni.tail70815b.ts.net:8443` | the client's public address | `https` | as `Host` | `Tailscale-Funnel-Request` |
| Tailnet (this machine, or a device of the tailnet) | 443 / 8443 | `172.30.250.1` | as above | the device's tailnet address (`100.x`) | `https` | as `Host` | `Tailscale-User-Login`, `-Name`, `-Profile-Pic` |

- Funnel keeps the `Host` header on both ports, so presigned links (signed for `host:8443`) work unchanged: the
  Caddyfile needed no fix.
- This is the first case of review 025, question 1: the proxy believes the edge gateway, the API keeps redirecting
  plain HTTP and answers `Strict-Transport-Security`; rate limits and logs see the client's address.
- A request from a device of the tailnet carries the **Tailscale login of its user** in headers; the proxy passes them
  to the API (which ignores them) and writes them to its access log. The log stays on this machine (5 x 10 MB).

## 14. Moving to an Oracle Always Free VM (task 025c, not done)

Only when an always-on host is wanted. What is known, what is open:

- **Shape:** Ampere A1 (arm64), 2 OCPU / 12 GB in the Always Free allowance at the time of the hosting decision; a
  card is needed at signup; capacity is often exhausted in popular regions. **Idle reclamation:** an instance whose
  CPU, network and memory stay under 20 % for 7 days may be reclaimed — this stack idles far below that, so the rule
  must be read again and answered (a paid-as-you-go account without usage, or accept the risk) before relying on it.
- **Build:** natively on the VM (`docker compose ... build`); the images are verified for `linux/arm64` (task 025,
  and the manual `arm64` CI run of 025b). Docker Engine, not Docker Desktop: the stack starts at boot without a logon.
- **Public names — undecided:** either Tailscale Funnel on the VM (same procedure as here, same kind of name), or the
  proxy's TLS mode with two real host names (`API_SITE_ADDRESS`, `STORAGE_SITE_ADDRESS`,
  `-f docker/docker-compose.prod.tls.yml`, ports 80 / 443 open in the VM's security list **and** its host firewall) —
  that needs a domain. TLS mode has never been exercised.
- **What to copy:** the newest backup folder and, if the secrets should stay, `docker/.env.prod` (over a private
  channel). Then "After the machine is lost" in section 7. A new public name means: new `STORAGE_PUBLIC_URL`, new
  `VITE_API_BASE_URL` in Vercel and a release.
- **Backups** then leave the VM by another way than a synced folder (object storage bucket of the same account, or
  pulled by this machine) — to be decided in 025c. `backup-prod.ps1` runs in PowerShell 7 on Linux only after a check
  (paths, `%USERPROFILE%`); a scheduled task becomes a systemd timer.

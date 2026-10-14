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

**Production has no backup - decision of the user (2026-10-12).** What that means: losing Docker Desktop's data
(reset, purge, uninstall, a broken virtual disk) or the disk, a release that goes wrong, or one wrong command
(`down -v`, `docker volume rm`) loses the data for good, and after a migration there is only the way forward (a fix in
a new release - nothing to go back to). No procedure below takes a backup first. `backup-prod.ps1` and
`restore-prod.ps1` stay in the repository, work, and are run against production only when the user asks (sections 6
and 7).

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
| Frontend | Vercel project, production branch `production`, `VITE_API_BASE_URL` = the API origin, `VITE_STORAGE_ORIGIN` = the storage origin (both go into the page's Content Security Policy) | none |
| Images of the stack | `omnichannel-api`, `-workers`, `-migrate` with the tag `IMAGE_TAG` of `docker/.env.prod` = the short commit of the release (section 4) | — |
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
- **Three expected warnings at every API start** (`Microsoft.AspNetCore.DataProtection`: an in-memory repository, an
  ephemeral key repository, no XML encryptor - the root file system of the container is read-only since task 026, so
  the key ring lives in memory). Nothing uses that key ring (review 025). They become relevant only when cookie
  authentication or Identity token providers are introduced (then: a volume for the keys).
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

# 1. The code, and the tag of this release: the short commit, written into docker/.env.prod as IMAGE_TAG. The script
#    prints "IMAGE_TAG: <previous> -> <new>": the previous tag is what a rollback puts back - note it.
git checkout master ; git pull --ff-only
$env:GIT_COMMIT = git rev-parse HEAD                      # label of the images (org.opencontainers.image.revision)
powershell -ExecutionPolicy Bypass -File scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod

# 2. The images omnichannel-api / -workers / -migrate:<new tag>. The running containers keep their images.
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
$env:SMOKE_BASE_URL = 'https://<host>' ; $env:SMOKE_STORAGE_ORIGIN = 'https://<host>:8443'
$env:SMOKE_FRONTEND_ORIGIN = 'https://<project>.vercel.app' ; $env:SMOKE_EXPECT_HSTS = '<true|false>'
powershell -ExecutionPolicy Bypass -File scripts\smoke\task-025b.ps1

# 7. Old images. Keep the tag of this release and the one before it (the rollback); remove older ones by name.
docker image ls "omnichannel-*"
docker image rm omnichannel-api:<old tag> omnichannel-workers:<old tag> omnichannel-migrate:<old tag>
```

- There is no backup step (top of this runbook). Look at step 0 before anything else: a release with a migration
  cannot be taken back.
- `IMAGE_TAG` is a line of the env file and never a shell variable: `up` always starts the images the file names, and
  a throwaway stack with its own env file (its own tag, `local-...` or `drill-...`) can be built next to production
  without changing what production runs. Never `docker image prune -a` and never `docker image rm` of the running or
  the previous tag while a rollback might be needed; `docker builder prune` (build cache) is always safe.

- **A changed `docker/proxy/Caddyfile` needs `docker compose @stack restart proxy` after step 4**: the file is mounted,
  the service definition does not change, and `up` keeps the running proxy with the old configuration.
- **The downtime starts at step 3, not at step 4, when the definition of a service changed** (its image digest,
  environment, command, limits): `compose run migrate` first brings up what `migrate` depends on, and that recreates
  `postgres` when its definition differs from the running container (seen in the release of 026: the API was without
  its database from then until step 4). A release that touches a third-party service is a maintenance moment from
  step 3 on; `docker compose @stack config --images` and the diff of `docker-compose.prod.yml` say beforehand.
- Between step 4 and the end of the Vercel build (about a minute) the old frontend talks to the new API. An API change
  that the old frontend cannot live with needs a maintenance moment: say so in the task that makes the change.
- `scripts\smoke\task-025.ps1` is **not** part of a release: it writes smoke products, orders and users. It ran once at
  go-live (decision in the log) and runs on production again only when a task says so.
- **Updating a third-party image** (`postgres:17`, `rabbitmq:4-management`, `caddy:2.11`,
  `chrislusf/seaweedfs:4.48`). The four are pinned by digest in `docker/docker-compose.prod.yml`
  (`image: tag@sha256:...`; the tool image of `scripts/deploy/_stack.ps1` is the same `postgres` digest): the stack
  runs exactly those builds, whatever the tag points to today, and a drill runs the same ones. An update is its own
  deliberate release: on a throwaway stack first, put the new digest into the compose file (and `_stack.ps1` for
  `postgres`) - `docker buildx imagetools inspect <image>:<tag>` shows the digest of a tag without fetching it into
  the local store -, start the stack, run the smoke scripts, check section 15 (a new image can need other capabilities
  or another user); then commit and release (`up -d --wait` fetches the image by its digest and recreates only that
  service). **Never `docker pull <image>:<tag>` and never `docker compose pull` on this machine**: nothing would
  change for the stack, but the local tag would move away from what is pinned and the digest list of this file
  would no longer describe the local store. There is no backup: a broker or database upgrade cannot be taken back.

### Rollback

- **Frontend only:** Vercel → project → Deployments → the previous production deployment → "Instant Rollback".
  The branch `production` is never moved back (the ruleset refuses a force push): revert the change on `master` and
  release that. Vercel may keep new deployments from becoming production until one is promoted after a rollback —
  check the deployment list after the next release.
- **Backend, no migration ran since the previous release:** put the previous tag back and start it - no rebuild, the
  images are still there (`docker image ls "omnichannel-*"` shows the tags present):
  `powershell -ExecutionPolicy Bypass -File scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod -Tag <previous tag>`,
  `docker compose @stack up -d --wait`, then the frontend as above. If the images of that tag were removed:
  `git checkout <previous commit>`, `set-image-tag.ps1` without `-Tag`, `build`, `up -d --wait`, `git checkout master`.
  The compose file and the Caddyfile of the checkout are used either way: a release that changed them (as 026 did)
  is rolled back from the checkout of the previous commit.
- **Backend, a migration ran:** the old code does not know the new schema and migrations are never taken back
  (`docs/tasks/README.md`). Fix forward with a new release. There is no backup to restore: data that a release
  damaged stays damaged.

## 5. Migrations

```powershell
# What is applied (the helper sends the statement as a file: no quoting through the shells):
. scripts\deploy\_stack.ps1 ; Get-AppliedMigrations (Get-ServiceContainer 'omnichannel-prod' 'postgres')
# What the code of this checkout has:
Get-ChildItem backend\src\Omnichannel.Infrastructure\Persistence\Migrations\*_*.cs -Exclude *.Designer.cs | Select-Object Name
```

Rules (unchanged, `docs/tasks/README.md`): the pending list must be exactly what the release brings; **additive only**
— a migration that drops or renames a table or column, narrows a type or rewrites rows is never applied to
production (there is no backup; the user decides what happens instead); never `database update <earlier migration>`. The `migrate` service applies what is
pending as the role `omnichannel_migrator` and exits; the application role cannot change the schema.

## 6. Backup (available, not in use)

**Production is not backed up** (top of this runbook): no task is registered and the script has never run against
`omnichannel-prod`. It works - exercised on throwaway stacks in tasks 025b, 025d and 026 (with the hardened
containers) - and this section describes it for the day the decision changes. The scheduled start (the daily task)
was never exercised.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\backup-prod.ps1 -Destination <backup folder>
```

Writes `<backup folder>\omnichannel-backup-<yyyyMMdd-HHmmss>\` with `postgres.dump` (`pg_dump -Fc`, taken inside the
container as the superuser over the local socket), `objectstorage.tar` (the storage volume) and `manifest.json` (time,
commit of the running images, applied migrations, size and SHA-256 of both files, the row counts of the main tables
and the number of entries of the storage archive), and one line in `<backup folder>\backup.log`. Exit code 0 only when everything was written; a failed run leaves no folder.

- **The storage service is stopped while its volume is archived** (measured: see "Live values"; about a quarter of a
  minute). An image upload or an export download in that moment fails visibly in the browser and can be repeated; an
  export that a Workers instance stores in that moment fails its attempt and is retried by the normal retry rules.
- **The dump and the storage archive are taken seconds apart**, and the row counts of the manifest right after the
  dump. An image confirmed in between is an orphan object after a restore; an image removed in between is missing for
  its product; a count can be ahead of the dump. Accepted: seconds, at night.
- The backup contains customers, orders and password hashes and is **not encrypted** (decision in the log): the access
  control of the destination is its only protection. The destination is the user's private storage, never a shared
  folder; it should leave the machine by itself (a folder that a cloud drive synchronises). The default is
  `%USERPROFILE%\OmnichannelBackups`.
- It contains **no password or key of the stack** and never the env file. Keep a copy of `docker/.env.prod` in the
  password manager if the same secrets must survive the machine (not needed for a restore).
- Retention after every successful run: every backup of the 7 most recent days that have one, and the newest backup
  of each of the 4 most recent Sundays; older ones are removed. Only folders the script created are ever removed.
- RabbitMQ is not backed up (section 8).
- **Daily task:** `powershell -ExecutionPolicy Bypass -File scripts\deploy\register-backup-task.ps1 -At 03:00
  -Destination <backup folder>` registers "Omnichannel backup (omnichannel-prod)" for the current user: daily, only
  while logged on, a missed run is made up at the next logon. `-Unregister` removes it.
- **The weekly look** (nobody is told when a backup fails): 1. the last lines of `<backup folder>\backup.log` - one
  `OK ...` line per day; a line `FAILED ...` (Docker Desktop not running, disk full) means there is no backup of that
  day. 2. Task Scheduler (`taskschd.msc`) -> Task Scheduler Library -> "Omnichannel backup (omnichannel-prod)": "Last
  Run Result" must be `(0x0)` and "Last Run Time" last night - or
  `Get-ScheduledTaskInfo -TaskName 'Omnichannel backup (omnichannel-prod)'` (`LastTaskResult` 0). A result other than
  0 with no new line in `backup.log` means the script did not even start.

## 7. Restore (available, not in use)

Without a backup of production (top of this runbook) there is nothing to restore: this section applies to drills and
to backups the user asks for. After the machine is lost the system starts again **empty** (README "Production
deployment", steps 1 and 2).

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
# Read the table the script prints: every table and the storage entries must show the same number under "manifest" and
# "restored" (a difference is a warning, marked "<-- differs"; while nobody used the system during the backup there
# must be none). Then http://localhost:18180/health and a login with a production account. Then remove the drill:
docker compose -p omni-drill --env-file docker\.env.prod.drill -f docker/docker-compose.prod.yml --profile migrate down -v
Remove-Item docker\.env.prod.drill
```

The `down -v` names the drill project explicitly. Read the project name twice before pressing Enter.

### After the machine is lost (new machine, no stack)

Install Docker Desktop and Tailscale (same tailnet, same machine name → same public name), clone the repository, put
`docker/.env.prod` back from the password manager **or** generate a new one (`new-prod-env.ps1` with the same
`-FrontendOrigin` / `-StoragePublicUrl`), check out the commit of the manifest and build the images
(`$env:GIT_COMMIT = git rev-parse HEAD`, `set-image-tag.ps1 -EnvFile docker\.env.prod`, `build`), then
`restore-prod.ps1 -BackupPath ... -ProjectName omnichannel-prod -EnvFile docker\.env.prod`, Funnel on (section 2),
backup task (section 6). With a **new** env file: every session ends (new JWT key), webhook senders need the new
secrets, the users keep their passwords (they are in the database).

### In place of the live stack (decision of the user, never a script)

1. A backup of the present state, even if it is the broken one (the one place where this runbook asks for a backup:
   the present state is about to be replaced). 2. Funnel off. 3. `docker compose @stack stop`.
4. Restore the wanted backup into a second project (`omnichannel-prod2`, the drill's ports and network) and check it.
5. Only then decide: either point Funnel at the second project's ports (it becomes production; the compose `name:`
   and this runbook change with it), or remove the old volumes by hand and restore into `omnichannel-prod`.
Removing a production volume is the one command in this runbook that cannot be undone: it is typed by the user.

## 8. Tasks whose messages are gone (RabbitMQ lost, a restore, a message that could not be published)

RabbitMQ holds messages in transit only, and no backup contains it. A task whose message is gone - the broker's
volume was lost, the database was restored from a backup, an outbox row ended `Failed` after the publisher's
attempts, a message was removed by hand - stays `Pending`, `InProgress` or `RetryScheduled`: nothing will ever pick
it up, it cannot be retried in that status, and reconciliation waits for it.

**Such a task fails by itself** (stale-task sweep of the Workers host, task 026b):

- Every 5 minutes (`StaleTasks:Interval`) one Workers instance fails every unfinished task that nothing changed for
  30 minutes (`StaleTasks:StaleAfter`) - at most 200 per run (`StaleTasks:MaxPerRun`), the longest standing first.
  So a lost message shows **within 35 minutes** (`StaleAfter + Interval`; after a start of the Workers the first run
  is one interval later).
- The task becomes `Failed` with the error code **`stale_no_progress`** (attempt of the worker `StaleTaskSweep`), as
  any final failure: an open alert on **Alerts**, the event "Task failed" on **Logs / audit**, and one event "Stale
  tasks failed" per run with the number and the first 50 task ids.
- **What to do:** **Sync & Processing** -> status *Failed*, error code `stale_no_progress` -> **Retry** on each
  (Manager / Admin). A retry sends a new message; should the old one turn up later (a retry queue, a broker that came
  back), it is acknowledged and dropped - the task never runs twice.
- A stock-sync task needs no retry: once it is failed, the next reconciliation (every 15 minutes) pushes the stock of
  that product and channel again when it still differs.

After **RabbitMQ lost its volume**: `docker compose @stack up -d --wait` (the Workers declare exchanges and queues
again; messages that were not yet published are published), wait 35 minutes, then retry what failed as above.

Things to know:

- **After a long time without Workers** (longer than `StaleAfter`) the queues may still hold the messages of tasks
  that look stale. The consumers get one interval (5 minutes) to start them - a started task is not stale - before
  the first run; a task they did not reach by then is failed although its message is still waiting. Retry it: the
  waiting message is dropped when it arrives. Nothing is processed twice and nothing is lost, but it is a retry by
  hand. With a large backlog, start the Workers once with the sweep off - `StaleTasks__Enabled: "false"` under
  `environment:` of the `workers` service in the compose file, `up -d --wait`, and take the line out again (another
  `up -d --wait`) when the queues are empty.
- `StaleAfter` must be at least twice the longest time a healthy task waits untouched: the longest retry delay
  (`RabbitMq:RetryDelays`, 10 minutes) and the time the Outbox Publisher keeps trying one message (about 9 minutes
  with the defaults). **The Workers do not start with a shorter value** (the log names the setting).
- Switched off nothing fails by itself: find such tasks with the status filters and the
  creation time, as before 026b - they cannot be retried until they are `Failed`.

After a **restore** (section 7) also:

- Outbox events that were not yet published at backup time are published by the Outbox Publisher after the restore -
  nothing to do.
- Everything written after the backup is lost: orders, stock movements, uploads. Channels that sent webhooks in that
  window must send them again (the same external order id is accepted once - idempotency).
- Export files whose rows are newer than the backup do not exist; export again.

## 9. Secrets

All in `docker/.env.prod`; a change takes effect with `docker compose @stack up -d --wait` (compose recreates the
services whose environment changed). No backup is taken first (top of this runbook): do a row on a drill stack
before doing it on production for the first time.

`rotate-secret.ps1` makes the new value (random letters and digits, the lengths of `new-prod-env.ps1`) and writes it
where the row says; it prints no value and puts none on a command line. In the table, `-Name ...` stands for

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\rotate-secret.ps1 -EnvFile docker\.env.prod -Name ...
```

| Secret | Procedure | Effect (the containers `up` recreates; times measured on the drill stack) |
| --- | --- | --- |
| `JWT_SIGNING_KEY` | `-Name JWT_SIGNING_KEY`, then `up -d --wait` | `api` (8 s). **Every session ends**; all users log in again |
| `WEBHOOK_SECRET_<CHANNEL>` | without a refused webhook: 1. `-Name WEBHOOK_SECRET_<CHANNEL> -OverlapFile <new file outside the repository>` writes a compose override that gives the API the new secret as a **second** one 2. `docker compose @stack -f <that file> up -d --wait` - both secrets are accepted 3. switch the sender (the new secret is in that file) 4. `-Name WEBHOOK_SECRET_<CHANNEL> -CompleteFrom <that file>` moves it into the env file and removes the override 5. `up -d --wait` - the old secret is refused (401) | `api` twice (8 s each); no webhook is refused when done in this order |
| `OMNI_APP_PASSWORD` | 1. `-Name OMNI_APP_PASSWORD` changes the role in the running database (below) and writes the file 2. `up -d --wait` | `postgres`, `api`, `workers` (13 s). Between 1 and 2 API and Workers keep their open connections; new ones are refused |
| `OMNI_MIGRATOR_PASSWORD` | `-Name OMNI_MIGRATOR_PASSWORD`, then `up -d --wait` (no service uses the role between releases, but the variable is part of the `postgres` service) | `postgres` (seconds; API and Workers reconnect by themselves). Without the `up`, the next `run --rm migrate` or release recreates it |
| `POSTGRES_PASSWORD` | `-Name POSTGRES_PASSWORD`, then `up -d --wait` (the variable is only read when the volume is created; backups use the local socket and need no password) | `postgres` (seconds), as above |
| `RABBITMQ_PASSWORD` | 1. `-Name RABBITMQ_PASSWORD` 2. `up -d --wait rabbitmq` (the container now carries the new value; the broker still has the old one - the default user is only created with the volume) 3. `docker compose @stack exec rabbitmq sh -c 'rabbitmqctl change_password $RABBITMQ_DEFAULT_USER $RABBITMQ_DEFAULT_PASS'` (single quotes: the shell **inside** the container fills in the values) 4. `up -d --wait` | `rabbitmq` at 2 (13 s), `workers` at 4. Workers cannot connect between 2 and 4; messages stay; the outbox keeps what is not published |
| `OBJECT_STORAGE_ACCESS_KEY_ID` / `_SECRET_ACCESS_KEY` | `-Name OBJECT_STORAGE_ACCESS_KEY_ID`, `-Name OBJECT_STORAGE_SECRET_ACCESS_KEY`, then one `up -d --wait` | `objectstorage`, `api`, `workers` (20 s). Links handed out before stop working (403; they live 5 to 15 minutes anyway) |
| `BOOTSTRAP_ADMIN_PASSWORD` | **blank after the first login** (it is only used to create the first Admin): empty value in the file, `up -d --wait api`. `docker inspect` then shows it empty. | none; the admin keeps the password |

**Database passwords without a prompt.** For the three database rows the script computes the SCRAM-SHA-256 verifier
of the new password itself and sends only that, as the superuser over the local socket of the container, in a session
with statement logging switched off (`log_min_error_statement = panic`): the password never reaches PostgreSQL in
clear, and a statement that fails leaves neither the password, the verifier nor the statement in
`docker compose logs postgres` - only the error line (verified in the drill). By hand the same is
`docker compose @stack exec postgres psql -U postgres -c '\password omnichannel_app'` (typed twice at the prompt, on no
command line), then the value into the file.

Exercised: the `BOOTSTRAP_ADMIN_PASSWORD` row on production at go-live. **Every other row on a drill stack in task
025d** (`omni025d-rot`, two Workers instances, each row followed by its check: old token 401 and a fresh login; old and
new webhook secret 202 during the overlap, old 401 after it; an order `Reserved` and three clients of the app role
logged in again; `migrate` "already up to date"; a backup; both Workers connected and the outbox drained; an old
upload link 403 and a new upload confirmed; the smoke script green before and after). **No secret of production has
ever been rotated.** Do a row on a drill stack again (section 7) when its procedure or the compose file changed.

Values are letters and digits only (they are substituted into the compose file and into connection strings). Never
`new-prod-env.ps1 -Force` over `docker/.env.prod`: it would replace passwords that the volumes keep.
A forgotten password of the only Admin: set `BOOTSTRAP_ADMIN_EMAIL` to a **new** address and `BOOTSTRAP_ADMIN_PASSWORD`
to a new value, `up -d --wait api` (the seeder creates that second Admin), reset the first one under Users, lock the
helper account, blank the variable again.

## 10. Client addresses, rate limits, trust

**Login throttling (decision of task 026: nothing was added).** Two limits exist: 10 login attempts a minute per
client address (429), and the lockout of an account after 5 wrong passwords for 15 minutes (ASP.NET Core Identity).
The lockout is the throttling per account - and its price: **anyone who knows an e-mail address can keep that account
locked, 15 minutes at a time**, without knowing the password. What to do: an Admin unlocks the account under
**Users**; when the locked account is the only Admin, the second Admin does it - keep one (the procedure of section 9,
"A forgotten password of the only Admin", creates it). The sender of the attempts is in the proxy's access log
(`client_ip` of `POST /api/auth/login`).


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
| `docker ps` answers "cannot connect" / Docker Desktop says it "is unable to start", usually after a Windows update | Windows upgraded WSL underneath Docker Desktop, which did not come back by itself (2026-10-13: production was down about 40 minutes until somebody looked; `%LOCALAPPDATA%\Docker\log\host\com.docker.backend.exe.log` said "WSL is finishing an upgrade" / "WSL update required") | `wsl --status` (and `wsl --version`) until WSL answers without an update notice; then `docker desktop restart`. The engine and **every container of the stack come back by themselves** (`restart: unless-stopped`; nothing is reset, the volumes are untouched) - check `docker compose @stack ps` and `/health`. The same after a deliberate `wsl --shutdown` (start Docker Desktop again; about a minute of downtime). Tasks caught in between: section 8 |
| A throwaway stack or a test cannot bind its port ("access permissions"), production is fine | after a restart of WSL / Hyper-V Windows reserves other port ranges (2026-10-13: 55199-55858) | `netsh interface ipv4 show excludedportrange protocol=tcp`; choose a port outside the ranges (below 49152 is never reserved dynamically). Production's ports (8080, 8081, 8443) are not affected |
| Login answers 429 | more than 10 login attempts a minute from that client address (section 10) | wait a minute; the sender is in the proxy's access log (`client_ip`) |
| A user cannot log in although the password is right | the account is locked: 5 wrong passwords (by the user or by somebody who knows the address), 15 minutes (section 10) | wait, or unlock under **Users** |
| A request is answered 413 | a body above 128 KiB (section 15; above 1 MiB the proxy answers, without a body); a webhook above 64 KiB | nothing the dashboard sends is that large: look at the sender (`client_ip`) |
| The browser console shows "violates the following Content Security Policy directive" and images or requests fail | the frontend was built with a wrong `VITE_API_BASE_URL` / `VITE_STORAGE_ORIGIN`, or a new feature loads from a source the policy does not name | Vercel -> Environment Variables (section 13), then a release; a new source goes into `frontend/vite/contentSecurityPolicy.ts` |
| An upload of an image is refused by the storage (403), other uploads work | the file is not exactly as long as the size the page announced (the length is signed into the upload link, section 16) - a client that is not the dashboard, or a proxy in front that changes the body | the dashboard sends the file as it is; look at the sender |
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
- **Backups**: none are taken (section 13). Losing Docker Desktop's data (reset, purge, uninstall, a broken virtual
  disk) or the disk loses everything. When they are switched on (section 6), they are as far from the machine as the
  chosen destination is.
- Hardening of tasks 026, 026b and 026c (sections 15 and 16) is in place.
- The `<style>` elements of the UI library need `style-src 'unsafe-inline'` in the frontend's policy (a static host
  cannot issue nonces); scripts are restricted to the files of the build.
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
| `VITE_API_BASE_URL` / `VITE_STORAGE_ORIGIN` in Vercel (Production) | `https://omni.tail70815b.ts.net` / `https://omni.tail70815b.ts.net:8443` |
| `SMOKE_STORAGE_ORIGIN` for `task-025b.ps1` | `https://omni.tail70815b.ts.net:8443` |
| Storage downtime during a backup | 13 to 14 s (measured on throwaway stacks in 025b, 025d and 026; never on production) |
| Restore into a new stack | healthy after 40 s, counts and storage entries identical to the manifest (throwaway stack with smoke data, 025d) |
| After a reboot (2026-10-12) | nothing was started by hand: all containers running 62 s after the boot (logon included), both Funnel ports on, first login from the internet 112 s after the boot. Docker Desktop - and with it the stack - starts with the logon; whether Tailscale alone serves before a logon was not measured |
| Backups | **none (user decision 2026-10-12)**: no backup task is registered, no backup of production exists, the backup scripts have never run against `omnichannel-prod`; "Production steps" (`docs/tasks/README.md`) no longer asks for one |
| Backup destination, time of the daily task | none |

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
- A request from a device of the tailnet carries the **Tailscale login of its user** in headers
  (`Tailscale-User-Login`, `-Name`, `-Profile-Pic`). Since task 026 the proxy removes the three before the request
  reaches the API or the storage and leaves them out of its access log (shown on a throwaway stack with a temporary
  upstream that echoes what it receives). Log lines written before that release still hold them until they rotate
  out (5 x 10 MB).

## 14. Moving to an Oracle Always Free VM (task 025c, not done)

Only when an always-on host is wanted. What is known, what is open:

- **Shape:** Ampere A1 (arm64), 2 OCPU / 12 GB in the Always Free allowance at the time of the hosting decision; a
  card is needed at signup; capacity is often exhausted in popular regions. **Idle reclamation:** an instance whose
  CPU, network and memory stay under 20 % for 7 days may be reclaimed — this stack idles far below that, so the rule
  must be read again and answered (a paid-as-you-go account without usage, or accept the risk) before relying on it.
- **Build:** natively on the VM (`set-image-tag.ps1` or an `IMAGE_TAG` line by hand, then `docker compose ... build`); the images are verified for `linux/arm64` (task 025,
  and the manual `arm64` CI run of 025b). Docker Engine, not Docker Desktop: the stack starts at boot without a logon.
- **Public names — undecided:** either Tailscale Funnel on the VM (same procedure as here, same kind of name), or the
  proxy's TLS mode with two real host names (`API_SITE_ADDRESS`, `STORAGE_SITE_ADDRESS`,
  `-f docker/docker-compose.prod.tls.yml`, ports 80 / 443 open in the VM's security list **and** its host firewall) —
  that needs a domain. TLS mode has never been exercised.
- **What to copy:** a backup made for the move (section 6 - there is no standing one) and, if the secrets should
  stay, `docker/.env.prod` (over a private channel). Then "After the machine is lost" in section 7. A new public name means: new `STORAGE_PUBLIC_URL`, new
  `VITE_API_BASE_URL` in Vercel and a release.
- **Backups** then leave the VM by another way than a synced folder (object storage bucket of the same account, or
  pulled by this machine) — to be decided in 025c. `backup-prod.ps1` runs in PowerShell 7 on Linux only after a check
  (paths, `%USERPROFILE%`); a scheduled task becomes a systemd timer.

## 15. Containers, limits and headers (task 026)

What every container of the stack may do, measured on a throwaway stack (`docker inspect`, task 026) - the compose
file is the source, this table the explanation:

| Service | Runs as | Capabilities (all others dropped) | Root file system | Why not less |
| --- | --- | --- | --- | --- |
| `api`, `workers`, `migrate` | the non-root user of the .NET images (1654) | none | read-only, `/tmp` in memory | — (the migration bundle unpacks itself into `/tmp`) |
| `proxy` (Caddy) | 65532:65532 | `NET_BIND_SERVICE` | read-only; `/data`, `/config` in memory | the binary of the image carries that file capability: without it the kernel refuses to start it (`operation not permitted`). Its ports (8080, 8081, 8090) need none |
| `objectstorage` (SeaweedFS) | 1000:1000 (the image's `seaweed`) | none | read-only, `/tmp` in memory | — |
| `rabbitmq` | `rabbitmq` (999) | none | read-only, `/tmp` in memory | — (as root with no capability the health check could not read the node's cookie) |
| `postgres` | starts as root, the server runs as `postgres` (999) | `CHOWN`, `DAC_READ_SEARCH`, `FOWNER`, `SETGID`, `SETUID` | writable | the entrypoint takes the data and socket directories and switches user: without one of the five the start fails or logs `Operation not permitted`. Writable because the operating scripts copy statement and dump files into the container (`docker cp` refuses a read-only root) |

Every service has `no-new-privileges`. `docker compose @stack exec rabbitmq ...` now runs as `rabbitmq`, `exec
postgres ...` still as root (with the five capabilities only: it reads everything, it cannot write where the file
permissions forbid it).

- **A storage volume that does not belong to 1000:1000** (written by a start as root, or restored by hand): the
  storage restarts in a loop with `permission denied`. Once, with the storage stopped (a quarter of a minute without
  images and exports):

```powershell
docker compose @stack stop objectstorage
docker run --rm --network none -v omnichannel-prod_objectstorage-data:/data --entrypoint chown postgres:17 -R 1000:1000 /data
docker compose @stack up -d --wait objectstorage
```

  `chown` changes owners, nothing else; the volume name is the only thing to read twice. `restore-prod.ps1` needs no
  such step (the archive keeps the owners).
- **RabbitMQ and memory:** the broker cannot see the limit of its container and counted on the memory of Docker's
  whole virtual machine (measured: 3591 MiB, high watermark 2154 MiB - above the container's 1 GiB, so the container
  would have been killed before the broker slowed publishers down). `docker/rabbitmq/20-memory.conf` tells it 1 GiB
  (watermark 614 MiB). **Change `mem_limit` of the service and that file together.**
- **Request bodies:** the API reads at most 128 KiB (`RequestBodyLimit.MaxBytes`; twice the largest body it accepts by
  its own rules - a webhook of 64 KiB; a stock import of 200 lines is about 20 kB). The API site of the proxy allows
  **8 times as much, 1 MiB** (`request_body max_size`; `RequestBodyLimit.ProxyLimitFactor`, a test of the backend
  asserts the relation): a backstop. With equal limits the answer was a race - the proxy fails only when it reads
  past its limit, and then with a bare 413 no page can read (task 026b); now every body between the two limits
  reaches the API, which answers `413` with a problem document (`errorCode` `request_body_too_large`, the correlation
  id, the CORS headers), with or without a Content-Length - except, now and then, a body **without** a
  Content-Length through Funnel: the tunnel answers 502 when the API has refused before the whole body was sent (no
  browser request is sent that way; review 026c q7: accepted - `_hardening.ps1` takes that 502 as a refusal only when
  the target is not this machine, `Test-TunnelledOrigin`, and stays strict everywhere else). Above 1 MiB the proxy refuses by itself (status only;
  through Funnel 413 or 502).
  **Change the two numbers together.** The storage site has no limit (the size of an image is signed into its upload
  link and checked again when it is confirmed).
- **Headers:** every answer of the API carries `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
  `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` and `Cache-Control: no-store`; every answer of
  the storage origin `nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`; the static host
  `frame-ancestors 'none'`, `nosniff`, a referrer and a permissions policy (`frontend/vercel.json`), and the page
  itself the policy of the build (`frontend/vite/contentSecurityPolicy.ts`). `scripts\smoke\task-026.ps1` (a stack
  through its proxy) and `task-025b.ps1` (live) check them.
- **The volumes `omnichannel-prod_proxy-data` and `_proxy-config`** are no longer used (Caddy keeps its state in
  memory without TLS). They are empty of anything needed and were left in place: removing a volume of production is
  the user's to do (`docker volume rm omnichannel-prod_proxy-data omnichannel-prod_proxy-config` - these two names only).

## 16. Image objects in the storage (task 026c)

Product images live in the bucket under two prefixes; export files under `exports/` (their own retention, section 1).

| Prefix | What | Who writes it | Who deletes it |
| --- | --- | --- | --- |
| `uploads/products/{productId}/{uploadId}` | an upload (staging) | the browser, with a presigned link that is valid 5 minutes for exactly one type and length | the confirmation, after its commit; the clean-up when it is older than `MinAge` |
| `products/{productId}/{objectId}` | what a product's image refers to (`Products.ImageKey`) | the API, by copying a confirmed upload inside the storage - once, never again | replacing or removing the image; the clean-up when it is older than `MinAge` **and no product refers to it** |

- **The clean-up** runs in the Workers (`ImageCleanup`: every 6 hours, objects older than 24 hours, at most 500 per
  run; the first run comes within 30 seconds after the Workers start; one run at a time over both replicas). Each
  run is one line in `logs workers` - `Image clean-up run ...: N objects listed, N uploads and N unreferenced images
  deleted, N old images in use kept, ...` - and a run that deleted something is one event on **Logs / Audit**
  ("Unused image files deleted", with the keys). `N old images in use kept` is the number of images the products
  have that are older than a day: it should be the number of products with an image, and it never appears among the
  deleted ones.
- **What would the next run delete?** Read-only, without starting a worker and without deleting or writing anything:

  ```powershell
  docker compose @stack run --rm --no-deps workers --image-cleanup-dry-run=true
  ```

  The last lines name the counts and up to 100 keys of each kind. (It needs the images of a release that has the
  clean-up: after step 2 of section 4 the new image exists while the old containers still run.)
- **Nothing a product refers to can be deleted**, also not an image that is being confirmed while a run is under way:
  a final object is written once, by the confirmation that refers to it within 30 seconds or removes it, and the
  clean-up only looks at objects older than `MinAge` (at least 2 minutes; the Workers do not start with less). There
  is no backup: an object that was deleted is gone - which is why the age is a day in production and why the dry run
  exists. To stop the clean-up: `ImageCleanup__Enabled=false` in the environment of `workers` and `up -d`.
- An object under one of the prefixes that the application did not write (another key form) is never deleted; a run
  logs it as "no key of the application".
- Settings are environment variables of `workers` (`ImageCleanup__Interval`, `ImageCleanup__MinAge`,
  `ImageCleanup__MaxPerRun`, `ImageCleanup__Enabled`); the compose file sets none, the defaults apply.

## 17. Acceptance runs (task 027)

What proves each acceptance criterion and the latest evidence: `docs/acceptance.md`. README "Acceptance" has the commands.

- **`docker/docker-compose.acceptance.yml` is for throwaway stacks and CI only** - never in a command for
  `omnichannel-prod`. It switches the channel simulator's SKU rules on (`SIM-TEMP-` / `SIM-DOWN-` / `SIM-PERM-` /
  `SIM-LOST-` external SKUs fail or drift on purpose) and shortens the retry delays to 1 s / 2 s; the delays shape the
  broker topology, so such a stack starts with a fresh broker volume. `task-027.ps1 -Mode Full` refuses the projects
  `omnichannel-prod` and `omnichannel` and any public origin.
- **The live subset** (only after a release, with the user's go-ahead - it writes `ACC027-` / `E2E027-` demo data and
  leaves it; mappings are deactivated and accounts locked at the end, products cannot be deactivated):
  - `task-027.ps1 -Mode NoDrills -Live` with `SMOKE_BASE_URL` = the public API origin and the production Admin in
    `SMOKE_ADMIN_EMAIL` / `SMOKE_ADMIN_PASSWORD` (`docker\.env.prod` holds no Admin password since go-live; in task 027
    the user stored them as temporary user-level variables, read in-process and removed afterwards). Production's SKU
    rules are off: the retry and DLQ parts report `skipped: SKU rules off`, never passed.
  - `npm run e2e` in `frontend/` with `E2E_BASE_URL` = the Vercel origin, `E2E_API_URL` = the public API origin and the
    production Admin in `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD` (set in the shell, never written down); no
    `E2E_STACK_CONTROL`. From this machine also `E2E_HOST_RESOLVER_RULES="MAP <api host> <public Funnel address>"`:
    here the name resolves to the tailnet address, which Chromium blocks for the public Vercel page (README
    "Acceptance").
  - Nothing else on production: no load scenario, no drill, no container stop, no `psql` write.

# Task 025b — Ops: go-live (public GitHub repository + CI, Vercel, backend through Tailscale Funnel, backups, runbook)

Status: Open
Written by: Cowork, 2026-10-10
Depends on: 025 (accepted — `docs/tasks/025-build-and-run-anywhere.review.md`)

## Context
Doc §4 (Docker), §6 (HTTPS, CORS, secrets), §14 (deployment, observability); CLAUDE.md.
Task 025 made the stack build and run anywhere and verified it locally. This task puts it **live**:

- **Decisions (user, 2026-10-10, after review 025):**
  - The GitHub repository is **public** (unlimited Actions minutes, secret scanning with push protection).
  - The backend goes live on the **user's own Windows machine** (Docker Desktop, amd64) through **Tailscale Funnel**.
    The **Oracle Always Free VM is not provisioned here**: it gets a runbook section only; provisioning is task
    **025c** (planned, only if the user wants an always-on host).
- Frontend: **Vercel Hobby**, static build of `frontend/` with `VITE_API_BASE_URL` = the Funnel API origin
  (log "Hosting decision": no Vercel rewrite to the API).
- Two public origins on one Funnel host name (ports are part of an origin):
  - API: `https://<machine>.<tailnet>.ts.net` (Funnel 443 → `http://127.0.0.1:8080`, the proxy's API site);
  - storage: `https://<machine>.<tailnet>.ts.net:8443` (Funnel 8443 → `http://127.0.0.1:8081`, the storage site).
- **Release model (decision of this task, log "Task 025b plan"):** Vercel's production branch is **`production`**, not
  `master`. A release = update the backend on the host from commit X, then push X to `production`. Both halves always
  run the same commit; a push to `master` never changes what users see.

Read first: review 025 (questions 1, 3, findings), log "Hosting decision", "Task 025 plan", "Task 025 result",
"Task 025b plan"; README "Production deployment", "Product images", "Frontend", "Smoke scripts";
`docker/docker-compose.prod.yml`, `docker/proxy/Caddyfile`, `docker/prod.env.example`, `scripts/deploy/new-prod-env.ps1`,
`scripts/smoke/task-025.ps1`, `scripts/smoke/_common.ps1`, `.github/workflows/ci.yml`; `docs/tasks/README.md`.

### Who does what
Steps marked **USER** need the user's own accounts or a decision only the user can make: Claude Code stops there,
says exactly what to do (screens, settings, values to type), waits until the user confirms in the chat, then checks
the result itself. Everything else is done by Claude Code. Never ask the user to paste a secret, token or password
into the chat; the env file and the CLIs' own login flows hold them.

### Production steps (new rule — add it to `docs/tasks/README.md` in this task, Part A7)
Production is **empty until go-live**, so in this task Claude Code builds, migrates, starts and smoke-tests it itself.
From the moment the user has logged in to the live system, production holds real state, and:
- a production step (release, migration, restore, secret rotation, scaling) is run only when a task or the user asks
  for it, by the runbook, **after a backup** of that moment;
- never `down -v`, never removing a volume of `omnichannel-prod`, never `--force` / `-Force` over the production env
  file; migrations additive only, listed before applying (unchanged rules of "Development steps");
- smoke scripts against production only when the task says so (they leave smoke data — see Part E).

### Stop and report (`Blocked`) if
- the history audit (A1) finds anything that looks like a secret — **do not rewrite history**, do not push; report
  file, commit and rule only, never the value;
- Funnel changes the `Host` header on the storage port and the Caddy fix allowed in C2 does not make a presigned `PUT`
  work;
- Funnel cannot be used on this Windows machine, or any step needs a paid plan, a card or a domain;
- a step would delete or replace a volume of a stack that is not this task's own throwaway project;
- `dotnet ef migrations list` / the bundle shows a pending migration (025b has none);
- a decision is needed that is not in this file, review 025 or the log.

## Part A — Repository preparation (no account needed; before anything is published)

### A1. History audit before the repository goes public
- Full history, all branches: `gitleaks` from its official container image (pinned version, read-only mount of the
  repository; nothing installed on the machine, nothing added to the repository) — report the version, the number of
  commits scanned and the findings **without values** (file, commit, rule). Expected: none. Anything found → `Blocked`.
- Additionally, a scripted check that every committed assignment of a known secret variable (`*_PASSWORD`,
  `JWT_SIGNING_KEY`, `*SECRET*`, `*ACCESS_KEY*`, `SigningKey`, `Password` in `appsettings*.json`, connection strings
  with `Password=`) in **any** commit is empty or an obvious placeholder (`change-me…`, `replace-with…`, `${…}`).
- List (names only) every tracked file over 1 MB and anything under `docs/` that names a person, a company or a
  machine-specific path; report the list. Nothing is removed without the user.

### A2. CI workflow lint
`actionlint` from its official container image (pinned) over `.github/workflows/ci.yml`; fix findings (CI file only).
Its first real run is B2.

### A3. `frontend/vercel.json`
- SPA fallback: every path that is not a file of the build serves `/index.html` (deep links such as `/orders/<id>`
  and a browser refresh on them); files of the build always win.
- `Cache-Control: public, max-age=31536000, immutable` for `/assets/*` (hashed names); `index.html` keeps Vercel's
  default (revalidated).
- Only the `production` branch deploys (release model above). Check the mechanism against Vercel's **current**
  documentation (`git.deploymentEnabled` in `vercel.json`, or the project's "Ignored Build Step"); prefer the one that
  lives in the repository. Cite the documentation page in the report.
- No headers beyond the cache rule (security headers / CSP are 026), no rewrite to the API.

### A4. Storage refusal checks (review 025, P2)
Add to `scripts/smoke/task-025.ps1` (after the image steps), each against `SMOKE_STORAGE_ORIGIN`:
an unsigned `GET` of the uploaded image's object → 403; an unsigned listing of the bucket → 403; an unsigned `PUT`
of a new key → 403 and the key does not exist afterwards (checked with a presigned request through the API, or the
next smoke steps). Use the object key from the confirmed image link (path only, no query). Run the script locally
against a throwaway `omni025b` stack first (as in 025: own project name, own ports, own env file, `down -v` of that
project only at the end).

### A5. Backup and restore scripts (`scripts/deploy/`, PowerShell 5.1, ASCII, never print a secret)
- `backup-prod.ps1 [-Destination <folder>] [-ProjectName omnichannel-prod] [-EnvFile docker\.env.prod]`:
  - PostgreSQL: `pg_dump -Fc` of `omnichannel` run inside the `postgres` container as the superuser over the local
    socket (no password on any command line) → `postgres.dump`;
  - object storage: stop `objectstorage`, archive its volume with an image the stack already has (no new image),
    start it again, wait until healthy → `objectstorage.tar` (the short stop is accepted: uploads and export
    `PUT`s retry or fail visibly; name the measured downtime in the report);
  - `manifest.json`: time (UTC and +07:00), git commit of the running images, applied migrations (from
    `__EFMigrationsHistory`), sizes and SHA-256 of both files;
  - a timestamped folder under the destination (default `%USERPROFILE%\OmnichannelBackups`, outside the
    repository); retention: the newest 7 daily and the newest 4 Sunday backups, older ones removed — only folders the
    script itself created (manifest present);
  - **never** copies `docker/.env.prod`; exit code ≠ 0 and nothing reported as done on any failure.
  - RabbitMQ is **not** backed up (messages are transient; the outbox is in PostgreSQL) — say what that means after a
    restore in the runbook (A6).
- `restore-prod.ps1 -BackupPath <folder> -ProjectName <name> -EnvFile <file>`: restores **only into a stack that
  does not exist yet** (refuses when the project has any volume): create the volumes, start `postgres` (init script
  creates the roles from the env file), `pg_restore` as the superuser, unpack the storage archive into the new
  volume, start `objectstorage`, run `migrate` (expected: nothing pending), then `up -d --wait`. Verifies the
  manifest's SHA-256 before starting. Restoring over the live stack is not a script feature: it is a runbook
  procedure decided by the user (back up, stop, rename, restore beside it, switch).
- `register-backup-task.ps1 [-At 03:00] [-Destination …]`: registers a Windows scheduled task for the current user
  that runs `backup-prod.ps1` daily (only when the user is logged on — Docker Desktop runs in the user session), and
  `-Unregister`. Running it changes the user's system: **USER** confirms before Claude Code runs it (Part E4).

### A6. Compose and env template
- `workers`: `deploy.replicas: ${WORKERS_REPLICAS:-1}` (review 025, P3); `WORKERS_REPLICAS` in
  `prod.env.example` (production: 2). `--scale` still overrides; README updated.
- Nothing else in the stack changes unless C2 requires the Caddy fix.

### A7. Documents
- `docs/runbook.md` (new) — operating the live system, for the user, by procedure: what runs where (diagram in
  text), start / stop / status / logs (`docker compose … logs`, the proxy's access log), the two expected Data
  Protection warnings (review 025 q3), **after a reboot** (what comes back by itself, what needs a logged-on user),
  **release** (backup → `git pull` → build → `run --rm migrate` → `up -d --wait` → health → push the same commit
  to `production` → Vercel deploys → live smoke subset), rollback (frontend: Vercel's previous deployment; backend:
  previous commit's images — only when no migration ran since; otherwise restore), migrations (list first, additive
  only, backup first), backup / restore / restore drill, secret rotation (JWT key = every session ends; webhook
  secret with two active secrets; database / RabbitMQ / storage passwords: what must change where, in order),
  bootstrap admin (blank the password after the first login, review 025 P3), re-driving tasks after a restore
  (non-terminal tasks whose messages were lost with RabbitMQ: find them on Sync & Processing, retry; the gap is the
  026 item "stale non-terminal tasks"), incident list (machine asleep or off, Docker Desktop not started, Funnel down,
  disk full, a DLQ filling up, storage refusing uploads), what is simulated in production, known limitations
  (client address result of C2, uptime bound to the machine, no monitoring beyond health), and an **Oracle VM
  section** for 025c (arm64 native build, TLS mode needs host names or Funnel on the VM — undecided, idle
  reclamation rule, what to copy: backup + env file).
- README "Production deployment": live topology (Vercel + Funnel), `WORKERS_REPLICAS`, the release model, backups,
  link to the runbook; TLS mode "validated as configuration, not exercised" (review 025 q2).
- `docs/tasks/README.md`: section "Production steps" with the rule above.

## Part B — GitHub (public)

### B1. **USER**
- GitHub account (exists or new), `gh auth login` on this machine (browser flow; Claude Code checks with
  `gh auth status`, which prints no token).
- Choose the repository name (suggest `omnichannel-order-inventory`) and the owner.
- Optional, user's choice: the commit e-mail for future commits (`git config user.email` = GitHub's no-reply address).
  History is not rewritten (review 025 finding).
- No LICENSE is added (public without a licence = all rights reserved) unless the user asks.

### B2. Claude Code (only after A1 and A2 are clean)
- `gh repo create <owner>/<name> --public --source . --remote origin`, push `master`.
- Repository security settings, read back with `gh api` and reported: secret scanning and push protection **on**,
  Dependabot alerts on (no version-update PRs), Actions default token permissions **read-only**, "Allow GitHub
  Actions to create and approve pull requests" off, fork pull-request workflows require approval for first-time
  contributors (the GitHub default — confirm).
- A ruleset for `master` and `production`: no force push, no deletion. No required pull request or status check
  (single developer; Claude Code pushes to `master`) — write the reason in the report.
- **First CI run**: `gh run watch` until done; all three jobs green; durations per job in the report. The arm64 image
  builds once by `workflow_dispatch` (`arm64=true`), duration reported. A red job caused by the workflow or the
  runner (paths, versions, Docker on the runner, test timing on 4 cores) is fixed here; a product test that fails on
  the runner but passes locally is investigated (log "Task 017 result" rules), not retried until green.
- Keep `--maxWorkers=2` for the frontend unless the first run shows a reason; report the wall time.

## Part C — Backend live on this machine (Tailscale Funnel)

### C1. **USER**
- Tailscale for Windows installed and logged in; in the admin console: MagicDNS on, HTTPS certificates on, the
  `funnel` node attribute for this machine in the tailnet policy. Machine name chosen (it becomes public in the host
  name and in certificate transparency logs; suggest a neutral name such as `omni`).
- Vercel project name chosen now (Part D1 creates it): the frontend origin `https://<project>.vercel.app` goes into the
  env file before the stack starts.
- Windows power settings: no sleep while plugged in; Docker Desktop "Start when you sign in" on.
- Confirm the host name: Claude Code reads it with `tailscale status --json` (no key is printed).

### C2. Funnel spike (before the stack holds anything)
1. Env file: `new-prod-env.ps1 -AdminEmail <user's> -FrontendOrigin https://<project>.vercel.app -StoragePublicUrl
   https://<host>:8443` (the admin password is shown once — **USER** stores it in a password manager; the chat
   never sees it). Set `WORKERS_REPLICAS=2`; ports stay 8080 / 8081 on `127.0.0.1`.
2. Build, `up -d --wait postgres`, `run --rm migrate`, `up -d --wait` (project `omnichannel-prod`).
3. `tailscale funnel --bg --https=443 http://127.0.0.1:8080` and `--https=8443 http://127.0.0.1:8081`;
   `tailscale funnel status` in the report.
4. **Measure from outside the tailnet** (a request that really goes through Funnel: **USER** opens
   `https://<host>/health` and `https://<host>:8443/` on a phone over mobile data, Wi-Fi off; Claude Code then reads
   the proxy's access log). Requests from this machine itself resolve the name to its tailnet address and take
   Tailscale Serve's path, whose headers can differ (tailnet identity headers, a tailnet `X-Forwarded-For`): they are
   fine for functional checks (smoke, step 6) but **not** for this measurement. For one API request and one storage
   request on **each** path, record `remote_ip`, `client_ip`, `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`,
   `X-Forwarded-Host` exactly as logged (no query strings are logged).
5. Apply review 025 q1's rule: set `PROXY_TRUSTED_PROXIES` and `API_HTTPS_REDIRECT` accordingly, `up -d` the changed
   services, repeat the measurement, write the result and its consequence (client address available or not) in the
   report and the runbook.
6. **Host on the storage port:** a presigned `PUT` must succeed on both paths — from this machine (smoke) and from
   outside (E3, the phone uploads a product image). If Funnel rewrote `Host`, the one
   allowed fix is in the Caddyfile's storage site: pass the original host from the trusted tunnel to SeaweedFS (e.g.
   `header_up Host {header.X-Forwarded-Host}` only for requests from the trusted proxy) — with a local test that a
   request from anyone else cannot choose the Host. Otherwise `Blocked`.
7. WebSockets: does the hub connect with the WebSocket transport through Funnel? If it falls back (SSE / long
   polling), record it; it is accepted (SignalR's fallback is the designed behaviour).

## Part D — Vercel

### D1. **USER**
Vercel Hobby account (sign in with GitHub), import the repository: root directory `frontend`, framework Vite, build
`npm run build`, output `dist`, Node.js version matching `frontend/.nvmrc` (check that Vercel offers it; otherwise the
nearest supported LTS ≥ the `engines` field — report which), environment variable `VITE_API_BASE_URL` =
`https://<host>` for **Production**, production branch = `production`. Claude Code gives the exact list of settings
before the user starts and checks the result afterwards (`vercel` CLI is **not** required; if the user prefers it,
`npx vercel@<pinned>` with the user's own login is fine).

### D2. Claude Code
Create `production` from the commit the stack was built from, push it; wait for the deployment (Vercel's GitHub
check on the commit, or the user confirms). Then Part E.

## Part E — Live verification

1. **Smoke** (`task-025.ps1` with `SMOKE_BASE_URL=https://<host>`, `SMOKE_STORAGE_ORIGIN=https://<host>:8443`,
   `SMOKE_FRONTEND_ORIGIN=https://<project>.vercel.app`, admin from the env file the script reads itself — add the
   lookup if the script cannot yet read `docker/.env.prod` without printing it): all steps green, including A4's
   refusals over the internet. It leaves smoke data in production (prefixed smoke records, two orders, locked smoke
   accounts) — **accepted once at go-live** (decision in the log); later production runs only by the runbook.
2. **`scripts/smoke/task-025b.ps1`** (new, small, read-only): Vercel deep link `/orders` → 200 HTML (SPA fallback);
   an `/assets/*` file → `immutable`; preflight from `https://<project>.vercel.app` allowed, from another `*.vercel.app`
   origin refused; `http://<host>` (no TLS) is not served by Funnel or is redirected — record which;
   `https://<host>/health` 200; the API answers `Strict-Transport-Security` only if C2 trusted `-Proto` (record).
3. **Browser check on the live URL** (1280 px; as in 025, values read from the page, the network log and
   screenshots): login, dashboard live without Refresh (transport recorded), image upload and display from the storage
   origin, an export downloaded, a payment with live invoice / e-mail status, a deep-link refresh. **USER**: open the
   live URL on a phone over mobile data (Wi-Fi off — the only check that uses Funnel's public path end to end), log
   in, open an order, upload a product image and see it — report what the user saw.
4. **Backups:** **USER** chooses the destination (suggest a folder synced by the user's own cloud drive: the copy
   must leave the machine; the dump contains customer data and password hashes — the user's private storage only) and
   confirms `register-backup-task.ps1`. Then: one backup by hand; the scheduled task run once on demand
   (`Start-ScheduledTask`) and its result checked; retention tested with fabricated old backup folders in a temporary
   destination (not the user's real one).
5. **Restore drill:** the newest production backup restored with `restore-prod.ps1` into a throwaway project
   `omni025b-restore` (own ports, own env file from the generator); compare row counts of `Orders`, `OrderItems`,
   `Inventories`, `InventoryMovements`, `ProcessingTasks`, `OutboxEvents`, `AspNetUsers` and the object count of the
   bucket with production at backup time; log in to the restored API with the admin; then `down -v` **of
   `omni025b-restore` only**. Time to restore in the report.
6. **Reboot test (USER restarts the machine and logs on):** afterwards, without manual steps by anyone: all
   services healthy (`restart: unless-stopped`), Funnel serving again, `https://<host>/health` 200 from outside, the
   live frontend logs in. Record what needed the logon.
7. **Bootstrap admin:** after the user's first login, `BOOTSTRAP_ADMIN_PASSWORD` blanked in `docker/.env.prod`,
   `up -d api`, `docker inspect` shows it empty (report that, not the value).

## Acceptance criteria
- [ ] A1 audit clean (gitleaks version, commits scanned, 0 findings; the variable check; the names list) **before**
      the first push.
- [ ] actionlint clean; first CI run on GitHub green in all three jobs; one arm64 dispatch run; durations reported.
- [ ] Public repository with the security settings and the ruleset read back via `gh api`.
- [ ] `vercel.json` (fallback, asset caching, production-branch-only deploys) with the cited documentation; live
      deep-link refresh works.
- [ ] Funnel measurement table (C2.4) and the applied rule; storage `PUT` through Funnel works; WebSocket transport
      result.
- [ ] `task-025.ps1` with the A4 steps: green locally (`omni025b`, then removed) and live; `task-025b.ps1` green live.
- [ ] Browser check on the live URL; the user's phone check.
- [ ] Backup by hand + scheduled run + retention test; restore drill with matching counts and restore time;
      `omni025b-restore` removed.
- [ ] Reboot test; bootstrap password blanked.
- [ ] `docs/runbook.md`, README, `docs/tasks/README.md` "Production steps"; no secret, token, host key or live
      password in any committed file (the live host name and Vercel origin may appear in the report; they are
      public anyway).
- [ ] Backend and frontend suites unchanged and green if any code changed (if only scripts / config / docs changed,
      say so and run `dotnet build` + `npm run build` once).
- [ ] Report: "Development steps (applied)" — none expected; "Production steps (applied)" — new section, each step
      with its command (no secret) and result; "Manual steps for the user" — only what is left.

## Out of scope
Provisioning the Oracle VM (025c); a custom domain; Caddy's TLS mode on a public host; publishing images to a
registry; deployment from CI; real channel / invoice / e-mail providers; security headers, CSP, body limits,
container hardening, login throttling per account (026); external uptime monitoring (a new account — follow-up);
rewriting git history; a licence.

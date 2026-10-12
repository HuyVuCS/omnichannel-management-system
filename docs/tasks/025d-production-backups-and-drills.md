# Task 025d — Ops: production backups on, restore drill of a production backup, secret-rotation drill

Status: Open
Written by: Cowork, 2026-10-12
Depends on: 025b (accepted with one open item — `docs/tasks/025b-go-live.review.md`, question 1)
Before: 026 (its release is the first production step that needs a backup)

## Context
Doc §14 (deployment, backups, observability); CLAUDE.md; `docs/runbook.md` sections 6, 7, 9; log "Task 025b plan"
(rows "Backups", "Restore", "Production steps") and "Task 025b result".
Production (`omnichannel-prod` on the user's machine, live through Funnel) holds state and has **no backup**: E4 / E5
of 025b were skipped. The scripts exist and were verified on throwaway stacks only; the scheduled start was never
registered or run. Review 025b q3: the rotation procedures of runbook §9 were never exercised.

This task: (A) two small script improvements, (B) backups on production, (C) a restore drill of a **production**
backup, (D) a rotation drill on a throwaway stack. **No product code, no image, no release, no migration.**

### Who does what
Steps marked **USER** need a decision or a confirmation only the user can give: Claude Code stops, says exactly what is
needed, waits for the confirmation in the chat, then checks the result itself. Never ask for a secret in the chat.

### Production steps in this task (rule of `docs/tasks/README.md`)
Allowed on `omnichannel-prod` **only**: `backup-prod.ps1` (stops `objectstorage` for ~15 s — say so to the user before
the first run), read-only `psql` queries as the superuser through `_stack.ps1` (`Invoke-StackSql`), `docker compose ps`
/ `logs`, and — after B1 — `register-backup-task.ps1` and `Start-ScheduledTask`. Nothing else: no `up`, no `build`, no
`migrate`, no env-file change, no rotation. A drill stack never shares a project name, a port, a network or an env
file with production.

### Stop and report (`Blocked`) if
- a production backup fails twice (report the script's message; do not change production to make it pass);
- the restored counts of the production backup differ from the manifest while nobody used the system (C);
- a rotation row of runbook §9 cannot be done without putting a secret on a command line, in a file in the
  repository, or in a log (D) and no variant in this file solves it;
- anything would touch a volume or the env file of `omnichannel-prod`.

## Part A — Script improvements (before any production run)

### A1. Row counts in the manifest; compared by the restore
- `backup-prod.ps1`: right **after** `pg_dump`, one `Invoke-StackSql` that counts the rows of `Orders`, `OrderItems`,
  `Inventories`, `InventoryMovements`, `ProcessingTasks`, `ProcessingAttempts`, `OutboxEvents`, `Products`,
  `Customers`, `Alerts`, `AspNetUsers` (check each name against the model; a table that does not exist is a script
  error, not a skipped row) → `manifest.rowCounts` (object, table → count). Also the number of entries of the storage
  archive (`tar -tf` in the helper container, before it is removed) → `manifest.storageArchiveEntries`.
  The counts are taken seconds after the dump's snapshot: under traffic they can be higher than the dump.
- `restore-prod.ps1`: after `pg_restore`, the same counts on the new database; print a table *table / manifest /
  restored*; any difference → a **warning** naming the tables (exit code stays 0: the manifest may be newer than the
  snapshot). After the storage restore, count the entries of the new volume the same way and compare (warning).
- Old manifests without `rowCounts` restore as before (no comparison, one line saying so).
- `_stack.ps1` gets the shared count helper; no new image, ASCII only, PowerShell 5.1.

### A2. Runbook
- §6: the dump and the storage archive are taken seconds apart (an image confirmed in between is an orphan after a
  restore, an image removed in between is missing for its product) — accepted; the weekly look: `backup.log` **and**
  Task Scheduler's "Last Run Result" (0x0) of "Omnichannel backup (omnichannel-prod)" — and where both are seen.
- §6: the backup is **not encrypted** (decision in the log): the destination's own access control protects it.
- §7: the drill reads the comparison table of A1 instead of comparing by hand.
- §13 "Live values": backup destination *type* (cloud-synced folder or local interim — never the path), time of the
  daily task, size and storage downtime of the first production backup, restore time of the drill.

### A3. Verification of A on a throwaway stack
`omni025d` (own project, ports, edge subnet, env file — as `omni025b` in 025b): `task-025.ps1` (any green run that
leaves data), `backup-prod.ps1 -ProjectName omni025d -Destination <temp folder>`, `restore-prod.ps1` into
`omni025d-r` → the table shows identical counts and entries; a manifest of 025b's format (without `rowCounts`,
hand-made copy) restores with the "no comparison" line. Then `--profile migrate down -v` of **`omni025d` and
`omni025d-r` only**, env files removed, temp folder removed.

## Part B — Backups on production

### B1. **USER** — destination
Claude Code explains the choice in three lines and waits:
- **Recommended:** a folder inside the user's own cloud-synced drive (OneDrive / Google Drive for desktop), e.g.
  `<drive>\OmnichannelBackups`. The copy leaves the machine; deleted backups stay in the drive's recycle bin for a while.
  The dump contains customers, orders and password hashes: the user's private drive only, never a shared folder.
- **Interim, only if the user declines a cloud folder:** a local folder outside the repository, preferably on another
  disk. Protects against Docker data loss (factory reset, purge, uninstall), **not** against losing the disk or the
  machine. Recorded as "local interim" in the log and the runbook; 026's release still proceeds.
- The user also confirms that `register-backup-task.ps1 -At 03:00` may register a scheduled task for their account
  (changes their system; `-Unregister` removes it).
Claude Code writes the path only into the command it runs, never into a committed file.

### B2. First production backup (by hand)
Tell the user that storage uploads / downloads fail for ~15 s, then `backup-prod.ps1 -Destination <B1>`. Report: exit
code, folder name, sizes, storage downtime, manifest (commit, 14 migrations, counts). Expected `gitCommit` =
`264270c…` for api and workers (if `unknown`: report, do not rebuild — it means the images were built without
`GIT_COMMIT`; the next release fixes it).

### B3. Scheduled task
`register-backup-task.ps1 -At 03:00 -Destination <B1>`; read back with `Get-ScheduledTask` / `Get-ScheduledTaskInfo`
(action arguments, principal, trigger, settings — the repository path with its space and non-ASCII characters must be
intact in the action). `Start-ScheduledTask`, wait for it to finish, then: `LastTaskResult` = 0, a new `OK` line in
`backup.log`, a second backup folder, the first one kept (same day). If the start fails because of the path: fix it in
`register-backup-task.ps1` (e.g. a `-WorkingDirectory` / quoting fix), re-register, re-run; report the cause.

## Part C — Restore drill of the production backup

1. `restore-prod.ps1 -BackupPath <newest production backup> -ProjectName omni025d-restore -EnvFile
   docker\.env.prod.drill` (env file from `new-prod-env.ps1` with the drill ports / subnet of runbook §7).
2. Run it while nobody uses production (ask the user not to use the live site for those minutes): the A1 table must
   show identical counts and archive entries — otherwise `Blocked`.
3. Log in to the restored API (`http://localhost:18180`) with the **smoke-free** check: `GET /health` 200, then one
   login as a **new** throwaway Admin created through the bootstrap variables of the drill env file (the production
   Admin's password is only with the user; Claude Code does not use it). One stored product image read through the
   restored storage origin (`http://localhost:18181`) → 200.
4. Time from start to healthy in the report and the runbook.
5. `--profile migrate down -v` of **`omni025d-restore` only**, `docker\.env.prod.drill` removed. Production untouched
   (`docker compose -p omnichannel-prod ps` before and after, same container ids).

## Part D — Rotation drill (throwaway stack `omni025d-rot`, Claude Code alone)

Create the stack from `new-prod-env.ps1` (own name, ports, subnet), `WORKERS_REPLICAS=2`, build, migrate, up, one
`task-025.ps1` run. Then each row of runbook §9 except `BOOTSTRAP_ADMIN_PASSWORD`, **in this order**, each followed by
its check:

| Row | Check after the rotation |
| --- | --- |
| `JWT_SIGNING_KEY` | an access token issued before → 401; a fresh login works |
| `WEBHOOK_SECRET_WEBSITE` (two-secret procedure) | during the overlap a webhook signed with the old **and** one with the new secret → 202; after the switch the old → 401 |
| `OMNI_APP_PASSWORD` | `up -d --wait` healthy; an order submitted → `Reserved` by a Worker (both instances reconnected) |
| `OMNI_MIGRATOR_PASSWORD` | `--profile migrate run --rm migrate` → "nothing pending" |
| `POSTGRES_PASSWORD` | `backup-prod.ps1 -ProjectName omni025d-rot` still works (it uses the local socket) |
| `RABBITMQ_PASSWORD` | both Workers connected (logs), an order → `Reserved`, outbox drained |
| `OBJECT_STORAGE_*` keys | presign → `PUT` → confirm of an image works; an upload link issued before the rotation → 403 |

Finally `task-025.ps1` once more → all green. Rules for the drill:
- Secret values are generated (CSPRNG, letters and digits, the generator's lengths) inside the script / session and
  written only into the drill env file or the drill override file; never printed, never on a command line, never in a
  file in the repository.
- **Database passwords without an interactive prompt** (review 025b q3): find a variant that Claude Code and the user
  can both run, keeps the value off every command line, and out of the PostgreSQL log even when the statement fails.
  Suggested: pipe a script into `docker compose … exec -T postgres psql -U postgres -v ON_ERROR_STOP=1` over stdin that
  first runs `SET log_min_error_statement = panic;` then `ALTER ROLE … PASSWORD :'pw';` with `pw` set by `\set` from
  the same stdin — or send a SCRAM verifier computed in PowerShell. Verify that a deliberately failing statement leaves
  no password in `docker compose logs postgres`. Put the chosen variant into runbook §9 (replacing `\password` or as the
  scripted alternative), as a small helper in `scripts/deploy/` if it is more than two lines.
- A row whose procedure in the runbook is wrong or incomplete: fix the runbook row; report what changed and why.
- At the end: `--profile migrate down -v` of **`omni025d-rot` only**, its env and override files removed.
- Runbook §9 line "Exercised at go-live" → which rows were exercised in 025d (drill stack) and that production was
  never rotated.

## Acceptance criteria
- [ ] A1 / A2 done; A3 on `omni025d` / `omni025d-r` green (identical table; old manifest restores), both removed.
- [ ] B1 decision recorded (type of destination, not the path); B2 first production backup OK with manifest details;
      B3 scheduled task registered, read back, started once, `LastTaskResult` 0, two backups kept.
- [ ] C restore drill of a production backup: identical counts and archive entries, restore time, image read,
      `omni025d-restore` removed, production container ids unchanged.
- [ ] D every listed row exercised with its check green, the database-password variant verified against the
      PostgreSQL log, final `task-025.ps1` green, `omni025d-rot` removed; runbook §9 updated.
- [ ] Runbook §6, §7, §9, §13 updated; no path of the backup destination, no secret in a committed file.
- [ ] Only scripts and docs changed: `dotnet build` and `npm run build` not required; `scripts\smoke\_assert-selftest.ps1`
      if `_common.ps1` changed.
- [ ] Report: "Development steps (applied)" — none expected; "Production steps (applied)" — B2, B3 and the read-only
      queries, each with its command (no secret, no destination path) and result; "Manual steps for the user" — the
      weekly look at `backup.log` only. `master` pushed (including the handoff commit `f66ee26`).

## Out of scope
Encrypting backups; a second backup destination; backing up RabbitMQ; automatic notice of a failed backup (with the
uptime-monitor follow-up); rotating any production secret; a release; anything of 026 (incl. the `Tailscale-User-*`
headers); the Oracle VM (025c).

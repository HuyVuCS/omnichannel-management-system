# Review 025b — Ops: go-live (public GitHub repository + CI, Vercel, backend through Tailscale Funnel, backups, runbook)

Reviewer: Cowork, 2026-10-12
Verdict: **Accepted with one open item** — the live system, the repository, CI, `vercel.json`, the Funnel measurement,
the smoke runs and the reboot test meet the task. **Backups on production (E4, E5) were skipped by the user** and are
**not** accepted as permanent: they become task **025d**, which runs **before 026** (question 1). No P0. One P1 (no
backup of a stack that holds state, and the "Production steps" rule blocks every release until one exists). All ten
deviations accepted. Questions 1–3 answered below (1 → 025d, 2 → 026, 3 → 025d drill).

## Checked
- Report and commits `264270c` (A), `431c876` (runbook: live values), `e07dffb` (report, reboot result); handoff
  `f66ee26`. No product code, no migration, no package, no API contract change.
- Read in full: `frontend/vercel.json`; the compose and env-template diffs; `scripts/deploy/_stack.ps1`,
  `backup-prod.ps1`, `restore-prod.ps1`, `register-backup-task.ps1`; the `_common.ps1` and `task-025.ps1` diffs
  (steps 32–35); the checks of `task-025b.ps1`; `docker/proxy/Caddyfile`; runbook sections 2–4, 6–10, 12–13.
- Checked by Cowork against the code:
  - **Release model holds.** `git.deploymentEnabled` `{"**": false, "production": true}`; `origin/production` is at
    `264270c`, the commit the stack was built from (`GIT_COMMIT` label); the later `master` pushes created no
    deployment (report). The rewrite `/((?!assets/).*)` keeps a missing hashed file a 404 (deviation 4 — correct: a
    year-long `immutable` HTML answer under `/assets/` would break the next release for cached browsers).
  - **Backup script** is fail-safe in the ways that matter: `.partial` folder renamed only after both files and the
    manifest exist and are non-empty; the storage service is restarted in a `finally` and waited for until healthy;
    retention only touches folders whose manifest names this tool and this project; the destination may not be inside
    the repository; no env file is read or copied; the dump is written inside the container and copied out (no binary
    over a PowerShell pipe — the right call on 5.1).
  - **Restore script** refuses any project that has a container or a volume, verifies SHA-256 before touching
    Docker, compares the restored migration list with the manifest, restores the storage volume before the storage
    starts, and runs `migrate` after the restore (newer code on an older backup is handled and reported).
  - **Scheduled task**: current user, `Interactive` / `Limited`, no stored password, `StartWhenAvailable`,
    `IgnoreNew`, 2 h limit — matches the plan. **It has never been registered or run anywhere** (E4 skipped), and it
    starts `powershell.exe -File` with the repository path, which contains a space and non-ASCII characters
    (`C:\Thực tập\...`). The manual runs proved the script, not the scheduled start.
  - **Trust boundary after the measurement** is case 1 of review 025 q1 and is applied correctly:
    `PROXY_TRUSTED_PROXIES=172.30.250.1/32`, redirect kept, HSTS present, 307 → 401 on `/api/auth/me` after trusting
    the gateway. Funnel keeps `Host` on 8443, so the Caddyfile is unchanged (no `header_up Host` from a header —
    good, that fix would have been a new attack surface).
  - **Smoke credentials:** `SMOKE_ENV_FILE` reads the admin from the stack's own env file; with
    `BOOTSTRAP_ADMIN_PASSWORD` now blank, `task-025.ps1` can no longer run against production by accident. Correct
    side effect of E7 — the runbook already says production smoke runs only by task.
  - **Storage refusals** (review 025 P2): unsigned `GET`, listing (plain and `list-type=2`) and `PUT` refused; the
    key is absent afterwards, verified through the API's own confirm. Closed.
- **Verification** (not re-run by Cowork): gitleaks 197 commits, 6 triaged findings; variable check 69 assignments;
  actionlint clean; CI green (push and arm64 dispatch); `task-025.ps1` 51/51 on `omni025b` and live; `task-025b.ps1`
  12/12 live; backup → restore → identical counts on throwaway stacks, second restore refused, retention removed 31 of 44
  fabricated folders as specified; phone check over mobile data (login, order, image upload / display, hub 101); reboot without
  manual steps (62 s to containers, 112 s to a login from the internet).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | gitleaks found 6 lines; the user decided instead of a `Blocked` report | Accepted — all six are placeholders / test fixtures (triage by file, commit, rule; no value shown), the user is the decider and was present; a `Blocked` round-trip through Cowork would have produced the same answer. History not rewritten — correct. |
| 2 | E4 / E5 skipped by the user | **Not accepted as final** — question 1, task 025d. |
| 3 | `backup-prod.ps1` has no `-EnvFile` | Accepted — it finds the containers by compose labels and needs no secret; better than the task. |
| 4 | Fallback excludes `/assets/` | Accepted — see "Checked". |
| 5 | Image label `org.opencontainers.image.revision` from `GIT_COMMIT` | Accepted — the only reliable way for the manifest to name the running commit. Note: an image built without `GIT_COMMIT` says `unknown`; the runbook's release step sets it. |
| 6 | Retention keeps every backup of the 7 most recent days | Accepted — a pre-release backup on the same day must not evict the nightly one. |
| 7 | Vercel project name `…-front`; `production` created by the user | Accepted — the branch points at the commit the stack runs (checked). |
| 8 | E3 browser check by the user, no screenshots | Accepted — Claude Code must not type a production password; the access log corroborates each step. |
| 9 | Bootstrap password blanked without a backup | Accepted — one env line, no data touched. |
| 10 | New commits use the GitHub no-reply address | Accepted (user's choice in B1). |

## Questions of the report
1. **Production without backups → not accepted; task 025d before 026.** Reasons:
   - `docs/tasks/README.md` "Production steps" (written in this task) requires a backup before every release,
     migration, restore or rotation. Without a working backup, the first release — 026 changes the API and the
     proxy — is blocked by our own rule, or the rule gets broken on day one.
   - The data lives in Docker Desktop's WSL2 virtual disk. "Reset to factory defaults", "Clean / Purge data", an
     uninstall, a corrupted virtual disk or a disk failure removes every volume at once; there is no second copy.
   - The scripts were never run against `omnichannel-prod` (its project name, two Workers, a database that has been
     used through Funnel) and the scheduled start was never run at all. A backup that has not been restored is a hope,
     not a backup.
   Cost is small: one destination choice, one confirmation, about 30 minutes mostly waiting. 025d also gives the
   manifest row counts so the drill compares automatically, and records in the log that dumps are not encrypted (the
   destination is the user's private cloud drive — decision below).
   If the user still does not want an off-machine copy, 025d allows a local destination as an explicit, recorded
   interim (better than nothing, protects against Docker data loss, not against losing the disk or the machine).
2. **`Tailscale-User-*` headers → strip in 026, at the proxy.** They appear only on the tailnet path (the user's own
   devices) and the API ignores them, so this is personal data in a local log, not a trust problem — P3, no reason
   for a release of its own. 026: `header_up` removal of `Tailscale-User-Login`, `-Name`, `-Profile-Pic` on both sites
   (verify whether Caddy's deletion accepts a `Tailscale-User-*` wildcard in the pinned version; otherwise the three
   names), and delete them in the access-log filter as well. `Tailscale-Funnel-Request` may stay (no personal data).
   Note for 026: nothing in the API may ever trust these headers for identity — anything on the host can send them.
3. **Secret rotation not exercised → drill in 025d, on a throwaway stack, by Claude Code alone.** Every row of
   runbook section 9 except the bootstrap password (done live) is run on `omni025d-rot` with a functional check after
   each; rows that do not work as written are fixed in the runbook. One known gap to solve there: the database-password
   rows use `\password` at an interactive prompt, which Claude Code cannot type and which is the step most likely to
   be done wrong under pressure. Production itself is **not** rotated (no reason to).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P1 | Production holds state and has no backup; the scheduled backup start was never run anywhere. | **025d** Parts B–C. |
| P2 | Rotation procedures (6 rows of runbook §9) are unexercised; the database rows need an interactive prompt. | **025d** Part D (drill on a throwaway stack, non-interactive variant that keeps the value off command lines and out of the PostgreSQL log). |
| P3 | Restore drills compare counts by hand: the manifest has no row counts. | **025d** A1: counts in the manifest, compared by `restore-prod.ps1`. |
| P3 | Dump and storage archive are taken seconds apart: an image confirmed in between is an orphan after a restore, an image removed in between is a missing object for a product row. | Accepted (seconds, nightly at 03:00). **025d** A2: one line in runbook §6. |
| P3 | A failed nightly backup is visible only in `backup.log`. | Accepted for the demo. 025d: the weekly look at `backup.log` and the Task Scheduler's last result in runbook §6; automatic notice belongs with the uptime-monitor follow-up. |
| P3 | `Tailscale-User-*` forwarded to the API and logged. | **026** (question 2). |
| P3 | Handoff says "pushed", but `f66ee26` is one commit ahead of `origin/master`. | Pushed with 025d's first commit; no action otherwise. |
| — | 026 has grown to more than twenty items across API, proxy, containers, frontend and database. | 026 will be **split** when written (026, 026b, …); the roadmap row says so. |

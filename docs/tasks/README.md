# Task handoff: Cowork ⇄ Claude Code

Planning/review happens in Claude **Cowork**; implementation happens in **Claude Code** (desktop Code tab or VS Code).
This folder is the shared mailbox between them. Git history is the audit trail.

## Files

| File | Written by | Purpose |
| --- | --- | --- |
| `NNN-short-name.md` | Cowork | Task spec: goal, scope, steps, acceptance criteria |
| `NNN-short-name.report.md` | Claude Code | What was done, files changed, **real** build/test output, open issues |
| `NNN-short-name.review.md` | Cowork (optional) | Review findings; may lead to a follow-up task |

`NNN` is a zero-padded sequence (`001`, `002`, ...); a task split after planning gets a letter suffix (`016b`, ordered
after `016` and before `017`). A task is **open** while it has no `.report.md`.

## Cycle

1. **Cowork:** "plan step X" → writes `NNN-*.md`.
2. **Claude Code:** `/next-task` (or `/next-task 004`) → implements the lowest-numbered open task, commits, writes `NNN-*.report.md`.
3. **Cowork:** "review task NNN" → reads report + diff, updates `docs/implementation-log.md`, writes the next task.

## Rules for every task

- Scope is exactly what the task file says. Anything else goes under "Out of scope / follow-ups" in the report.
- Architectural or high-risk decisions not already settled in the task or in `docs/implementation-log.md`: **stop**, write the question in the report with status `Blocked`, do not guess.
- Report only commands that were actually executed, with their real output (trim long logs to the relevant part).
- Never read, print or commit secrets (`docker/.env`, user-secrets, connection strings with passwords).
- Development steps (migrations, storage keys, changed containers) are applied by Claude Code itself — see "Development steps" below.

## Report template

```markdown
# Report NNN — <title>

Status: Done | Partially done | Blocked
Commit: <hash> (or "not committed" + reason)

## Summary
<2-5 lines>

## Files changed
- path — why

## Verification (actually executed)
| Command | Result |
| --- | --- |
| dotnet build | 0 errors, N warnings |
| dotnet test | X passed, Y failed |

<relevant output excerpts>

## Deviations from the task
<none / list with reason>

## Blocked / questions for Cowork
<none / list>

## Development steps (applied)
<each step run on development: command (no secret), result>

## Manual steps for the user
<none / only what Claude Code could not apply, with the reason>
```

## Development steps
Decision of the user (2026-10-05): after a task is verified, **Claude Code applies what the development environment needs**
instead of listing it for the user, then runs the task's smoke script on development.

Allowed, in this order, only when the task's build and tests are green:
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` when the task changed it or the storage keys
   (idempotent, prints no key).
2. `docker compose -f docker/docker-compose.yml up -d <service>` for services whose definition changed (containers are
   recreated, volumes kept). Never `down -v`, never removing a volume.
3. Migrations, as the migrator, against the development database only:
   - the connection comes from the user-level environment variable `OMNICHANNEL_MIGRATOR_CONNECTION`; it is never
     printed, echoed, logged, written to a file or put on a command line; if it is missing, the step goes to
     "Manual steps for the user" with that reason;
   - before applying: `dotnet ef migrations list` — the pending migrations must be exactly those committed by the task
     (or an earlier task still listed as pending); anything else → stop, `Blocked`;
   - the migration was applied on a throwaway environment first (smoke run), as before;
   - **additive only**: a migration that drops or renames a table or column, narrows a type, or deletes / rewrites rows
     is not applied — stop and ask (`Blocked`), the user decides (backup first);
   - never `database drop`, never `database update <earlier migration>`, never `migrations remove` of an applied migration.
4. Hosts: API and Workers processes started from this repository are stopped before building (they lock `bin/Debug`);
   none is left running afterwards (a running host hangs the calling tool). The smoke script starts and stops its own.
5. The task's smoke script on development; the table in the report.

A step that fails is not retried blindly and not worked around: its output (without secrets) goes into the report and the
step into "Manual steps for the user". The rule "never read, print or commit secrets" is unchanged: the scripts read
`docker/.env` and the user-secrets themselves; Claude Code does not open them.

One-time setup by the user (outside this repository): the persistent user environment variable
`OMNICHANNEL_MIGRATOR_CONNECTION`, then a restart of the editor / Claude app so Claude Code inherits it.

## Moving to a fresh chat
Long chats cost tokens. The repository documents are the memory, so a new chat loses nothing:
- In Claude Code, `/handoff` writes and commits `docs/handoff.md` (state snapshot, pending steps, next action, resume prompts; never secrets).
- In a new Claude Code chat, `/resume` reads the handoff and the roadmap and summarises where things stand.
- In a new Cowork chat, paste the Cowork resume prompt printed by `/handoff`.

## Smoke scripts instead of typed manual checks
Every task whose acceptance criteria include a manual check also delivers `scripts/smoke/task-NNN.ps1` (Windows PowerShell 5.1
compatible, ASCII only), built on `scripts/smoke/_common.ps1`:
- reads the admin credentials from `backend/src/Omnichannel.Api/.env` (git-ignored) and never prints secrets; test users get random passwords;
- uses a running API or builds and starts it, and stops what it started;
- records each check with `Assert-Step` and ends with `Complete-Smoke` (table + exit code 0/1).
Claude Code runs the script on development after applying the development steps and puts the table in the report;
only if that is impossible (a failed step) the user runs
`powershell -ExecutionPolicy Bypass -File scripts\smoke\task-NNN.ps1` and pastes the table. Checks that need Docker control
(stopping PostgreSQL, locking a table) or the Workers host belong in the script too when they can be automated safely.

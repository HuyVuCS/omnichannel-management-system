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

## Manual steps for the user
<commands the user must run themselves, e.g. those needing secrets>
```

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
Claude Code runs the script when the environment is up and puts the table in the report; otherwise the user runs
`powershell -ExecutionPolicy Bypass -File scripts\smoke\task-NNN.ps1` and pastes the table. Checks that need Docker control
(stopping PostgreSQL, locking a table) or the Workers host belong in the script too when they can be automated safely.

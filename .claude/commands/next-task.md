---
description: Implement the next open task from docs/tasks (or the one given as argument) and write its report
argument-hint: "[task number, e.g. 002]"
---

You are the implementation side of the Cowork ⇄ Claude Code workflow described in `docs/tasks/README.md`.

## 1. Pick the task

- If an argument was given (`$ARGUMENTS`), use the task file in `docs/tasks/` whose name starts with that number.
- Otherwise use the **lowest-numbered** `docs/tasks/NNN-*.md` that has no matching `NNN-*.report.md`
  (ignore `README.md`, `*.report.md`, `*.review.md`).
- If there is no open task, say so and stop.

## 2. Understand before changing anything

Read, in this order: `CLAUDE.md`, `docs/tasks/README.md`, `docs/implementation-log.md`, the task file, then the code the task touches.
If the task conflicts with those documents or needs a decision they do not settle, do not guess:
write the report with `Status: Blocked` and the question, then stop.

## 3. Implement

- Stay strictly inside the task scope. Record anything else as a follow-up in the report.
- Follow existing conventions in the codebase. No speculative refactors, no new dependencies unless the task allows them.
- Never read, print or commit secrets (`docker/.env`, user-secrets, passwords in connection strings).

## 4. Verify

Run every verification command the task lists (at minimum `dotnet build` and `dotnet test` in `backend/` for backend work).
Fix failures caused by your changes. Report only what was actually executed, with real results.

## 5. Commit and report

1. `git status` → stage only the files belonging to this task → commit with message `task NNN: <title>`.
2. Write `docs/tasks/NNN-<same-name>.report.md` using the template in `docs/tasks/README.md`, then commit it too
   (a separate commit is fine).
3. Finish with a 3-5 line summary in chat: status, commit hash, test result, anything blocked or needing a manual step.

---
description: Snapshot the current project state into docs/handoff.md so work can continue in a fresh chat (Claude Code or Cowork)
---

Goal: a new chat must be able to continue this project from **one short prompt plus `docs/handoff.md`**, without the old chat
history. The repository documents are the memory; the handoff file is only an index and a snapshot of what is *in flight*.
Keep it short (target ≤ 150 lines): link to files instead of copying them.

## 1. Collect (read-only)
- `git log --oneline -15`, `git status --short`, current branch and HEAD hash.
- `docs/roadmap.md` (task table, open decisions), `docs/implementation-log.md` (decisions — do not copy, reference sections),
  `docs/tasks/README.md`.
- In `docs/tasks/`: the highest-numbered task file and whether it has a `.report.md` and a `.review.md`; the latest report's
  "Blocked / questions" and "Manual steps for the user" sections.
- `backend/src/Omnichannel.Infrastructure/Persistence/Migrations/`: newest migration name (so the user can check it is applied).
- Anything done in **this** chat that is not yet in a committed file (decisions, findings, half-finished work).

## 2. Write `docs/handoff.md` (overwrite) with exactly these sections
1. **Snapshot** — date/time, branch, HEAD hash + subject, uncommitted files (or "clean").
2. **Project in 5 lines** — what the system is; source-of-truth order: `CLAUDE.md` → architecture doc
   (`THIET_KE_KIEN_TRUC_HE_THONG_OMNICHANNEL_AI_CODING_GUIDE.docx`, in the Cowork project) → `docs/implementation-log.md` → `docs/roadmap.md`.
3. **Workflow** — Cowork plans/reviews and writes `docs/tasks/NNN-*.md` + `*.review.md`; Claude Code runs `/next-task`, writes
   `*.report.md`; the user runs steps needing secrets. Link `docs/tasks/README.md`.
4. **Progress** — one line per task from the roadmap: `NNN — title — Done/Review/Open/Planned` (only 001 … next 3 planned).
5. **In flight** — current task number and state: *task file written, not started* / *in progress (what is done, what is left)* /
   *report written, awaiting Cowork review* / *reviewed, next task not written*. Include unfinished work of this chat.
6. **Pending for the user** — manual steps from the latest report not yet confirmed (e.g. `dotnet ef database update` for migration
   `<name>`, manual acceptance check), and open decisions from the roadmap.
7. **Environment** — how to run (3 terminals: API, Workers, scripts; commands in README §5–6), where secrets live **by name only**
   (`docker/.env`, user-secrets of `Omnichannel.Api` and `Omnichannel.Workers`), ports (API 5080, RabbitMQ UI 15672, PostgreSQL 5432).
8. **Gotchas learned** — short bullets with a pointer to the log/review that explains each (e.g. RabbitMQ 4 does not count
   NACK-requeue toward `x-delivery-limit`; inventory rows are locked in `ProductId` order; PowerShell strips `"` in `Invoke-Expression`
   — pipe SQL via stdin; Workers must run in their own terminal).
9. **Next action** — the single next step and who does it (user / Claude Code / Cowork).
10. **Resume prompts** — two fenced blocks the user can paste:
    - *New Claude Code chat:* `/resume` (or, if that command is missing: "Read CLAUDE.md, docs/handoff.md, docs/roadmap.md, then
      continue with the next action in docs/handoff.md.").
    - *New Cowork chat:* "Dự án Management System. Đọc docs/handoff.md, docs/roadmap.md và docs/implementation-log.md trong thư mục
      đã kết nối, rồi tiếp tục vai trò lập kế hoạch/review theo docs/tasks/README.md. Việc tiếp theo: <next action>."

## 3. Rules
- **Never** write secret values (passwords, keys, tokens, connection strings with passwords). Names and file locations only.
- Do not change any file other than `docs/handoff.md`.
- Commit it alone: `git add docs/handoff.md && git commit -m "docs: handoff snapshot"`. If other files are uncommitted, leave them and
  list them in the Snapshot section.
- Finish in chat with: the commit hash and the two resume prompts (copy-paste ready), nothing else.

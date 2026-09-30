---
description: Resume work in a fresh chat from docs/handoff.md and the repository documents
---

You are continuing an existing project in a new chat. Do not assume anything from a previous conversation.

1. Read, in order: `CLAUDE.md`, `docs/handoff.md`, `docs/tasks/README.md`, `docs/roadmap.md`. Read `docs/implementation-log.md`
   sections only when the current task touches them.
2. Check the snapshot is still current: `git log --oneline -5` and `git status --short`. If HEAD moved past the handoff's hash, read the
   commits in between and say what changed.
3. Reply in Vietnamese, at most 12 lines:
   - where the project stands (last done task, current task and its state),
   - pending steps for the user,
   - the next action you propose.
4. Then wait. Do not start implementing until the user confirms, unless the next action is `/next-task` and the user asked to continue.

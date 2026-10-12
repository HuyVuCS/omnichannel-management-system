# Review 025d — Ops: production backups on, restore drill of a production backup, secret-rotation drill

Reviewer: Cowork, 2026-10-12
Verdict: **Accepted** — Parts A and D are done and well verified; Parts B and C are **closed by decision of the user**
("không cần backup", given at B1 and repeated to Cowork on 2026-10-12): production runs without backups, loss of its
data accepted. Review 025b's gate "025d before 026" is withdrawn; `docs/tasks/README.md` "Production steps" is amended
by Cowork (no backup before a production step; the other guardrails unchanged and now the only protection). All
seven deviations accepted; deviation 2 is a real find and becomes 026 Part A (question 3).

## Checked
- Report and commits `285e30d` (scripts, runbook, README), `8fc1e5e` (report). No product code, image, migration or
  package; production untouched (same seven container ids and image ids before / after).
- Read in full: the diffs of `_stack.ps1`, `backup-prod.ps1`, `restore-prod.ps1`; `rotate-secret.ps1`.
- Checked by Cowork against the code:
  - **SCRAM verifier** (`Get-ScramVerifier`): RFC 5802 / PostgreSQL format — PBKDF2-HMAC-SHA256 over the password with
    a CSPRNG 16-byte salt and 4096 iterations (PostgreSQL's default), `StoredKey = SHA256(HMAC(salted, "Client Key"))`,
    `ServerKey = HMAC(salted, "Server Key")`, `SCRAM-SHA-256$iter:salt$stored:server`. Restricting passwords to letters
    and digits makes SASLprep a no-op — correct and stated. The drill proves it end to end (new password logs in, old
    does not). Better than the task's suggestion: the server never sees the password at all.
  - **Statement logging off for the session** (`log_min_error_statement = panic`, `log_statement = 'none'`,
    `log_min_duration_statement = -1`) before `alter role`; verified with a failing statement that the PostgreSQL log
    holds neither the verifier nor the statement. Statement file removed in the container and locally.
  - **Order of the database rows:** role first, file second; if the file write fails the message says so and a re-run
    sets a newer password on both sides — no state where the file holds a password the role never had. Correct.
  - **Webhook rotation:** override file refused inside the repository; the env file is untouched during the overlap;
    `-CompleteFrom` accepts only a file that contains exactly the expected key with a ≥ 32-character value, then
    deletes it. Slug derivation (`DIGITAL_PLATFORM` → `digital-platform`) matches the configuration keys.
  - **Counts:** one statement for eleven tables, a missing table is an error; the restore compares and warns
    (`<-- differs`), old manifests restore with one line — exercised in all three paths, including the warning path.
  - **Helper container** that only `sleep`s and is used through `docker exec`: the storage is counted after it runs
    again, so downtime did not grow (13.3 s); the helper is removed in `finally`.
- **Verification** (not re-run by Cowork): parser / ASCII check; `omni025d` 51/51 → backup → restore into
  `omni025d-r` with identical table (11 tables + 113 entries); old-format manifest; warning path; rotation drill 30/30
  on `omni025d-rot` with smoke 51/51 before and after; no rotated value in any container log; every throwaway
  project removed with `down -v` of its own name only.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | B and C not done — the user's decision at B1 | Accepted — confirmed by the user to Cowork; recorded in the log and `docs/tasks/README.md`. |
| 2 | No `build` for the throwaway stacks: the `:local` image tags are shared by every compose project | Accepted — the right call, and a latent production risk found: any build of a drill re-tags the images production's next `up` would use. Fixed in 026 Part A (question 3). |
| 3 | Storage counted in a waiting helper via `docker exec` | Accepted. |
| 4 | `rotate-secret.ps1` covers every row | Accepted — one generator for user and Claude Code, no value on a command line or in the chat. |
| 5 | Runbook rows corrected (`OMNI_MIGRATOR_PASSWORD` / `POSTGRES_PASSWORD` recreate `postgres`; RabbitMQ quoting) | Accepted — exactly what a drill is for. |
| 6 | §13 without production backup values | Accepted — there are none. |
| 7 | Log not edited | Accepted — Cowork's. |

## Questions of the report
1. **Backup gate → removed (user's decision).** No exception per task: the rule itself changes — production steps no
   longer wait for a backup and no task runs `backup-prod.ps1` against production unless the user asks. Consequences,
   recorded in the log so nobody rediscovers them: a failed release, a mistaken command or a Docker Desktop reset loses
   all production data; rollback after a migration is fix-forward only; the restore sections of the runbook apply to
   drills only. The guardrails (no `down -v`, no volume removal, no `-Force` over the env file, additive migrations
   only) are now the only protection and stay absolute. The scripts stay in the repository (one command if the user
   changes their mind). Runbook wording is adjusted in 026 Part A.
2. **Unproven scheduled start → accepted, moot.** No scheduled task is planned. Runbook §6 says "not exercised"
   (026 A).
3. **Image names per stack → yes, in 026 Part A, with a tag per release.** Proposed: `image:
   omnichannel-api:${IMAGE_TAG:?}` (likewise `-workers`, `-migrate`), `IMAGE_TAG` stored **in the stack's env file**
   (not a shell variable — a forgotten variable must not change which image `up` runs), production = the short commit
   of the release, drill stacks = their own value (e.g. `drill-<project>`). This removes the cross-project hazard and
   makes a backend rollback without a migration a tag change instead of a rebuild (old images stay until pruned).
   `${COMPOSE_PROJECT_NAME}` was the report's idea; a commit tag solves the same hazard and adds rollback.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Shared `:local` image tags across compose projects (deviation 2): a drill or experiment that builds re-points production's next `up`. | **026 A1** (`IMAGE_TAG` in the env file). |
| P3 | Runbook still says "Backup first" / "Stop here when it fails" in §4, §5, §9, "Updating a third-party image". | **026 A2**. |
| P3 | `rotate-secret.ps1` prints "Backup first" in its header. | **026 A2** (comment only). |
| — | No production backup exists; data loss accepted by the user. | Recorded (log, `docs/tasks/README.md`). |

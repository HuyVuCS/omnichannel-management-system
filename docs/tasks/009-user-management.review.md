# Review 009 — User management, token revocation

Reviewer: Cowork, 2026-10-01
Verdict: **Accepted** — `scripts/smoke/task-009.ps1` run by the user: all checks passed (2026-10-01)

## Checked
- Report, commit `567e3b0`; API table, enforcement of `last_admin` / `cannot_modify_self` / duplicate e-mail, mutation results.
- Revocation: `sstamp` claim, `OnTokenValidated` check with a per-user cache, eviction on the instance that changes the user;
  proven by mutations (no stamp compare → 5 failures; no eviction; no cache → other-instance test fails).
- `last_admin` serialised by `FOR UPDATE` on the Admin role row (mutation: both concurrency tests fail without it).
- Audit rows in the same transaction as the change; no password or e-mail in audit payloads or captured logs (mutation-proven).
- 428/428 tests (288 unit, 140 integration), 3 runs green. The honest note about the stale-binary mutation run is appreciated.

## Deviations — accepted
All eight. `newPassword` as the error key follows the JSON-path convention; idempotent no-ops are fine; existing tokens are
invalidated once at deployment (everyone logs in again).

## Decision on the question raised (user, 2026-10-01): option A
Temporary lockout after failed logins must **not** revoke active sessions (otherwise anyone who knows an e-mail can repeatedly kick a
user out). The token check and `GET /api/auth/me` rely on the security stamp only; admin lock, role change and password reset still
revoke at once because they change the stamp. Implemented in task 010 Part A.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Lockout check in token validation enables session-kicking by failed logins (raised by the implementer). | Task 010 Part A (option A) |
| P3 | Failed-login lockout itself can still be triggered by anyone knowing an e-mail (blocks new logins for 15 min). | Hardening task 026: consider per-account+IP throttling instead of plain lockout |

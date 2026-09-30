# Task 009 — User management (Admin), immediate revocation of old tokens

Status: Open
Written by: Cowork, 2026-09-30
Depends on: 008 (done)

## Context
Only the bootstrap admin exists. Doc §6: Identity + JWT + RBAC (Admin / Manager / Staff), audit of important operations. The frontend
(010+) needs Staff/Manager accounts. Access tokens are stateless for 60 minutes, so a locked user or a changed role would otherwise keep
the old rights until expiry — this task closes that gap.

## Part A — Token revocation via security stamp
- Add claim `sstamp` (the user's Identity `SecurityStamp`) to issued access tokens.
- In `JwtBearerEvents.OnTokenValidated`: load the user's current `SecurityStamp` and `LockoutEnd` (one small query) and reject the token
  (`context.Fail`) if the user no longer exists, the stamp differs, or the user is locked out. Cache the lookup per user id in
  `IMemoryCache` for 30 s (in-process only; no new infrastructure). Document "revocation within ≤ 30 s".
- Every operation below that changes rights calls `UserManager.UpdateSecurityStampAsync` (and evicts the cache entry locally).
- `GET /api/auth/me` keeps reading from the store.

## Part B — Endpoints (`/api/users`, all **AdminOnly**)
Contracts/validators in `Application/Users`, implementation in `Infrastructure/Identity`, controller `UsersController`.
Each user has **exactly one** role (`Admin`, `Manager`, `Staff`).

| Method | Route | Behaviour |
| --- | --- | --- |
| GET | `/api/users` | Paged (existing contract). Filters: `search` (email, ILIKE escaped), `role`, `locked` (bool). Item: `userId, email, role, isLocked, lockoutEnd, accessFailedCount`. |
| GET | `/api/users/{id}` | Same shape; 404 `user_not_found`. |
| POST | `/api/users` | `{email, password, role}` → 201. Identity password policy errors → 400 with key `password` (messages from Identity, never echo the password). Duplicate email → 409 `user_email_exists` (race-safe: unique index / Identity result, no check-then-insert only). User + role in one transaction. |
| PUT | `/api/users/{id}/role` | `{role}`; new security stamp. |
| POST | `/api/users/{id}/lock` | Locks indefinitely (`LockoutEnd = DateTimeOffset.MaxValue`); new security stamp. |
| POST | `/api/users/{id}/unlock` | Clears lockout and `AccessFailedCount`. |
| POST | `/api/users/{id}/reset-password` | `{newPassword}` — admin sets a temporary password (no e-mail infrastructure yet): remove + add password in one transaction, new security stamp. Policy errors → 400 `password`. |

Guards (409): `last_admin` — no operation may leave zero unlocked Admins (demote or lock the last one); `cannot_modify_self` — an admin
cannot change their own role or lock themselves (they can still reset their own password). Evaluate `last_admin` under a lock or
serialisable check so two concurrent demotions cannot both pass.

## Part C — Audit
`SystemEventLog` Information: `user.created`, `user.role_changed` (`from`, `to`), `user.locked`, `user.unlocked`, `user.password_reset`,
each with `targetUserId` and `actorUserId` (JWT `sub`) and the request's correlation id. **Never** log or store passwords; do not put
e-mail addresses in logs (ids only).

## Tests (required)
Integration:
1. Admin creates one user per role → 201; each can log in and `/api/auth/me` shows the role.
2. Manager and Staff → 403 on every `/api/users` endpoint; anonymous → 401.
3. Duplicate e-mail (differing only in case) → 409; weak password → 400 with key `password`; unknown role → 400.
4. **Revocation:** a Staff token works; admin changes the role to Manager → the **old** token is rejected (401) within the cache window
   (use a short configurable window in tests); a new login carries `Manager`.
5. Lock → login 401 and the existing token 401; unlock → login works again.
6. Reset password → old password fails, new one works, tokens issued before the reset are rejected.
7. Guards: demoting/locking the last admin → 409 `last_admin`; admin changing own role / locking self → 409 `cannot_modify_self`;
   two concurrent demotions of the only two admins → exactly one succeeds.
8. Audit events written with actor and target ids; captured logs and SystemEventLog payloads contain no password.
Unit: validators; role mapping.

## Acceptance criteria
- [ ] Build 0 warnings; all tests pass (unit/integration counts). Stop the user's running API/Workers or use `--artifacts-path` as in 008.
- [ ] No migration expected (Identity tables already exist); if one is needed, explain why and include its SQL.
- [ ] Manual check documented: create a Staff user via the API, log in as that user, then lock it and see the old token rejected.

## Out of scope
Self-service password change and e-mail flows (no e-mail infrastructure), user deletion, refresh tokens, frontend pages.

# Report 009 — User management (Admin), immediate revocation of old tokens

Status: Done
Commit: `567e3b0`

## Summary
- **Part A:** access tokens now carry the user's security stamp (`sstamp`). After the normal JWT validation, the API rejects a
  token when the user no longer exists, the stamp has changed, or the user is locked out. The lookup is cached per user for
  `TokenRevocation:CacheDuration` (default 30 s). The instance that makes a change evicts the entry, so revocation is immediate
  there and takes at most 30 s on other instances.
- **Part B:** `/api/users` (Admin only): list, get, create, change role, lock, unlock, reset password. Guards `last_admin` and
  `cannot_modify_self`.
- **Part C:** audit events `user.created`, `user.role_changed`, `user.locked`, `user.unlocked`, `user.password_reset`.
- No migration. Build: 0 warnings. Tests: **428/428** (288 unit, 140 integration), 3 consecutive full runs green.
- One security point needs a decision from Cowork (see "Blocked / questions"). It does not block the task.

## Files changed
**Part A**
- `Application/Auth/IAccessTokenIssuer.cs` — `Issue(..., securityStamp)`.
- `Api/Auth/JwtAccessTokenIssuer.cs`, `JwtClaimNames.cs` — claim `sstamp`; a token without a stamp is never issued.
- `Application/Auth/IUserSecurityStateProvider.cs` (new) — `UserSecurityState(SecurityStamp, LockoutEnd)`, `GetAsync`, `Evict`.
- `Infrastructure/Identity/UserSecurityStateProvider.cs` (new) — one query (`SecurityStamp`, `LockoutEnabled`, `LockoutEnd`),
  cached in `IMemoryCache`. "User not found" is cached too.
- `Infrastructure/Identity/TokenRevocationOptions.cs` (new) — `CacheDuration`, 0 to 5 min, validated at start-up.
  `Api/appsettings.json` has the new section.
- `Api/Auth/TokenRevocation.cs` (new) — the `OnTokenValidated` check. `AuthServiceCollectionExtensions.cs` wires it in.
- `Infrastructure/Identity/IdentityAuthService.cs` — login passes the stamp to the issuer.

**Parts B and C**
- `Application/Users/UserContracts.cs`, `IUserService.cs`, `UserValidators.cs` (new) — requests, `UserResponse`,
  `UserListQuery`, error codes, event names, validators.
- `Application/Auth/AppRoles.cs` — `TryNormalize` (role names are accepted in any casing).
- `Infrastructure/Identity/IdentityUserService.cs` (new) — the implementation.
- `Api/Controllers/UsersController.cs` (new).
- `Infrastructure/DependencyInjection.cs` — registrations.
- `README.md` — section "Users and token revocation".

**Tests**
- New: `Integration/UserManagementTests.cs`, `Integration/UserGuardTests.cs`, `Auth/TokenRevocationTests.cs`,
  `Validation/UserValidatorTests.cs`.
- Changed: `Integration/ApiFactory.cs`, `Integration/ManualRetryEndToEndTests.cs`, `Auth/JwtAccessTokenIssuerTests.cs`.

### API (as implemented)
| Method | Route | Notes |
| --- | --- | --- |
| GET | `/api/users` | Paging contract. `search` (e-mail, ILIKE escaped), `role` (any casing), `locked`. `sortBy`: `email` only. |
| GET | `/api/users/{id}` | 404 `user_not_found`. |
| POST | `/api/users` | 201 with `Location`. 400 key `password` for policy errors (Identity's messages), `email`, `role`. 409 `user_email_exists`. |
| PUT | `/api/users/{id}/role` | New stamp. Setting the same role again changes nothing (no new stamp, no event). |
| POST | `/api/users/{id}/lock` | `LockoutEnd = DateTimeOffset.MaxValue`, new stamp. Locking a locked user changes nothing. |
| POST | `/api/users/{id}/unlock` | Clears `LockoutEnd` and `AccessFailedCount`. The stamp is kept. |
| POST | `/api/users/{id}/reset-password` | Remove + add in one transaction. 400 key `newPassword` for policy errors. |

All return `UserResponse` (`userId, email, role, isLocked, lockoutEnd, accessFailedCount`). All require `AdminOnly`.

### How the rules are enforced
- **Duplicate e-mail.** Identity's own check catches the normal case. For two requests at the same time, the unique index
  `UserNameIndex` decides and the service maps the violation to 409.
- **`last_admin`.** Role change and lock first take a row lock on the Admin role (`SELECT ... FOR UPDATE` on `AspNetRoles`).
  They then count the other unlocked Admins. Two concurrent operations therefore run one after the other, and the second
  sees the result of the first. An Admin who is already locked does not count.
- **`cannot_modify_self`.** Checked against the JWT `sub` before anything else. Resetting one's own password is allowed.
- **Every write** runs in one transaction with its audit row. Failed operations leave no audit row.

## Verification (actually executed)
All commands ran from `backend/`, SDK 10.0.401, Docker 29.8.1. No API or Workers process was running, so the normal
`bin/Debug` output was used.

| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, 0 warnings |
| `dotnet test --no-build` (3 consecutive full runs) | Failed 0, Passed 428, Total 428 (×3; 1 m 12 s / 1 m / 1 m 2 s) |
| `dotnet test --no-build --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 288, Total 288 |
| `dotnet test --no-build --filter "Category=Integration"` | **Integration:** Failed 0, Passed 140, Total 140 |
| `dotnet ef migrations has-pending-model-changes -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api --configuration Release` | `No changes have been made to the model since the last migration.` |

### Mutation checks (each one reverted; files confirmed identical by checksum, then rebuilt)
| Mutation | Result |
| --- | --- |
| `FOR UPDATE` removed from the Admin-role lock | Both concurrency tests **failed** (lock variant and demote variant) |
| Stamp not compared in the token check | 5 tests **failed** (role change, other-instance change, lock, own password reset, password reset) |
| No eviction after a role change | `Role_change_rejects_the_old_token...` **failed** |
| Security state never cached | `Change_made_by_another_instance...` **failed** |
| E-mail address added to the "user created" log line | `Every_operation_writes_an_audit_event...` **failed** |
| Lockout ignored in the token check | First run: **no test failed**. I added `Lockout_after_failed_logins...` and `TokenRevocationTests`; second run: 3 tests **failed** |

The first mutation run had a mistake of mine: restoring a file kept its old timestamp, so the next build did not recompile and
one run used the previous mutation's binary. I fixed the script and repeated the affected runs. The table shows the repeated
results.

### Required tests → where
| # | Requirement | Test |
| --- | --- | --- |
| 1 | One user per role, login, `/me` shows the role | `UserManagementTests.Admin_creates_a_user_who_can_log_in_with_that_role` (Admin, Manager, Staff) |
| 2 | Manager/Staff 403, anonymous 401, every endpoint | `Only_admins_reach_user_management` (7 endpoints) |
| 3 | Duplicate e-mail in another casing, weak password, unknown role | `Duplicate_email_weak_password_and_unknown_role_are_rejected`. `Concurrent_creates_with_the_same_email_store_one_user` (8 parallel requests: one 201, seven 409, the user has its role). |
| 4 | Role change rejects the old token; new login has the new role | `Role_change_rejects_the_old_token_and_a_new_login_carries_the_new_role` (rejected at once). `Change_made_by_another_instance_is_picked_up_within_the_cache_window` (stamp changed directly in the database: still accepted from the cache, then rejected after the window). |
| 5 | Lock and unlock | `Locked_user_cannot_log_in_or_use_the_old_token_until_unlocked`. `Lockout_after_failed_logins_also_rejects_the_existing_token_until_an_admin_unlocks`. |
| 6 | Password reset | `Password_reset_replaces_the_password_and_revokes_tokens` (a rejected weak password changes nothing) |
| 7 | Guards | `UserGuardTests.The_last_unlocked_admin_cannot_be_demoted_or_locked`, `A_locked_admin_does_not_count_as_a_remaining_admin`, `Two_admins_removing_each_other_concurrently_leave_exactly_one` (demote and lock, 5 rounds each). `UserManagementTests.Admin_cannot_change_own_role_or_lock_self_but_can_reset_own_password`. |
| 8 | Audit, nothing sensitive logged | `Every_operation_writes_an_audit_event_and_nothing_logs_passwords_or_emails`: 5 events in order with actor and target ids; no event for the failed operations; no password and no e-mail address in the audit payloads or in anything the API logged. |
| — | List | `Users_are_listed_with_search_role_and_lock_filters` |
| Unit | Validators, role mapping, token check | `UserValidatorTests`, `TokenRevocationTests`, `JwtAccessTokenIssuerTests` |

## Deviations from the task
1. **`last_admin` is tested at service level, not over HTTP.** The shared API test database always has other Admins, so "the last
   admin" cannot be built there, and only one API host can exist per test process. `UserGuardTests` calls `IUserService` with the
   production registrations against its own database on the same PostgreSQL server. The mapping from `ConflictException` to 409 is
   covered by `cannot_modify_self` over HTTP.
2. **Password-reset policy errors use the key `newPassword`, not `password`.** The request field is `newPassword`, and the
   repository convention is that error keys are the JSON paths of the request. Create uses `password` as specified.
3. **The cache window is 2 s in tests**, set through `TokenRevocation:CacheDuration`.
4. **Existing tokens stop working once this is deployed.** They have no `sstamp`, so they cannot be revoked and are rejected.
   Everyone logs in again once.
5. **Idempotent no-ops.** Setting the role a user already has, or locking a user who is already locked by an admin, returns 200
   without a new stamp or audit event.
6. **Password reset does not call `UpdateSecurityStampAsync` separately.** Identity's `AddPasswordAsync` already issues a new
   stamp. The test proves that old tokens are rejected.
7. **`UserResponse.role` can be null** for a user without any role (not reachable through this API). `user.created` also
   records `role`.
8. **Test infrastructure changes:**
   - `ApiFactory` now creates one real user per role, because tokens of non-existent users are rejected.
     `CreateClientAs(role)` issues tokens for those users.
   - The test host has a small middleware that lets a client choose its remote IP. The login rate limit (10 per minute and IP)
     stays as in production; each test login uses its own IP.
   - The API's Serilog output is captured in memory for the "nothing sensitive logged" test.

## Blocked / questions for Cowork
**Rejecting tokens of locked-out users makes a known weakness worse.** The task says to reject a token when the user is locked
out, and I implemented that. But lockout also happens after 5 wrong passwords (15 minutes), and anyone who knows a user's e-mail
address can trigger it. Before this task that only blocked new logins. Now it also ends the user's active session within 30 s,
for 15 minutes, and it can be repeated. The login rate limit (10 per minute and IP) does not prevent it.

An admin lock does not need this check: it issues a new stamp, which already rejects the old tokens.

Options:
- **A (my recommendation):** in the token check, drop the lockout condition and rely on the stamp. Admin locks, role changes and
  password resets still revoke at once. A failed-login lockout then only blocks new logins, as before.
- **B:** keep it as specified and accept the risk.
- **C:** reject only for an admin lock (`LockoutEnd` in the year 9999) and ignore temporary lockouts. Same effect as A with more
  code.

This is a small change in `TokenRevocation.ValidateAsync` plus one test. Nothing else depends on it.

## Out of scope / follow-ups
- `GET /api/auth/me` returns 401 for a locked-out user (existing behaviour). It should follow whatever is decided above.
- The cache is per process. With several API instances, revocation takes up to `CacheDuration` on the others.
- A failed-login lockout does not evict the cache entry, so the token is rejected after up to `CacheDuration` on the same
  instance too.
- Self-service password change, e-mail flows, user deletion and refresh tokens are out of scope, as the task states.

## Manual steps for the user
1. No migration. Restart the API so it runs the new code, then log in again (old tokens are rejected).
2. Manual acceptance check, as Admin:
   - `POST /api/users` with `{ "email": "staff1@example.com", "password": "<12+ characters, digit, upper and lower case>",
     "role": "Staff" }`. Expect 201.
   - `POST /api/auth/login` as that user. Call `GET /api/products` with the token. Expect 200.
   - As Admin: `POST /api/users/{id}/lock`. Expect 200 with `isLocked: true`.
   - Call `GET /api/products` again with the Staff token. Expect 401. Logging in as that user also gives 401.
   - As Admin: `POST /api/users/{id}/unlock`. The user can log in again; the token from before the lock stays rejected.

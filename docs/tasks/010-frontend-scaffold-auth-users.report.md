# Report 010 — Frontend scaffold: auth, layout, bilingual UI, error handling, Users page

Status: Done
Commits: `5689c23` and `7e7e1ae` (Part A, backend), `0b360ec` (Part B, frontend + smoke script + README)

## Summary
- **Part A (backend):** a temporary lockout after failed logins no longer ends active sessions; the token check and
  `GET /api/auth/me` rely on the security stamp only. Validation messages follow `Accept-Language` (`vi` / `en`, default `en`).
- **Part B (frontend):** `frontend/` now has the scaffold every later page copies, plus the Users page as the first real CRUD
  page. Login, session handling, role guards, layout with the full menu, Vietnamese/English UI, ProblemDetails handling.
- **Checked against the real API in a browser.** That found three problems the mocked tests could not see; all three are fixed
  (see "Real-browser check").
- Backend: build 0 warnings, **433/433** tests (284 unit, 149 integration), 3 consecutive full runs green. No migration.
- Frontend: `typecheck`, `lint`, `test` (**42/42** in 4 files) and `build` all pass.
- `scripts/smoke/task-010.ps1`: all 17 checks passed.

## Part A — what changed
- `Api/Auth/TokenRevocation.cs`: a token is rejected when the user no longer exists or the stamp differs. Lockout is no longer a
  reason.
- `Application/Auth/IUserSecurityStateProvider.cs`, `Infrastructure/Identity/UserSecurityStateProvider.cs`: the cached state is
  the stamp only.
- `Infrastructure/Identity/IdentityAuthService.cs`: `GetUserAsync` (`/me`) no longer returns 401 for a locked-out user.
- `Infrastructure/Identity/IdentityUserService.cs`: unlock no longer evicts the cache (the stamp does not change).
- `Api/Program.cs`: `UseRequestLocalization` with UI cultures `en` and `vi`, driven by `Accept-Language` only. The answer
  carries `Content-Language`.
- `Infrastructure/Identity/LocalizedIdentityErrorDescriber.cs` (new, commit `7e7e1ae`): the password-policy messages of ASP.NET
  Identity in Vietnamese when the request asks for `vi`. See "Real-browser check", item 1.
- Tests:
  - `UserManagementTests.Lockout_after_failed_logins_blocks_new_logins_but_keeps_the_existing_session` replaces the 009 test. It
    covers: 5 wrong passwords lock the account; a new login gets 401; the existing token still gets 200 on the API and on `/me`,
    also after the cache window; an admin lock then gives 401.
  - `TokenRevocationTests`: the lockout cases are removed.
  - `RequestLocalisationTests` (new): the same invalid login body is answered in Vietnamese for `vi` and `vi-VN,…`, in English
    for `en`, for an unsupported language and without the header. Keys and `errorCode` are identical. Password-policy messages
    on create and on reset follow the language too.

## Part B — what exists
### Versions (installed)
| Package | Version | Note |
| --- | --- | --- |
| react / react-dom | 19.3.0 | |
| antd | 5.29.3 | v5 as decided (latest is 6.6.5) |
| @ant-design/icons | 5.6.1 | the version antd 5 depends on |
| @ant-design/v5-patch-for-react-19 | 1.0.3 | **extra package**: AntD v5 needs it on React 19 |
| react-router | 7.18.4 | v7 as specified (latest is 8.4.0) |
| @reduxjs/toolkit / react-redux | 2.13.0 / 9.3.0 | |
| i18next / react-i18next | 26.4.2 / 17.0.15 | |
| dayjs | 1.11.23 | |
| vite / @vitejs/plugin-react | 8.3.1 / 6.1.1 | |
| typescript | 6.0.3 | not 7.0.2: typescript-eslint supports TypeScript below 6.1 only |
| vitest / jsdom | 5.0.3 / 30.1.1 | |
| @testing-library/react, /dom, /user-event, /jest-dom | 16.3.3, 10.4.2, 14.6.7, 7.0.1 | |
| msw | 3.0.1 | needs Node 22.12 or later |
| eslint / typescript-eslint | 10.11.0 / 8.71.0 | plus react-hooks 7.1.1, react-refresh 0.5.7, eslint-config-prettier 10.1.8 |
| prettier | 3.9.9 | |

Node: `engines` is `>=22.12.0` and `.nvmrc` is `24`. Installed here: Node 24.16.0, npm 11.13.0.

### Structure
```
frontend/src/
  app/       store.ts, router.tsx, providers.tsx, App.tsx, AppLayout.tsx, navigation.tsx
  shared/    api/ (baseApi, types, problemDetails, apiErrorBus)
             components/ (PageHeader, ComingSoon, LanguageSwitch, FieldHint, ApiErrorNotifier, layout constants)
             i18n/ (index.ts, vi.json, en.json)   hooks/   utils/format.ts
  features/  auth/ (authSlice, session, authApi, LoginPage, RequireAuth, RequireRole, SessionManager, returnUrl)
             users/ (usersApi, UsersPage, components/)
  pages/     DashboardPage (placeholder), ForbiddenPage, NotFoundPage
  test/      setup.ts, server.ts (MSW), renderApp.tsx
```

### How the main requirements are met
- **Server state** is RTK Query only (`baseApi.injectEndpoints`, tag `User`). Redux holds the session and nothing else.
- **Auth.** The session is kept in `sessionStorage`. On start it is checked with `GET /api/auth/me`. Any 401 on an authenticated
  call signs the user out, and so does the expiry timer. The login page then opens with `returnUrl`. Only paths inside the app
  are accepted as `returnUrl`.
- **Every request** carries `Authorization`, `Accept-Language` (the UI language) and a new `X-Correlation-Id`.
- **Layout.** Collapsible `Sider` with all 13 menu items, filtered by role. Pages of later tasks show "coming in task NNN".
  Header: e-mail, role, `VI | EN` switch, logout.
- **Language.** Vietnamese by default, stored in `localStorage`, applied to i18next, the AntD locale and dayjs. Money is always
  VND with Vietnamese grouping; date-times are always `DD/MM/YYYY HH:mm`.
- **Language switch changes text only.** All widths and heights are constants in `shared/components/layout.ts`. Longer text is
  truncated with a tooltip. The list state of the Users page lives in the query string. Nothing is keyed by language, so
  nothing remounts and nothing is refetched.
- **Errors.** `applyProblemToForm` puts validation errors on their fields (JSON path → AntD field path) and returns the rest for
  a form-level alert. `errorCode` is translated under `errors.<code>`. Network errors and 5xx show a notification with the
  correlation id (copyable).
- **Users page.** Server-side paging and sorting, debounced search, role and status filters, create, change role, lock/unlock
  with confirmation, reset password.

## Real-browser check (dev server + real API)
I ran the API and `npm run dev` and used the app in a browser: wrong password, login, reload, Users page, create with a weak
password, create, lock, language switch with measurements. For this I created two temporary accounts through the API
(`ui-check-61563@example.com`, Admin, and `ui-check-staff-1@example.com`, Staff). Both are **locked** again; they remain in the
development database, like the `smoke-*` users.

It found three problems, all fixed:
1. **Password-policy messages were English in the Vietnamese UI.** They come from ASP.NET Identity, not FluentValidation, so
   Part A as specified did not cover them. Fixed on the backend (`LocalizedIdentityErrorDescriber`, with a test).
2. **The create dialog changed height when the language changed** (388 px → 432 px): the English hint under the password field
   wrapped to two lines. Fixed with `FieldHint`, which reserves a fixed number of lines and truncates with a tooltip.
3. **A failed create refetched the user list.** RTK Query invalidates tags for rejected mutations too. The Users endpoints now
   invalidate only on success (asserted in the 409 test).

After the fixes, at 1280×800, these were measured in VI, then EN, then VI again and were identical: `Sider` 240 px, header
56 px, user block 320 px, create button 176×32, table columns 333/167/167/167/158, open create dialog 480×408, hint 432×44.
The DOM nodes of the table and of the dialog's e-mail input were the same objects before and after, and the typed value was
kept. After a reload the language stayed English and the session was still valid.

## Verification (actually executed)
Backend from `backend/`, frontend from `frontend/`. SDK 10.0.401, Docker 29.8.1.

| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, 0 warnings |
| `dotnet test --no-build` (3 consecutive full runs) | Failed 0, Passed 433, Total 433 (×3) |
| `dotnet test --no-build --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 284, Total 284 |
| `dotnet test --no-build --filter "Category=Integration"` | **Integration:** Failed 0, Passed 149, Total 149 |
| `dotnet ef migrations has-pending-model-changes … --configuration Release` | `No changes have been made to the model since the last migration.` |
| `npm run typecheck` | exit 0, no output |
| `npm run lint` | ESLint: no findings; `All matched files use Prettier code style!` |
| `npm test` | `Test Files 4 passed (4)`, `Tests 42 passed (42)`, 32 s |
| `npm run build` | `3068 modules transformed`; `dist/assets/index-*.js 1,368.89 kB │ gzip: 431.08 kB`; built in 1.12 s |

### Smoke script (`scripts/smoke/task-010.ps1`, run by Claude Code)
```
Step                                  Expected Actual   OK
----                                  -------- ------   --
01 admin creates Staff user                201    201 True
02 Staff token works                       200    200 True
03 five wrong passwords are rejected         5      5 True
04 correct password while locked out       401    401 True
05 user shown as locked                   True   True True
06 existing session still works (API)      200    200 True
07 existing session still works (/me)      200    200 True
08 admin unlocks user                      200    200 True
09 session still works after unlock        200    200 True
10 admin locks user                        200    200 True
11 session after admin lock                401    401 True
12 /me after admin lock                    401    401 True
13 Accept-Language vi                       vi     vi True
14 Accept-Language en                       en     en True
15 unsupported language falls back          en     en True
17 npm run build                             0      0 True
18 npm test                                  0      0 True

PASSED: all 17 checks.
```
Step 16 (`npm ci`) only runs when `frontend/node_modules` is missing. The script uses 8 of the 10 logins allowed per minute and
IP, so wait a minute after another smoke run.

### Frontend mutation checks (each restored byte for byte, checked by checksum)
| Mutation | Result |
| --- | --- |
| AntD `App` keyed by language (remount) | Test 7b **failed** |
| `Sider` width depends on the language | Test 7b **failed** |
| List query arguments depend on the language (refetch) | Test 7b **failed** |
| One key removed from `vi.json` | Key-parity test **failed** |
| Hard-coded text in a component | Hard-coded-text test **failed** |
| No logout on 401 | Two session tests **failed** |
| No expiry timer | Expiry test **failed** |
| `//host` accepted as `returnUrl` | Open-redirect test **failed** |
| No `Accept-Language` header | Language test **failed** |
| Users menu item without a role | Staff test **failed** |
| Role guard lets any signed-in user through | Staff test **failed** |

### Required tests → where
| # | Requirement | Test (file) |
| --- | --- | --- |
| 1 | Login success, wrong password, 429 | `auth.test.tsx` › login (4 tests, including API validation errors on the fields) |
| 2 | `/users` unauthenticated → login with `returnUrl` → back | `auth.test.tsx` › route guards |
| 3 | Staff: no Users item, 403 on `/users` | `auth.test.tsx` › route guards |
| 4 | 401 logs out; expired `expiresAt` logs out | `auth.test.tsx` › session end (401 from an endpoint, revoked on start, expired in storage, expiring while open) |
| 5 | Validation errors on the right fields, nested path, unknown keys | `problemDetails.test.tsx` |
| 6 | 500 → notification with the correlation id | `users.test.tsx` › unexpected errors (500, and a network failure showing the id the client sent) |
| 7 | Language switch: texts, AntD locale, `Accept-Language`, stored choice | `users.test.tsx` › language switch (first test) |
| 7b | Language switch keeps the UI | `users.test.tsx` › language switch (second test): address, request count, DOM node identity, table page/sort/rows, dialog values, inline widths and classes — compared before, after EN, after VI |
| 8 | Identical key sets | `i18n.test.ts` |
| 9 | Users list, create, 409 on e-mail, lock | `users.test.tsx` › users page (8 tests) |
| — | Backend Part A | see "Part A" |

## Deviations from the task
1. **Node `>=22.12`** instead of `>=20.19`: msw 3 requires it.
2. **TypeScript 6.0.3**, antd 5, react-router 7 instead of the latest majors, for the reasons in the versions table.
3. **One extra package**, `@ant-design/v5-patch-for-react-19`.
4. **Identity password messages localised** (Part A extension), because the real check showed English messages in the
   Vietnamese UI.
5. **Request localisation switches the UI culture only.** The formatting culture stays `en`, so number and date handling on the
   server does not change with the header. Only `Accept-Language` is read (no query string or cookie).
6. **Logout goes to the plain `/login`**, without `returnUrl`: the next person at the browser may be another user.
   A session that ends by itself (401, expiry) does keep `returnUrl`.
7. **`returnUrl` is URL-encoded** (`/login?returnUrl=%2Fusers`). The test compares the decoded value with `/users`.
8. **Test 7, "survives reload":** the test asserts the stored value and the function that reads it at start-up. A real reload
   was checked in the browser.
9. **Tests switch AntD animations off** (an outer `ConfigProvider` in the test helper). Animations never finish in jsdom, so
   closed dialogs would stay in the DOM. Application code is unchanged.
10. **Extra test:** components may not contain hard-coded text (JSX text or text attributes), checked with the TypeScript parser.
11. **Layout shift cannot be measured in jsdom** (no layout engine). Test 7b asserts fixed inline sizes, classes and node
    identity; the pixel measurements are from the real browser.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- **Custom validator messages are still English** (for example the role list message, "'To' must not be earlier than 'From'")
  and so is `detail` in ProblemDetails. The client does not show `detail`; it translates `errorCode`. Messages written with
  `WithMessage` need resource files if they should be Vietnamese.
- **One 1.37 MB JavaScript bundle** (431 kB gzip). Vite warns above 500 kB. Route-level code splitting fits when more pages
  exist, or in the deployment task (025).
- **A confirmation dialog that is already open keeps its language** (its text is computed when it opens). In practice the
  dialog's mask blocks the language switch while it is open.
- **AntD's `vi_VN` locale has gaps** in accessibility labels (the page-size selector is announced as "Page Size").
- `OrderEndToEndTests` could reuse `RabbitMqBrokerFixture` (from 008, still open).

## Manual steps for the user
1. Restart the API so it runs the new code. No migration.
2. Manual check:
   ```powershell
   cd frontend
   npm ci
   npm run dev
   ```
   - Open http://localhost:5173 and sign in as the admin. Switch `VI | EN` in the header: texts change, nothing moves.
   - Open **Người dùng / Users**, create a Staff user. Try a weak password first: the policy messages appear under the password
     field in the current language.
   - In another browser profile (or a private window) sign in as that Staff user: the Users menu item is missing, and
     http://localhost:5173/users shows the "no access" page.

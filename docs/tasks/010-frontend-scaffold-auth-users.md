# Task 010 — Frontend scaffold: auth, layout, bilingual UI, error handling, Users page

Status: Open
Written by: Cowork, 2026-10-01
Depends on: 009 (done)

## Context
First frontend task (doc §3, CLAUDE.md §5). It sets the patterns every later page copies: RTK Query for server state, Redux slices
only for client state, Ant Design tables/forms, ProblemDetails handling, role-based navigation, bilingual text. The Users page
(task 009 API) is included as the first real CRUD page to prove those patterns end to end.

User decisions (2026-10-01): **Ant Design**, access token in **sessionStorage**, **bilingual Vietnamese/English** UI.

## Part A — Backend follow-ups (separate commit)
1. **Lockout (review 009, option A):** `TokenRevocation` no longer rejects tokens because of `LockoutEnd`; it relies on the security
   stamp (and user existence). `GET /api/auth/me` (`IdentityAuthService.GetUserAsync`) no longer returns 401 for a temporarily locked
   user. Admin lock still revokes at once (it changes the stamp). Update/replace the affected tests: failed-login lockout blocks new
   logins but an existing token keeps working; admin lock still rejects the old token.
2. **Request localisation:** `UseRequestLocalization` with cultures `vi` and `en` (default `en`), driven by `Accept-Language`, so
   FluentValidation's built-in messages come back in the requested language. Error **keys** and **errorCode** values stay unchanged
   (English, stable). Test: the same invalid login body returns a Vietnamese message with `Accept-Language: vi`.

## Part B — Frontend (`frontend/`)

### Stack (no others without a reason in the report)
Vite + React + TypeScript (`strict`), Redux Toolkit + RTK Query, `react-router` (v7, library mode), Ant Design v5 + `@ant-design/icons`,
`dayjs`, `i18next` + `react-i18next`. Tests: Vitest + React Testing Library + MSW. ESLint (typescript-eslint, react-hooks) + Prettier.
Use the latest stable versions; record them in the report. npm scripts: `dev`, `build`, `preview`, `typecheck`, `lint`, `test`.
Node version pinned in `package.json` `engines` and `.nvmrc` (Node ≥ 20.19).

### Structure
```
frontend/src/
  app/        store.ts, router.tsx, providers (Redux, AntD ConfigProvider, i18n), App.tsx
  shared/     api/ (baseApi, problem-details helpers), components/ (PageHeader, guards, ErrorResult…), i18n/ (vi.json, en.json),
              hooks/, utils/ (format money/date)
  features/   auth/ (slice, login page, guards), users/ (api endpoints, pages, components)
  pages/      Dashboard (placeholder), NotFound, Forbidden
```
Feature code owns its RTK Query endpoints via `baseApi.injectEndpoints`; tags per resource (`User`, …) for invalidation.

### Auth
- Login page (`/login`): email + password, AntD Form, errors from ProblemDetails; 401 → one generic message; 429 → "too many attempts".
- `authSlice` holds `{ accessToken, expiresAt, user }`, persisted to **sessionStorage** (wrap access in try/catch). On start, hydrate,
  then call `GET /api/auth/me`; 401 → clear and go to `/login`.
- `baseApi` (`fetchBaseQuery`, base URL `/api`): `Authorization: Bearer`, `Accept-Language` = current UI language,
  `X-Correlation-Id` (new UUID per request). On any 401 from an authenticated call → logout + redirect to `/login?returnUrl=…`.
- Auto-logout when `expiresAt` passes (timer); no refresh tokens.
- Guards: `RequireAuth`, `RequireRole(roles)`; 403 page for authenticated users without the role.
- Never log tokens; never put tokens in URLs.

### Layout & navigation
AntD `Layout` with collapsible `Sider` menu, `Header` (user e-mail + role, language switch VI/EN, logout). Menu items for every page in
doc §3.2 (Dashboard, Order entry, Orders, Inventory, Inventory movements, Products, Customers, Channels, Sync & Processing, Alerts,
Logs/Audit, Reports, Users); pages not built yet render a localised "coming in task NNN" placeholder. Items filtered by role
(Users: Admin only). One primary colour (AntD `token.colorPrimary`), status colours only for states (doc §3.4). Responsive down to tablet.

### Internationalisation
- `vi` (default) and `en`; choice stored in `localStorage` (UI preference only), applied to i18next, AntD `ConfigProvider` locale
  (`vi_VN` / `en_US`) and dayjs locale.
- **No hard-coded UI strings** in components; keys grouped by feature. A test fails if `vi.json` and `en.json` key sets differ.
- Formatting helpers: money (VND, `vi-VN` grouping in both languages), date-time in the browser's time zone.
- API `errorCode` values are translated through `errors.<errorCode>` keys with a generic fallback; field messages come from the API
  (localised by Part A).
- **Switching language changes text only — never the interface itself** (user requirement, 2026-10-01). Specifically:
  - no page reload, no navigation, no remount of the page: current route, query string, table page/sort/filters, open modals,
    form values and scroll position are kept;
  - no layout shift: the `Sider` width, header height, menu structure, table column widths, button sizes and icon positions stay the
    same in both languages — design widths for the longer text (usually Vietnamese) and truncate with a tooltip instead of resizing;
  - the AntD locale switch only changes built-in texts (pagination, empty state, date picker labels); date **formats** in our own cells
    come from our formatting helpers and use the same pattern in both languages (e.g. `dd/MM/yyyy HH:mm`); money always VND;
  - the language switch itself is a fixed-width control in the header (e.g. `VI | EN` segmented control), not a select that changes width.

### Error handling
- Helper mapping `ValidationProblemDetails.errors` (camelCase JSON paths, e.g. `items[0].stock`) to AntD `form.setFields`
  (path → `NamePath`), unknown keys shown as a form-level alert.
- Global handling for network errors and 5xx: AntD notification with the translated message **and the `correlationId`** (copyable),
  so a user can report it.

### Users page (Admin) — `/users`
- Table with server-side paging/sorting (`email`), search box (debounced), filters role/locked; columns e-mail, role tag, locked state,
  failed attempts, actions.
- Create user (modal form: e-mail, password, role) — 409 `user_email_exists` and password policy errors shown on the fields.
- Row actions: change role (select), lock / unlock (confirm), reset password (modal). Server errors `last_admin`,
  `cannot_modify_self` shown translated. The table refreshes via tag invalidation.

### Dev setup
Vite dev server on 5173 with proxy `/api` → `http://localhost:5080` (no CORS needed in dev; CORS for 5173 already exists).
README: new section "Frontend" (install, dev, test, build).

## Tests (required)
Frontend (Vitest + RTL + MSW):
1. Login success stores the session (sessionStorage) and lands on the dashboard; wrong password shows the generic error; 429 message.
2. Unauthenticated access to `/users` redirects to `/login?returnUrl=/users`; after login returns there.
3. A Staff user does not see the Users menu item and gets the 403 page on `/users`.
4. Any 401 from the API logs out and redirects; expired `expiresAt` logs out.
5. `ValidationProblemDetails` errors appear on the right form fields (including a nested path) and unknown keys as an alert.
6. 500 response shows a notification containing the correlation id.
7. Language switch changes texts, AntD locale and the `Accept-Language` header; the choice survives reload.
7b. Language switch keeps the UI: on the Users page with page 2, a search term, a sort and an open create-user modal with typed
    values, switching VI ↔ EN keeps the route/query, table state, modal and form values (no remount — assert a component mount
    counter / no refetch of the list), and the `Sider`/header dimensions are unchanged (assert fixed widths/classes).
8. `vi.json` and `en.json` have identical key sets.
9. Users page: list renders paged data, create user happy path + 409 on e-mail field, lock action calls the API and refreshes.
Backend: Part A tests as listed.

## Acceptance criteria
- [ ] `npm run typecheck`, `npm run lint`, `npm test`, `npm run build` all pass (report outputs); backend build 0 warnings and all tests pass.
- [ ] No `any` without a comment; no secrets or tokens in code, logs or URLs.
- [ ] `scripts/smoke/task-010.ps1` (see `docs/tasks/README.md`): runs the backend Part A checks over HTTP (temporary lockout does not
      kill an existing session; admin lock does), plus `npm run build` and `npm test` in `frontend/`. Run it and paste the table.
- [ ] Manual check for the user documented: `npm run dev`, log in as admin in both languages, create a Staff user on the Users page,
      log in as Staff in another browser profile and see that Users is hidden.

## Out of scope
All other pages (011+), SignalR (015/016), charts, CSP/HTTP headers and serving the built app (025/026), cookie-based auth (revisit in 026).

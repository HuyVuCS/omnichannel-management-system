# Review 010 — Frontend scaffold, auth, bilingual UI, Users page

Reviewer: Cowork, 2026-10-01
Verdict: **Accepted** — user's browser check passed (2026-10-01)

## Checked
- Report, commits `5689c23`, `7e7e1ae` (Part A), `0b360ec` (frontend, smoke script, README).
- Part A: token check and `/me` rely on the security stamp only; temporary lockout keeps sessions (smoke steps 03–09), admin lock
  still revokes (10–12). `Accept-Language` localisation incl. Identity password messages (found in the browser, fixed with a test).
- Frontend patterns: RTK Query only for server state, Redux only for the session; `injectEndpoints` + tags; `Authorization`,
  `Accept-Language`, `X-Correlation-Id` on every request; 401/expiry → logout; open-redirect-safe `returnUrl`.
- Language switch changes text only — verified in a real browser with pixel measurements and DOM identity, and guarded by test 7b
  plus three mutations (remount, language-dependent width, language-dependent query args all fail it).
- Errors: JSON-path validation errors mapped to AntD fields; 5xx/network → notification with copyable correlation id.
- Backend 433/433 (284 unit, 149 integration), 3 runs; frontend typecheck, lint, 42/42 tests, build; smoke `task-010.ps1` 17/17.
- Eleven frontend mutation checks all fail the intended tests. The real-browser pass found three defects mocked tests could not
  (English Identity messages, dialog height change, refetch after a failed mutation) — all fixed.

## Deviations — accepted
All eleven. Logout without `returnUrl` (shared computers) and UI-culture-only localisation are the right calls.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Messages written with `WithMessage` (custom rules) and Identity-independent texts are English in the Vietnamese UI. Task 011 adds three forms that use such rules (SKU format, slug format, range checks). | Task 011 Part A: localise all custom validation messages (vi/en) through one mechanism + a completeness test |
| P2 | AntD **v6** is current (6.6.5); v5 on React 19 needs the extra compatibility patch. The cost of migrating grows with every page. | Task 011 Part A: upgrade to AntD 6 now while only the Users page exists, unless the migration turns out broad (then stop and report) |
| P3 | 1.37 MB single bundle (431 kB gzip). | Task 011: route-level code splitting (`React.lazy` per page) |
| P3 | The browser check created two accounts (`ui-check-*`, one of them **Admin**), now locked, in the dev database. | Accept for dev. Rule for future tasks: create test accounts only through smoke scripts (`smoke-*` names), never Admin unless the check needs it |
| P3 | AntD `vi_VN` accessibility label gaps; open confirmation dialog keeps its language. | Accept |

## Manual check
**Passed (user, 2026-10-01):** `npm ci`, `npm run dev`, sign in, switch VI/EN, create a Staff user (weak password first), sign in as Staff in a
private window and see Users hidden and `/users` → no-access page.

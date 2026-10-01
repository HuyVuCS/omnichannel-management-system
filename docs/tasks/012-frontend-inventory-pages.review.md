# Review 012 — Frontend: Inventory, Stock import, Movement history (+ camelCase keys, field names, route error page)

Reviewer: Cowork, 2026-10-01
Verdict: **Accepted** — user's manual check pending (report, "Manual steps for the user")

## Checked
- Report and commits `a5a3b11` (A1), `7be93f4` (A2), `f3b7050` (`createdByEmail`), `5c63867` (A3), `95d42dc` (A4),
  `e49eb87` (A5), `884aac8` (Part B), `50eacdd` (report).
- Review based on reading the diffs and the report. Re-run by Cowork on the user's machine (Linux VM, Node 22.23):
  `npx tsc -b --noEmit` → exit 0; `npm run lint` → no ESLint findings, Prettier clean. `npm test` could **not** be re-run
  there (the installed `rolldown` binding is the Windows one); backend not re-run. Test results below are the report's.
- A1: `ValidationProblemResponse.NormaliseKeys` camelCases each `.` segment, keeps `[n]`, leaves `$…` and `""`, merges
  collapsing keys without repeating a message. One place, covers FluentValidation and model binding. Unit (12 cases) and
  integration tests on three list endpoints.
- A2: `FieldNames` resources keyed by JSON member name, wired as FluentValidation's `DisplayNameResolver`. The two things
  the task asked to verify were verified and the findings are sound: the resolver runs per validation;
  `{ComparisonProperty}` is fixed when the rule is built, so `DateRangeInverted` passes `From` as an argument. Completeness
  test walks child rules of collections. Mutations (unnormalised keys, a missing Vietnamese name) fail the intended tests.
- `createdByEmail`: correlated subquery evaluated only for the page's rows, text-to-text comparison (no uuid cast, so
  `system:*` actors cannot fail the query). Tests cover user, system actor, unknown id, no actor.
- A3: `lazyPage` turns a failed dynamic import into `PageLoadError` (type-based, not browser-text-based); error element
  inside the layout keeps the menu; full-screen variant for login/shell. No error details shown.
- A4: `Modal.destroyAll()` on session end, with the AntD 6 behaviour verified (hook confirms share `destroyFns`). Page
  `Modal`s unmount with their route.
- A5 / `useAddressText`: the "pending own write" check is correct — an own commit arriving late never overwrites newer
  typing; any other change of the address replaces the text. Shared by the search box and the reference filter.
- Part B: pages follow the 010/011 patterns; no client-side business rule (low stock from `isLowStock`, stock numbers and
  import limits from the API). Invalid address values are dropped before the request. Date range sends local start/end of
  day with offset. Import: confirmation, result view from the response, field / list / form-level errors as specified,
  invalidation only on success. `ProductSelect` loads nothing until opened, debounced server search, pre-set value labelled
  through `getProduct`.
- Backend 528/528 (360 unit, 168 integration); frontend typecheck, lint, 120/120, build; smoke `task-012.ps1` 24/24;
  24 frontend mutations each caught. The real-browser pass again found three layout defects mocks could not — all fixed.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | A2 changed one line in `IdentityUserService.RoleOf` (outside the listed files) | **Accepted.** It builds the same `RoleUnknown` message outside the validator pipeline; leaving it would ship a technical English field name in the Vietnamese UI, the exact defect A2 removes. The stop rule protects the *mechanism*; adopting the mechanism at an existing producer of the same message is in scope. Rule recorded in the implementation log. |
| 2 | `scripts/smoke/task-011.ps1` step 05 expects the new English text | Accepted (consequence of A2). |
| 3 | Frontend tests capped at `maxWorkers: 4` in `vite.config.ts` | **Accepted.** Root cause is memory (one jsdom per file), not CPU; the cap removes contention instead of hiding it with longer timeouts, and total time is unchanged. It is a ceiling: CI may override with `vitest --maxWorkers`. Revisit in 025 when the CI runner is sized. |
| 4 | Import confirmation is a page `Modal`, not `modal.confirm` | **Accepted.** A `modal.confirm` keeps the texts it was opened with, and the task required an open confirmation to follow the language switch. Rule: short yes/no confirmations may use `modal.confirm` (texts frozen while open — known limitation, same class as API messages); confirmations that show a data summary use a page `Modal`. A page `Modal` is not reached by `Modal.destroyAll()`, but it unmounts with its route on session end. |
| 5 | Full-screen error page offers only "Reload" | Accepted (a "back to dashboard" there would hit the same failure). |
| 6 | Extra cache rules: product create/update invalidates the inventory list and the product detail; inventory and movement lists refetch on every open | **Accepted.** Rule: a mutation invalidates every list that displays fields it changed; lists whose data the Workers change in the background use `refetchOnMountOrArgChange` until realtime arrives (016 revisits it: push → refetch, keep refetch on reconnect). |
| 7 | Three tests use `/alerts` as the "not built yet" page | Accepted. |
| 8 | Last line cannot be removed; quantity input min 1 | Accepted (input affordances; limits stay server-side). |

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | Main chunk grew 153 → 223 kB and the report does not explain it ("did not track down what moved into it"). Still under 500 kB, but unexplained growth in the chunk every page waits for tends to repeat. | 013 Part A3: identify what moved in, move page-only code out if that is the cause, report before/after |
| P3 | Long `SKU — name` labels are cut in the picker dropdown without a tooltip. 013 reuses the picker twice per line and adds a customer picker. | 013 Part A1 |
| P3 | `auth.test.tsx` › "logs out when the access token expires…" depends on a 400 ms real-time session and is load-sensitive. With 013 adding test files this gets worse. | 013 Part A2: deterministic (fake timers / controlled clock) |
| P3 | A rejected re-import (409) is logged by EF Core at Error with the `DbUpdateException` stack (since 003); same pattern for every mapped 23505. Expected business conflicts as errors mislead operators. | 014 Part A (backend task): expected, mapped constraint violations must not be logged as errors by EF Core; unexpected ones still are |
| P3 | `createdByEmail` compares `"Id"::text`, which cannot use the primary-key index. Fine for one page against a small users table. | 026 with the other movement-table performance items |
| P3 | `createdByEmail` shows colleagues' e-mail addresses to every role, Staff included. | Accepted: internal audit view of internal users; recorded as a decision |
| P3 | A message received from the API stays in the language it arrived in after a language switch (inline alert, form-level error, `modal.confirm`). | Accepted as a known limitation (consistent since 010) |
| P3 | No-access illustration carries AntD's English SVG `<title>`. | 026 (accessibility pass) |

No P0–P2 findings.

## Manual check
Pending — the user runs the steps in the report ("Manual steps for the user") after restarting the API. A failure goes
into 013 Part A.

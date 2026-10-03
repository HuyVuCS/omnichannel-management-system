# Review 018 — Frontend: Alerts page, dashboard alert card + Part A

Reviewer: Cowork, 2026-10-03
Verdict: **Accepted with one open verification item** — no P0–P1 finding. The report is **unfinished**: the five
consecutive full `npm test` runs (an acceptance criterion) are placeholders (`@@FIVE_RUNS@@`, `@@FIVE_RUNS_DETAIL@@`),
and the report and the README changes are not committed. → 018b Part A1, first commit.

## Checked
- Report (uncommitted, last written 15:51 +07:00) and commits `0cf52b2` (A1), `eadacd4` (A2), `b3d3bff` (A3),
  `8b9866a` (A4), `4252f7f` (A5), `81030d0` (A6), `f347a64` (Parts B, C), `0e39d97`, `7aa26e4`, `0c6e7d9`, `75e28cd`,
  `ebfb38d`. No migration, no new dependency, no new endpoint.
- Read in full: `ResetFormOnOpen.tsx`, `useOpenCount.ts`, the use in `ProductFormModal`, the alert cases of
  `hintTags.ts`, the diff of `ChannelService.UpdateProductAsync`.
- Checked by Cowork against the code:
  - **A1:** the reset runs in a layout effect of a child of `<Form>`, keyed on an open counter that the dialog
    component (which renders the closed state between two openings) advances during render whenever `opening` becomes
    a different truthy value — so record B after A and A after A (via `null`) both reset. The "mounted form" heuristic is
    gone; `clearOnDestroy` stays. Numbers: old hook 3 of 30 failed, fix 0 of 50; a constant counter fails all six
    reopen tests; production build 17 of 17 rounds correct (script-driven, deviation 2).
  - **A6:** resolve only on `Active` → `Inactive`, inside the new transaction after the save (the 23505 path rolls back).
    `wasActive` is read before the transaction without a lock: two concurrent edits of the same mapping can miss a
    resolve (P3, accepted — a manual retry still resolves; mapping edits are rare and manual).
  - **Tags:** `alert.changed` → id + LIST + summary, `all` → bare types; `low_stock.created` → LIST + summary; no
    toast/badge — as planned.
- **Verification:** backend 825/825, 0 warnings, 5/5 A6 mutants killed; frontend typecheck, lint, build clean, 338
  tests; first-load set +0.92 kB raw (alerts page lazy, 10.23 kB); `task-018.ps1` 35/35 and `task-017.ps1` 53/53 (new
  table format) on a throwaway environment; production-build browser check incl. live low stock (row on screen 0.7 s
  after the order finished), live resolution, dashboard card, acknowledge, Staff view. **Five consecutive full runs:
  not reported.** **The user (2026-10-03)** applied migration `Alerts` to the development database (it had not been
  applied before — the 017 entry of the log is corrected), ran `task-018.ps1` on development and the manual check
  (reported as done; table not pasted). Not re-run by Cowork.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | A2: no product change — the keyed `Suspense` of `AppLayout` already remounts a detail page per path | Accepted. Better than a second mechanism; pinned by three tests that fail without the key. |
| 2 | A1 browser check by script, not keyboard | Accepted; the user's manual check covered the keyboard. |
| 3 | Products table: medium size, small icon buttons | Accepted (question 3). |
| 4 | Existing tests changed (page without requests → `/reports`, tag-map assertions, one test ends settled) | Accepted. |
| 5 | Pre-existing flaky dashboard test fixed in product code (explicit radio-group `name`s) | Accepted — a real cause (one native radio group under test). Rule recorded in the log (question 2). |
| 6 | `patchParams` takes a list; `someOf` | Accepted. |
| 7 | Alerts table: action column fixed right, history columns scroll | Accepted — what an alert is and what can be done fit 1280 px. |
| 8 | Short type labels | Accepted. |
| 9 | Multi-value filters 360 px | Accepted. |
| 10 | One more A6 test (needed to kill a mutant) | Accepted. |
| 11 | Self-test +4 cases, `Format-SmokeTable` | Accepted. |
| 12 | Dashboard card placement + info icon | Accepted. |
| 13 | Smoke and browser check on a throwaway environment | Accepted; the development run was done by the user afterwards. |
| 14 | Three wrong counts in the first smoke run (PowerShell `@(...)` of an empty list) | Accepted. |

## Questions of the report
1. **Development database without `Alerts`.** The user applied the migration afterwards (the first attempt in 017 never
   reached PostgreSQL: the container was not running). Log entry "Task 017 result" corrected.
2. **Radio-group names.** Yes — rule in the log: every `Radio.Group` / `Segmented` gets an explicit, page-unique `name`.
   No lint rule.
3. **Medium table size on the products page.** Accepted.
4. **Tests that end with a request in flight.** Not a manual pass — a guard: the test setup fails any test that ends
   with an unanswered request (MSW life-cycle events, `afterEach`), then every test it flags is fixed. → 018b Part A2.
   This turns the handoff gotcha into an enforced rule; it has cost three red series so far.
5. **`createProduct` does not invalidate the alert list.** Leave as is: the list is not mounted while a product is
   created on the products page, a later mount refetches, and the `alert.changed` hint covers other tabs.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Report unfinished: five-run series missing, report and README not committed. | 018b Part A1 (first commit). |
| P3 | Question 4: tests can end with a request in flight. | 018b Part A2 (guard in the setup). |
| P3 | A6: `wasActive` read without a lock (concurrent edits of one mapping can miss a resolve). | Accepted; recorded. |

## Assessment
A1 closes the data-overwriting dialog defect with a mechanism that can be reasoned about (one counter in the component
that sees every state, one reset in the commit that has the new values) and with numbers before and after. The
alerts page keeps every rule on the server; the live path (order → `NOTIFY` → hint → refetch) put a new low-stock row
on screen 0.7 s after the order finished. Two good calls outside the letter of the task: A2 found the guarantee already
in place instead of adding a second one, and deviation 5 fixed a flaky test at its real cause.

## Manual check
Done by the user on 2026-10-03: migration `Alerts` applied, `task-018.ps1` on development, the manual browser check
(reported as done; table not pasted).

# Review 013 — Frontend: Order entry, Orders list and detail (+ order links in the movement history)

Reviewer: Cowork, 2026-10-02
Verdict: **Accepted** — user's manual check pending (report, "Manual steps for the user")

## Checked
- Report and commits `bb61ea5` (A2), `4398d43` (A3), `44d501e` (A1), `abcb192` (stale picker options), `2fa3d5a`
  (Part B), `f80a7b8` (report).
- Review based on reading the diffs and the report, plus the RTK Query 2 source in `node_modules` for deviation 1.
  Re-run by Cowork on the user's machine (Linux VM, Node 22.23): `npx tsc -b --noEmit` → exit 0; `npm run lint` → no
  ESLint findings, Prettier clean. `npm test` / `npm run build` not re-run there (Windows `rolldown` binding, as in
  review 012); backend unchanged (`git show --stat` of the five commits touches nothing under `backend/`). Test, build,
  smoke and mutation results below are the report's.
- A1: the 012 finding ("cut without a tooltip") came from an automated screenshot and was wrong — AntD 6 already sets the
  native `title`. Now explicit and tested for both pickers; `CustomerSelect` titles carry name and contact. Accepted.
- A2: expiry test on a controlled clock (`setTimeout`/`clearTimeout`/`Date` only, `shouldAdvanceTime`), 10/10 runs,
  mutation caught.
- A3: the analysis is sound and answers the 012 question: +67 kB (012) and +33 kB (013) are AntD shell code regrouped by
  the bundler, not new code; the one real leak (`inventoryApi` via `STOCK_IMPORT_ROLES`) is fixed. The measurable growth
  is the translation files (both languages up front).
- Part B against the task: three independent panels; external order id generated with unbiased rejection sampling, kept
  after every failure, sent untrimmed (the API trims it — `OrderIntakeService` and `Order` both call `Trim()`); outcome
  polling 1.5 s / 60 s / paused while hidden / one `getOrder` / one invalidation at the terminal state; `lastError` never
  rendered; no price, total, stock or channel-status rule in the client; list filters dropped when invalid; detail
  without client arithmetic; order links only for `Order` + GUID references.
- Smoke `task-013.ps1` 31/31 (and the "Workers stopped" path fails with the intended message); frontend 167/167;
  38/38 mutations; real-browser pass with VI/EN/VI measurements on all three pages. Again the browser found defects the
  mocked tests could not (wrong product on Enter, a 7 px overflow, GUID wrapping, an 85 ms false "processed").

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | Tab visibility tracked by a custom `setupListeners` handler in `app/tabVisibility.ts`, mounted from `App.tsx` | **Accepted.** Both facts verified in RTK Query 2 (`rtk-query.modern.mjs`): the default handler is guarded by a module-level `initialized` flag (one registration per page load, first store only), and the `config` slice takes `focused: isDocumentVisible()` when `createApi` runs while `resetApiState` rebuilds the state from that initial value — so after logout + login in a tab that was opened in the background, `skipPollingIfUnfocused` would keep polling off. The handler registers `visibilitychange` only (no focus/online listeners, so `refetchOnFocus`/`refetchOnReconnect` stay inert) and its store subscription is a cheap comparison that dispatches only on a mismatch (no feedback loop). Tied to the mounted app, so tests and StrictMode clean up. Rule recorded in the implementation log. |
| 2 | A finished task invalidates the order *list* tag only | **Accepted.** Orders are not changed after the Order Worker stored them (no cancel/edit API yet), the detail page refetches on open, and invalidating the order that is being loaded would fetch it twice. Revisit when an order can change state after creation (cancel, payment, 024): then the outcome must invalidate that order's tag after its own load. |
| 3 | `useListQueryState` option `defaultSortDir` | **Accepted.** Opt-in, other lists unchanged. Checked the cycle: the `createdAt` column uses `['descend', 'ascend']`, so default (desc, nothing in the address) → asc (`?sortDir=asc`) → back to the default; covered by "sorts through the address … back to the default order". |
| 4 | One existing movement test changed | **Accepted.** It asserted the absence of the feature this task adds; the "plain text" case is kept with a manual adjustment and the order link has its own test. |
| 5 | Both pickers offer no option while a search is pending (behaviour change of the 012 `ProductSelect`) | **Accepted** — it fixes a data-entry defect (a different product on the line). One refinement as P3 below (`isFetching` also hides options during a background refetch of the *same* search). |
| 6 | A1 added no tooltip component | Accepted (native `title` is sufficient; tested). |
| 7 | "Clear filters" keeps the search text | Accepted (same as the other lists). |
| 8 | Type names without the `…Response` suffix | Accepted (follows the existing features). |
| 9 | Small UI choices (last line not removable, quantity ≥ 1, status/total rows in the outcome, task re-read when the outcome opens) | Accepted (affordances; limits stay server-side). |
| 10 | `StockImportForm` quantity width moved to `layout.ts`; `updateCustomer` invalidates the customer's own tag | Accepted (the second one prevents a stale picker label). |

## Question: which bundle size to track
**Decision: (a) — track the first-load set** (entry chunk + everything it imports statically), raw and gzip, in every
frontend report; explain growth above 10 kB raw. The main chunk is still listed but not judged on its own: it moves by
tens of kB when the bundler regroups shared vendor code. **(b) `manualChunks` is not tied to 025** (that task is about
containers and CI, not bundling); it is considered only if the first-load set has to be reduced — candidate for the 026
performance pass. The cheapest real reduction is loading only the active language (≈ 14 kB raw today) — 016 Part A.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | `StockImportForm` keeps an API error of the list as a whole after the next submit (the order form clears it). | 014 Part A2 (one line + test) |
| P3 | Pickers hide their options whenever `list.isFetching` — also while the *same* search is refetched in the background (e.g. the `Product` invalidation after an order finished in another panel while a dropdown is open): a brief spinner instead of correct options. Safe, slightly jumpy. RTK's `currentData` is `undefined` exactly while a *different* argument is loading, so `searchPending \|\| list.currentData === undefined` (with `isFetching`) would hide only stale answers. | 016 Part A |
| P3 | Panel title repeats itself when the component name equals the channel name ("Website Website"). | 016 Part A |
| P3 | Orders table needs 1,378 px at 1280 px width (content 992 px); status — the column users scan — scrolls out of view. | 016 Part A (column order/widths, no layout redesign) |
| P3 | Both translation files are in the first-load set and grow with every page (2 × 14.3 kB raw). | 016 Part A: load only the active language, switch without remount (keep the "text only" rule) |
| P3 | The 60 s polling window is wall-clock from the 202: a tab hidden for a minute returns to "still processing". | Accepted (one click on "Check again"); 016 replaces polling by push |
| P3 | "Enter" with no option on offer closes the dropdown and clears the typed text (AntD behaviour). | Accepted for now; revisit if scanner input is required |

No P0–P2 findings.

## Manual check
Pending — the user runs the steps in the report ("Manual steps for the user"), with API and Workers running. A failure
goes into 014 Part A (or 016 Part A if it is frontend-only). The 012 manual check is still pending too.

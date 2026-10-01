# Review 011 — Frontend: Products, Customers, Channels pages (+ AntD 6, localised validation messages, code splitting)

Reviewer: Cowork, 2026-10-01
Verdict: **Accepted** — user's manual check (report, "Manual steps for the user") still pending

## Checked
- Report, commits `e595411` (A3), `ead627a` (A2), `18cead6` (A1), `29ab04e` (A4 + Part B), `ffa513d` / `04df2f8` (report).
- Review based on reading the diffs and the report; build and tests were **not** re-run by Cowork.
- A3: `IsLowStock` on `ProductResponse` is the shared `InventoryPredicates.IsLowStock` expression re-rooted on the product
  (`IsLowStockProduct`) and inlined into the projection, so it runs in SQL. No second implementation. The test walks the
  boundary (0, = threshold, threshold + 1, threshold raised) and asserts product list, product detail and inventory list agree.
- A2: one mechanism (`ValidationMessages` + `ValidationMessages.resx` / `.vi.resx`) for validator messages, service-level
  failures and the Identity password policy. Messages are resolved per request through the lambda overload of `WithMessage`.
  Completeness test (keys × cultures × placeholders) plus a reflective test that fails on a literal `WithMessage("…")`
  (mutation confirmed in the report). Keys and `errorCode` unchanged as required.
- A1: AntD 6.6.5 / icons 6.3.4, v5 React-19 patch removed, two deprecations fixed, 010 tests green without source changes.
- A4: `React.lazy` per page; `Suspense` keyed by path in `AppLayout` (a query-string change or a language switch does not
  remount). Main chunk 1,369 kB → 153 kB; no chunk above 500 kB.
- Part B: pages follow the 010 patterns; only shared pieces were extracted (`useListQueryState`, `useFormSubmission`,
  `useResetFormOnOpen`, a few small components) — no CRUD framework. Business rules stay on the server (`isLowStock`
  displayed, SKU upper-cased by CSS only). Role-hidden actions with the API as authority.
- Backend 479/479 (326 unit, 153 integration) × 3; frontend typecheck, lint, 77/77, build; smoke `task-011.ps1` 16/16;
  fifteen mutation checks, each failing the intended test. The real-browser pass again found defects mocks could not
  (page-size selector width, stale values in a dialog reopened during its close animation) — both fixed with tests,
  including the same latent defect in the Users dialogs from 010.

## Deviations — accepted
1. A4 committed together with Part B (same route table) — fine.
2. Browser check reused the existing locked test Admin `ui-check-61563` (unlocked for the check, locked again) — the right
   call given "no new Admin". Rule from now on: Admin browser checks reuse that account and lock it again afterwards.
3. `findBy…` timeout 5 s in tests — accepted (lazy modules on first render in parallel test files).
4. Prices entered and edited as whole VND — accepted (VND has no minor unit; only test data has decimals).
5. `react-refresh/only-export-components` off for `router.tsx` — accepted.
6. Model-binding / malformed-JSON texts not localised — accepted (the UI cannot produce those requests).

## Decision: validation error keys of query-string parameters → camelCase
- **Decision:** every key of a 400 `ValidationProblemDetails` is a camelCase JSON path (`to`, `sortBy`, `pageSize`,
  `items[0].productId`), for FluentValidation errors **and** model-binding errors. Normalised in one place,
  `ValidationProblemResponse.Create`. Keys that start with `$` (System.Text.Json body paths of malformed JSON) and the empty
  key are left unchanged.
- **Reason:** the convention in the implementation log (task 003/004) is camelCase; `ModelStateDictionary` is
  case-insensitive, so a supplied query parameter's entry keeps the C# name ("To") and FluentValidation's "to" is merged
  into it. Task 012 is the first page with query-string filters that can fail validation (`from` / `to` of the movement
  history), so the frontend would otherwise need a case-insensitive special case.
- **Alternatives:** document the difference (pushes a special case into every client); fix it in `ValidationFilter` only
  (misses model-binding errors such as `pageSize=abc`).
- **Trade-offs:** a visible change of the 400 contract for query parameters; no client depends on the old casing yet.
- **Revisit when:** never expected; the API has no external consumers of these keys.
- Scheduled as task 012 Part A1.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Query-string validation keys keep the C# casing (`To`, `SortBy`) — see the decision above. | 012 Part A1 |
| P2 | No route `errorElement`. With lazy pages, a chunk that fails to load (redeploy while a tab is open → old hashed file gone, or a network blip) ends on react-router's default English developer error screen with no way back except a manual reload. Same for any render error. | 012 Part A3: localised error page with "reload" for chunk-load failures and a generic message otherwise |
| P3 | Property names inside validation messages are technical and English (`'low Stock Threshold' phải …`, `'sort By'`, `'To' không được sớm hơn 'From'`). 012 adds the import form (`items[n].stock`, `documentNumber`) where users will see them. | 012 Part A2: localised field display names through the same resource mechanism |
| P3 | A confirmation dialog open when the session ends stays over the login page (its OK gets a 401). 012 adds an import confirmation. | 012 Part A4 |
| P3 | The search box does not follow the address when it changes from outside: on `/products?search=abc`, clicking "Sản phẩm" in the menu navigates to `/products`, but `searchText` is still "abc" and the debounce effect writes `search=abc` back. Back/forward has the same effect. | 012 Part A5: local text follows the address; regression test |
| P3 | Largest chunks (vendor 321 kB, shared form code 270 kB). | Accept; revisit only if a page gets slow |
| P3 | `OrderEndToEndTests` could reuse `RabbitMqBrokerFixture` (open since 008). | Accept; candidate for 025 (CI) |

## Manual check
Pending (user): restart the API, `npm ci` in `frontend/`, then the four checks in the report ("Manual steps for the user").

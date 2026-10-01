# Report 011 — Frontend: Products, Customers, Channels pages (+ AntD 6, localised validation messages, code splitting)

Status: Done
Commits: `e595411` (A3 `isLowStock`), `ead627a` (A2 localised messages), `18cead6` (A1 AntD 6),
`29ab04e` (A4 code splitting + Part B pages + smoke script + README)

## Summary
- **Part A:** AntD 6; every validation message the application writes is now Vietnamese/English through one resource
  mechanism; `isLowStock` on product responses from the shared rule; each page is its own chunk.
- **Part B:** Products, Customers and Channels pages with create/edit dialogs, role-based actions, list state in the query
  string, and the same language-switch guarantees as the Users page.
- **Checked against the real API in a browser.** That found two defects the mocked tests could not see; both are fixed and
  now covered by tests (see "Real-browser check").
- Backend: build 0 warnings, **479/479** tests (326 unit, 153 integration), 3 consecutive full runs green. No migration.
- Frontend: `typecheck`, `lint`, `test` (**77/77** in 8 files) and `build` pass. Main chunk 153 kB (was 1,369 kB).
- `scripts/smoke/task-011.ps1`: all 16 checks passed.
- One finding needs a decision from Cowork (query-string error keys, see "Blocked / questions"). It does not block the task.

## Part A — what changed
### A1 — AntD 6 (`18cead6`)
- `antd` 5.29.3 → **6.6.5**, `@ant-design/icons` 5.6.1 → **6.3.4**, `@ant-design/v5-patch-for-react-19` removed.
- The migration was narrow. The 42 tests of task 010 passed without source changes. Two deprecations were fixed: `Alert` and
  the error notification use `title` instead of `message`. No warnings remain in the test output.
- The Users page was re-measured in VI/EN (numbers below).

### A2 — Localised validation messages (`ead627a`)
- **One mechanism:** `Application/Resources/ValidationMessages.resx` (English, default) and `ValidationMessages.vi.resx`, read
  by `Application/Common/Validation/ValidationMessages.Get(key, (name, value)…)` in the UI culture of the request.
- 20 keys. All 15 `WithMessage` texts in the validators, the 5 validation failures raised by services (role, e-mail,
  product/customer not found) and the 6 Identity password messages now come from it.
- Error keys and `errorCode` values are unchanged.
- Tests:
  - `ValidationMessagesTests`: every key has an English and a Vietnamese text with the same placeholders.
  - Same class, reflective: for each of the 19 request validators, no rule has the same message in `en` and `vi`. A literal
    `.WithMessage("…")` fails this test.
  - `RequestLocalisationTests`: SKU format, inverted date range and a non-whitelisted sort field come back in Vietnamese for
    `Accept-Language: vi` and in English for `en`; a service-level failure (unknown customer/product) is Vietnamese too.

### A3 — `isLowStock` on products (`e595411`)
- `ProductResponse.IsLowStock` (list, detail, create and update responses).
- It is the shared `InventoryPredicates.IsLowStock` expression applied to the product's inventory row
  (`InventoryPredicates.IsLowStockProduct`) and inlined into the projection, so it runs in SQL. There is no second rule in C#
  or TypeScript.
- Test `Product_responses_carry_the_low_stock_flag_of_the_shared_rule`: out of stock → low; exactly at the threshold → low;
  one above → not low; raising the threshold → low again. Product list, product detail and inventory list always agree.

### A4 — Route-level code splitting (in `29ab04e`)
- `React.lazy` per page in `src/app/router.tsx`; `Suspense` around the page in `AppLayout`, fallback `PageLoading` (AntD `Spin`
  filling the content area). The login page has a full-screen fallback.
- The boundary is keyed by path. Without the key the router's transition would keep the old page on screen instead of
  showing the fallback. A query-string change or a language switch does not remount the page.

| Chunk (largest first) | Size | gzip |
| --- | --- | --- |
| `jsx-runtime` (React and shared vendor code) | 320.83 kB | 106.69 kB |
| shared form/dialog code (named `useResetFormOnOpen` by the bundler) | 270.30 kB | 84.89 kB |
| `layout` | 183.24 kB | 61.48 kB |
| **`index` (main chunk)** | **153.43 kB** | **51.26 kB** |
| `typography` | 147.17 kB | 48.45 kB |
| `ProductsPage` (includes the date picker) | 128.62 kB | 40.70 kB |
| `spin` | 98.18 kB | 30.94 kB |
| `input` | 59.60 kB | 22.06 kB |
| `LoginPage` | 45.67 kB | 14.06 kB |
| `UsersPage` / `CustomersPage` / `ChannelsPage` | 12.67 / 4.56 / 4.54 kB | 4.20 / 1.91 / 1.89 kB |

Before: one file of 1,368.89 kB (431 kB gzip). Now no chunk is above 500 kB and Vite prints no size warning.

## Part B — what exists
| Page | Read | Write | Notes |
| --- | --- | --- | --- |
| Products `/products` | every role | Manager, Admin | Sort `sku` (default), `name`, `price`, `createdAt`; search; low-stock tag from `isLowStock`; create and edit dialog (SKU read-only on edit) |
| Customers `/customers` | every role | every role | Sort `name` (default), `createdAt`; search; address truncated with tooltip; create and edit dialog |
| Channels `/channels` | every role | Admin | Not paged; type and status tags; create dialog; edit dialog (name, status); deactivating asks for confirmation |

Shared pieces (only what the four list pages actually share):
- `useListQueryState` — paging, sorting and search of a list in the query string, with the bindings for an AntD table.
- `useFormSubmission` — submit, success toast, close; server errors to fields or to a form-level alert.
- `useResetFormOnOpen` — see "Real-browser check", item 2.
- `FormErrors`, `TruncatedText`, `MoneyInput`, `PageLoading`, `useHasAnyRole`.

The Users page was moved onto these pieces; its tests are unchanged except for the new one.

Rules kept: no business rule in the client (the low-stock tag shows `isLowStock`; the SKU is sent as typed and shown
upper-cased only by CSS); actions a role may not use are hidden and a 403 is still shown translated; no hard-coded text
(the parser-based test covers the new files); `vi.json` and `en.json` have identical keys.

## Real-browser check (dev server + real API, 1280×800)
Accounts: I reused the **existing** test admin from task 010 (`ui-check-61563`, unlocked for the check and locked again
afterwards) because the Users page and channel editing need an Admin; no new Admin was created. One Staff user
`smoke-011-ui-68707` was created and is locked again. Data left in the development database: products `SMOKE-011-UI-96559` and
`SMOKE-011-<n>` (one per smoke run), customer `Smoke 011 UI 56579`, channel `smoke-011-ui-53092` (Inactive).

Measured in VI, then EN, then VI again. Every value below was identical in all three; the table and the dialog input were
the same DOM nodes, and typed values were kept.

| | Users (AntD 6) | Products | Customers | Channels |
| --- | --- | --- | --- | --- |
| Sider / header height | 240 / 56 | 240 / 56 | 240 / 56 | 240 / 56 |
| User block / language switch | 320 / 96×32 | 320 / 96×32 | 320 / 96×32 | 320 / 96×32 |
| Page action / search | 176×32 / 280×32 | 176×32 / 280×32 | 176×32 / 280×32 | 176×32 / — |
| Page-size selector | 120×32 | 120×32 | 120×32 | — |
| Columns | 328/164/164/164/156 | 180/280/150/140/140/130/170/88 | 240/160/240/300/170/88 | 280/240/220/180/88 |
| Row height | — | 65 | 65 | 65 |
| Open create dialog | 480×411 | 480×608 | 480×470 | 480×406 |

Functional checks with the real API, all as expected:
- Products: bad SKU → the Vietnamese format message under the SKU field; create (the server upper-cases the SKU) → list
  refreshes, tag "Sắp hết"; edit → saved.
- Customers: phone `abc` → Vietnamese message under the phone field; create; the long address is one truncated line.
- Channels: slug `website` → "Đã có kênh bán với slug này." under the slug field; bad format → Vietnamese message; create;
  deactivate → confirmation → status "Ngừng hoạt động".
- Staff: no create/edit on Products and Channels (no Actions column), create/edit on Customers, no Users menu item, `/users`
  shows the no-access page.

It found two defects, both fixed:
1. **The page-size selector changed width with the language** ("20 / trang" 103 px, "20 / page" 101 px), which moved the
   pagination by 2 px. It now has a fixed width (120 px) for every list. The language tests assert that width.
2. **A form dialog reopened before its close animation had finished kept the previous values.** Opening "edit" for product A,
   cancelling, and quickly opening "edit" for product B showed A's values under B's id; saving would have written them to B.
   The same was true for the Users dialogs from task 010 (the role of the previous user). Cause: `destroyOnHidden` only
   destroys the form when the animation ends. Fix: `useResetFormOnOpen` resets the form on every open, and cancelling clears
   the form-level error at once. Four new tests reproduce it (animations on; in jsdom they never finish).

## Verification (actually executed)
Backend from `backend/`, frontend from `frontend/`. SDK 10.0.401, Docker 29.8.1, Node 24.16.0.

| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, 0 warnings |
| `dotnet test --no-build` (3 consecutive full runs) | Failed 0, Passed 479, Total 479 (×3) |
| `dotnet test --no-build --filter "Category!=Integration"` | **Unit:** Failed 0, Passed 326, Total 326 |
| `dotnet test --no-build --filter "Category=Integration"` | **Integration:** Failed 0, Passed 153, Total 153 |
| `dotnet ef migrations has-pending-model-changes … --configuration Release` | `No changes have been made to the model since the last migration.` |
| `npm run typecheck` | exit 0, no output |
| `npm run lint` | ESLint: no findings; `All matched files use Prettier code style!` |
| `npm test` | `Test Files 8 passed (8)`, `Tests 77 passed (77)`, 35 s |
| `npm run build` | `3197 modules transformed`, built in 13.8 s, chunk sizes above, no size warning |

### Smoke script (`scripts/smoke/task-011.ps1`, run by Claude Code)
```
Step                                                                         Expected                                Actual   OK
----                                                                         --------                                ------   --
01 SKU format message, vi: Vietnamese text                                       True                                  True True
02 SKU format message, en: English text                                          True                                  True True
03 SKU messages differ by language                                               True                                  True True
04 date range message, vi: Vietnamese text                                       True                                  True True
05 date range message, en                       'To' must not be earlier than 'From'. 'To' must not be earlier than 'From'. True
06 password policy message, vi: Vietnamese text                                  True                                  True True
07 create product (threshold 5, no stock)                                         201                                   201 True
08 new product is low on stock                                                   True                                  True True
09 import 5 units                                                                 200                                   200 True
10 at the threshold: still low (detail)                                          True                                  True True
11 import 1 more unit                                                             200                                   200 True
12 above the threshold: not low (list)                                          False                                 False True
13 available stock in the list                                                      6                                     6 True
14 inventory list agrees with the product list                                  False                                 False True
16 npm run build                                                                    0                                     0 True
17 npm test                                                                         0                                     0 True

PASSED: all 16 checks.
```
Step 15 (`npm ci`) only runs when `frontend/node_modules` is missing.

### Mutation checks (each restored byte for byte, checked by checksum)
| Mutation | Result |
| --- | --- |
| Backend: a literal `.WithMessage("…")` in a validator | `ValidationMessagesTests` **failed** for that validator |
| Backend: low-stock rule `<` instead of `<=` | Two product tests **failed** |
| Low-stock tag computed in the client from stock and threshold | Products list test **failed** |
| Staff sees product create/edit | Role test **failed** |
| Manager sees channel create/edit | Role test **failed** |
| Channel deactivated without confirmation | Channel edit test **failed** |
| List refetched after a failed product save | Validation/conflict test **failed** |
| Sort field not restored from the address | Products and Customers reload tests **failed** |
| No Suspense fallback | Code-splitting test **failed** |
| Page remounts on language switch | Products and Channels language tests **failed** |
| SKU upper-cased by the client | Product create test **failed** |
| Empty optional customer fields sent as text | Customer create test **failed** |
| No form reset on open | All four reopen tests **failed** |
| Form-level error not cleared on cancel | Customers reopen test **failed** |
| Page-size selector without fixed width | First run: **no test failed**. I added the width assertion; second run: Products language test **failed** |

### Required tests → where
| # | Requirement | Test |
| --- | --- | --- |
| 1 | List with paging/sorting/search in the query string; reload keeps it | `products.test.tsx` and `customers.test.tsx` › "restores paging, sorting and search from the address (reload)…"; `channels.test.tsx` › "lists every channel…" (not paged) |
| 2 | Create/edit happy path; server errors on fields; 409 on SKU/slug; refresh only after success | Products: create, edit, typed expiry date, validation + 409 + 403. Customers: create, validation, edit. Channels: create, 409 + format, edit with confirmation, activate without confirmation |
| 3 | Role visibility | `products page: roles`, `customers page: roles`, `channels page: roles` (Staff, Manager, Admin each) |
| 4 | Language switch keeps route, table state, open form and values | `… : language switch` in each of the three files (`expectLanguageSwitchChangesTextOnly`, the 7b approach as a shared helper) |
| 5 | Low-stock tag follows `isLowStock` | Products list test: the flags contradict what stock and threshold would give |
| 6 | Code splitting | `codeSplitting.test.tsx`: fallback first, then the page; navigation through the menu; login page |
| — | Dialog reopened before it closed | One test per page, including Users |
| — | Backend A2, A3 | see "Part A" |

## Deviations from the task
1. **A4 is in the same commit as Part B.** Both change the route table, so they could not be separated without inventing an
   intermediate state that was never tested. A1–A3 are separate commits.
2. **Browser check reused the existing test Admin** instead of smoke-style accounts only. The task asks to re-measure the
   Users page and to check channel editing, which both need an Admin, and forbids a new Admin.
3. **`findBy…` timeout raised to 5 s in the test setup.** The first render of a lazy page in a test file loads its modules,
   which exceeds the default 1 s when test files run in parallel.
4. **Prices are whole VND in the form.** A stored price with decimals (only test data has them) is shown rounded and would be
   saved rounded when the product is edited.
5. **ESLint:** `react-refresh/only-export-components` is off for `router.tsx`, which declares the lazy pages next to the
   routes.
6. **Not localised:** model-binding and malformed-JSON messages. They are framework texts for requests the UI cannot
   produce (a non-numeric `pageSize`, broken JSON).

## Blocked / questions for Cowork
**Validation error keys of query-string parameters are not camelCase.** For a supplied query parameter the key keeps the
casing of the C# property: `GET /api/orders?from=…&to=…` answers `{"To": [...]}` and `?sortBy=x` answers `{"SortBy": [...]}`,
while body fields are camelCase (`sku`, `items[0].stock`). The convention in the implementation log says camelCase JSON paths.
Cause: model binding registers the entry under the property name first, and the validation filter adds its error to that
entry. This has been so since task 003; no frontend page depends on it yet. I did not change it, because this task says keys
must stay unchanged. Options: camelCase all keys in `ValidationProblemResponse` (my recommendation; a small change with
tests), or document the difference.

## Out of scope / follow-ups
- **Property names inside messages are technical** (`'sku' dài tối đa…`, `'low Stock Threshold' …`). FluentValidation builds
  them from the JSON name. Localised field labels would need display names from the same resources.
- **A confirmation dialog that is open when the session ends stays open over the login page.** Its OK button then gets a 401
  and does nothing, but it should be closed on logout.
- The largest chunks (React/vendor 321 kB, shared form code 270 kB) could be tuned further with manual chunk groups; not
  needed for the 500 kB limit.
- `OrderEndToEndTests` could reuse `RabbitMqBrokerFixture` (from 008, still open).

## Manual steps for the user
1. Restart the API so it runs the new code. No migration. In `frontend/` run `npm ci` once (AntD 6 replaced AntD 5).
2. Manual check (`npm run dev`, then http://localhost:5173):
   - As Admin or Manager open **Sản phẩm**: create a product (try the SKU `bad sku!` first and see the message under the
     field), then edit it. Switch `VI | EN` with the dialog open: only the texts change.
   - Open **Khách hàng**: create a customer with a long address and see it truncated with a tooltip.
   - As Admin open **Kênh bán**: edit a test channel and set it to "Ngừng hoạt động"; a confirmation appears first.
   - As Staff: Products and Channels show no create/edit buttons; Customers does.

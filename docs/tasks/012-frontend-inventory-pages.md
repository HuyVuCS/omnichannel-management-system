# Task 012 — Frontend: Inventory, Stock import, Movement history (+ camelCase error keys, field names, route error page)

Status: Open
Written by: Cowork, 2026-10-01
Depends on: 011 (accepted)

## Context
Third frontend task (doc §3.2: Tồn kho, Lịch sử biến động kho; doc §11: `GET /api/inventory`, `POST /api/inventory/import`,
`GET /api/inventory/movements`). Backend endpoints exist since tasks 002/003. Reuse the 010/011 patterns exactly: RTK Query
endpoints per feature, list state in the query string (`useListQueryState`), `useFormSubmission` / `applyProblemToForm`,
`useResetFormOnOpen` for dialogs, translated `errorCode`, layout constants, no hard-coded text, language switch changes
text only, no client-side business rules.

Read first: review 011 (`docs/tasks/011-frontend-catalog-pages.review.md`), implementation log "Task 011 result".

## Part A — Follow-ups from review 011 (separate commits)
1. **camelCase validation keys everywhere (backend, decided in review 011).** In `ValidationProblemResponse.Create`, normalise
   every key of the problem's `errors`: camelCase each `.`-separated segment's name and keep `[n]` indexes
   (`To` → `to`, `SortBy` → `sortBy`, `PageSize` → `pageSize`, `Items[0].ProductId` → `items[0].productId`). Keys starting
   with `$` and the empty key stay unchanged. Merge messages if two keys collapse into one. Update the `<remarks>` of
   `ValidationProblemResponse` and remove the case-insensitive lookup (and its comment) from `RequestLocalisationTests`.
   Tests (integration): `GET /api/orders?from=…&to=…` inverted → key exactly `to`; `?sortBy=secret` → `sortBy`;
   `?pageSize=abc` (model binding) → `pageSize`; a body error is still `items[0].stock`; malformed JSON still a `$…` key.
2. **Localised field names in validation messages (backend).** Messages must name fields in the request language, not with
   technical names (`'low Stock Threshold'`, `'sort By'`, `'To' … 'From'`).
   - One mechanism: a second resource pair `Application/Resources/FieldNames.resx` (English) / `FieldNames.vi.resx`, keyed by
     the JSON member name (`lowStockThreshold`, `stock`, `documentNumber`, `from`, `to`, …; one key per name, not per type).
     Wording = the matching UI label in `vi.json` / `en.json` where the UI has that field.
   - Wire it as FluentValidation's display name (`ValidatorOptions.Global.DisplayNameResolver`, set next to
     `UseJsonPropertyNames`). **Verify** that the resolver is evaluated per validation (request UI culture) and not once
     when the rule is built; if it is not, use a per-rule `WithName(_ => …)` helper instead. `DateRangeInverted` and
     `RoleUnknown` must use the field names too (`{PropertyName}` / `{ComparisonProperty}` or explicit arguments — verify
     which placeholders FluentValidation 12 fills for a comparison rule).
   - Tests: completeness — every property that has a rule in any request validator (enumerate validators and use
     `CreateDescriptor().GetMembersWithValidators()`, including child rules of collections) has a field name in `en` and `vi`;
     integration — `lowStockThreshold = -1` and the inverted date range come back with the Vietnamese field names for
     `Accept-Language: vi` and English ones for `en`.
   - If this needs changes to more than the validators' set-up, `ValidationMessages`, the two resource pairs and
     `Program.cs`/test start-up, stop and report (`Blocked`).
3. **Route error page (frontend).** Add an `errorElement` to the route tree (inside the layout for signed-in pages, so the
   menu stays usable; a full-screen variant for the login route). Localised; for a failed dynamic import (chunk load error)
   it says a new version may be available and offers "Reload"; otherwise a generic message, a
   "Back to dashboard" link and "Reload". Never shows a stack trace. Tests: a lazy page whose import rejects shows the
   page with a reload button; a page that throws while rendering shows the generic variant; the menu still works.
4. **No dialog survives the session.** Confirmation dialogs (`modal.confirm` from `App.useApp()`) and form dialogs must be
   closed when the session ends (logout, 401, expiry). Verify whether `Modal.destroyAll()` covers hook-based confirm dialogs in
   AntD 6; if not, close them through the instances `modal.confirm` returns (e.g. a small hook that destroys its dialogs on
   unmount). Test: open the channel-deactivation confirm, sign out → no dialog on the login page.
5. **Search box follows the address.** In `useListQueryState`, when `search` in the address changes from outside (menu click
   on the same page, back/forward), the local text follows it and the debounce must not write the old text back.
   Regression test: `/products?search=abc` → click the "Products" menu item → address without `search`, empty box, list
   requested without `search`.

## Part B — Pages
Shared rule (as in 011): actions a role may not use are hidden; the API stays the authority (a 403 still shows a translated
message). Read endpoints are AnyStaff; import is Manager/Admin.

### Backend support (small, own commit)
- **Who made a movement.** `InventoryMovementResponse` gets `createdByEmail` (nullable): the e-mail of the user whose id is
  `CreatedBy`, `null` for system actors (`system:*`) and unknown ids. Resolve it in the same query (correlated subquery or
  left join on the Identity users table, comparing `CreatedBy` with the user id as text — never cast `CreatedBy` to uuid,
  system actors would fail). Check the generated SQL once and paste it in the report. Keep `createdBy` as is.
  Test: an import by the manager test user returns that user's e-mail; a reservation by the Order Worker returns `null`
  and `createdBy` starting with `system:`.

### Inventory — `/inventory` (every role)
- Table: SKU, name, total, reserved, available, low-stock threshold, low-stock tag (`isLowStock`, same tag and colours as
  Products), updated at. Server paging; sort `sku` (default), `name`, `availableStock`, `updatedAt`; debounced search
  (SKU/name); a "Low stock only" switch bound to `lowStockOnly=true` in the address (resets to page 1).
- Numbers right-aligned with the same grouping in both languages (add a `formatNumber` next to `formatMoney`).
- Row actions: "Movement history" → `/inventory/movements?productId=<id>` (everyone); "Import stock" →
  `/inventory/import?productId=<id>` (Manager/Admin only).
- Page actions: "Import stock" (Manager/Admin) and a "Refresh" button (stock also changes asynchronously through the Order
  Worker; realtime arrives in 016).

### Stock import — `/inventory/import` (Manager/Admin; not a menu item)
- Route outside the menu list: extend the route table minimally so a page can exist without a menu item; the menu highlights
  "Tồn kho" (`activeNavigationPath` already picks the longest parent). `RequireRole` Manager/Admin; Staff gets the
  no-access page.
- Form: document number (required), document series (optional), and lines (`Form.List` `items`): product + quantity
  (whole number, ≥ 1). "Add line" / remove line. `?productId=` pre-fills the first line.
- **Product picker** (`features/products/components/ProductSelect.tsx`, reused by 013's order entry): AntD `Select` with
  server search (`GET /api/products?search=&pageSize=20`, debounced), option text `SKU — name`, available stock shown in
  the option. A pre-selected id shows its label (add `getProduct` = `GET /api/products/{id}` to `productsApi`). Products
  chosen on another line are disabled in the options (convenience only; the server still rejects duplicates).
- Submit: confirmation dialog with document reference, line count and total quantity (the import cannot be undone; there
  is no reversal endpoint yet). Submit button loading/disabled while the request runs.
- Errors: field errors land on `documentNumber`, `documentSeries`, `items[n].productId`, `items[n].stock`; list-level errors
  (`items`: duplicates, too many lines) under the list (`Form.ErrorList`; verify `setFields` on a `Form.List` name shows
  them); 409 `stock_import_already_applied` on the document number field with a translated text that says the document
  was already imported and points to the movement history; 409 `stock_limit_exceeded` as a form-level alert.
- Success: a result view (AntD `Result`) with the reference and a table of the lines with their new total / reserved /
  available stock (from the response), plus "View movements of this document" →
  `/inventory/movements?referenceType=StockImport&referenceId=<referenceId>` and "New import" (resets the form).
- Cache: a successful import invalidates inventory, movements and products (add the tag types); a failed one invalidates
  nothing.
- Leaving the page with unsaved lines is not guarded in this task (follow-up candidate).

### Movement history — `/inventory/movements` (every role)
- Always newest first (the API does not sort): no sortable columns, no search box; paging in the address as usual.
- Filters, each in the address and resetting to page 1: product (`ProductSelect`, `productId`), type (`Import`, `Reserve`,
  `Release`, `Deduct`, `Adjustment`), reference type (`Order`, `StockImport`, `ManualAdjustment`), reference (exact text,
  `referenceId`), date range (`RangePicker`; the address keeps local dates `from=YYYY-MM-DD&to=YYYY-MM-DD`; the request
  sends the local start of `from` and the local end of `to` as ISO 8601 with offset). Values in the address that are not
  valid (unknown enum, bad date, non-GUID product id) are ignored, never sent. "Clear filters" button.
- Columns: time (`DD/MM/YYYY HH:mm`), SKU, product name, type tag (one status colour per type, translated), quantity
  (`stock`), reference (translated reference type + `referenceId`, truncated with tooltip; a `StockImport` reference links to
  the same page filtered by that reference; `Order` references become links in 013), actor (`createdByEmail`; for
  `system:*` actors a translated "System" with the raw actor as tooltip; otherwise the raw value).
- A list request answered with a 4xx (e.g. an inverted range typed into the address) shows an inline `Alert` above the
  table with the translated messages instead of an empty table.
- "Refresh" button as on the inventory page.

### Products page link
- Products page row action "Import stock" (Manager/Admin) → `/inventory/import?productId=<id>` (deferred from 011).

## Tests (required)
Frontend:
1. Inventory: list with paging/sorting/search and `lowStockOnly` in the address; reload keeps it; low-stock tag follows
   `isLowStock` (flags that contradict stock/threshold); row and page actions per role (Staff: history only).
2. Import: happy path (two lines → confirm → result view with the response values → caches invalidated); pre-fill from
   `?productId=` shows the product label; field errors on `items[1].stock` and `items[0].productId`; list-level `items`
   error; 409 `stock_import_already_applied` on the document number; 409 `stock_limit_exceeded` as form-level alert; no
   invalidation after a failure; cancel in the confirmation sends nothing; Staff gets no-access on the route.
3. Movement history: every filter round-trips through the address (reload keeps it) and resets the page; invalid address
   values are not sent; date range sends local start/end of day with offset; 400 shows the inline alert with the server
   messages; actor column (e-mail, system, raw); `StockImport` reference link filters by that reference.
4. `ProductSelect`: server search is debounced; a pre-selected id renders its label; options chosen elsewhere are disabled.
5. Language switch on each of the three pages keeps route, filters, table state, the import form with its lines and an open
   confirmation, and texts change (`expectLanguageSwitchChangesTextOnly`).
6. Part A 3, 4, 5 tests as listed there.
Backend: Part A 1 and 2 tests; `createdByEmail` test.

## Acceptance criteria
- [ ] Backend build 0 warnings, all tests pass; frontend typecheck, lint, test, build pass (report outputs and the new chunk
      sizes).
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no client-side business rules (low stock, stock arithmetic,
      import limits stay server-side).
- [ ] `scripts/smoke/task-012.ps1`: camelCase keys (`to`, `sortBy`, `pageSize`), Vietnamese field name in a message,
      import of a smoke product + movement visible with `createdByEmail`, re-import of the same document → 409
      `stock_import_already_applied`, `lowStockOnly` filter contains the product, `npm run build` + `npm test`; run it and
      paste the table.
- [ ] Real-browser check like 011 (VI/EN measurements on the three new pages, including the import form with three lines and
      the result view). Admin only via `ui-check-61563` (lock again afterwards); otherwise smoke-style accounts.
- [ ] Manual check for the user documented (short).

## Out of scope
Stock adjustments / reversal of an import (no API), Excel import of lines, unsaved-changes guard, order links in the
movement history (013), realtime refresh (016), movement-table performance and indexes for unfiltered counts (026),
exports (020/021).

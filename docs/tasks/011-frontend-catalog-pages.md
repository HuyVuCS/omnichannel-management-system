# Task 011 — Frontend: Products, Customers, Channels pages (+ AntD 6, localised validation messages, code splitting)

Status: Open
Written by: Cowork, 2026-10-01
Depends on: 010 (done)

## Context
Second frontend task (doc §3.2: Sản phẩm, Khách hàng, Kênh bán hàng). Reuse the 010 patterns exactly (RTK Query endpoints per
feature, list state in the query string, `applyProblemToForm`, translated `errorCode`, layout constants, no hard-coded text,
language switch changes text only). Backend APIs exist since task 002 (+ SKU rules from 003).

## Part A — Follow-ups from review 010 (separate commits)
1. **AntD 6:** upgrade `antd` and `@ant-design/icons` to the latest 6.x and remove `@ant-design/v5-patch-for-react-19`. Keep all 010
   tests green, including 7b (language switch keeps layout) and the browser measurements (re-measure the Users page in VI/EN and put
   the numbers in the report). If the migration needs broad rewrites beyond the Users page and the shell, stop and report (`Blocked`).
2. **Localised validation messages (backend):** every custom FluentValidation message (`WithMessage`, custom `Must` rules) and every
   other user-facing validation text the API produces gets Vietnamese and English versions through **one** mechanism (e.g. `.resx`
   resources in `Application` with an English default and `vi` translation, used via the request UI culture). Keys and `errorCode`
   stay unchanged. Test: reflectively or by enumeration, every custom message resource exists in both cultures; integration test:
   SKU-format and inverted date-range errors come back in Vietnamese with `Accept-Language: vi`.
3. **`isLowStock` on product responses:** add `isLowStock` to `ProductResponse` (list and detail) using the **shared**
   `InventoryPredicates.IsLowStock` expression (no second implementation in C# or TypeScript). Update API tests.
4. **Route-level code splitting:** `React.lazy` + `Suspense` per page (fallback = AntD `Spin` sized to the content area so nothing
   jumps). The main chunk must drop well below the 500 kB warning; report chunk sizes.

## Part B — Pages
Shared rule: a page reachable by a role but with actions it may not use hides those actions (buttons/menu items), and the API stays
the authority (a 403 still shows a translated message).

### Products — `/products` (AnyStaff read; Manager/Admin write)
- Table: SKU, name, price (VND), available stock, low-stock tag (`isLowStock`, status colour), expiry date, created at.
  Server paging; sort `sku` (default), `name`, `price`, `createdAt`; debounced search (SKU/name).
- Create (Manager/Admin): SKU (shown upper-cased as typed, server rules), name, price (VND input, no decimals shown, ≥ 0),
  low-stock threshold (≥ 0; hint: "0 = warn only when out of stock"), expiry date (optional). 409 `product_sku_exists` on the SKU field.
- Edit (Manager/Admin): same fields except SKU (read-only). Stock is **not** editable here (link "Import stock" is task 012).

### Customers — `/customers` (AnyStaff read and write)
- Table: name, phone, e-mail, address (truncated + tooltip), created at. Paging; sort `name` (default), `createdAt`; search
  (name/phone/e-mail).
- Create/edit modal with the API's rules (e-mail format, phone characters).

### Channels — `/channels` (AnyStaff read; Admin write)
- Table (no paging): name, slug, type tag, status tag (Active/Inactive).
- Create (Admin): name, slug (lower-case pattern, hint with an example), type. 409 `channel_slug_exists` on the slug field.
- Edit (Admin): name, status. Slug and type read-only. Deactivating shows a confirm explaining that orders for this channel
  will then be rejected (`channel_not_found`).

### Common pieces
Extract only what the four list pages actually share (Users + three new), e.g. a hook binding table paging/sorting/search to the
query string. Do not build a generic CRUD framework.

## Tests (required)
Frontend:
1. Each page: list renders server data with paging/sorting/search reflected in the query string; reload keeps the state.
2. Each create/edit form: happy path; server validation errors land on the right fields; 409 codes on SKU / slug fields;
   the list refreshes only after success.
3. Role visibility: Staff sees no create/edit on Products and Channels but can create/edit customers; Manager can write products
   but not channels; Admin can do everything.
4. Language switch on each page keeps route, table state, an open form and its values (reuse the 7b approach) and texts change.
5. Low-stock tag follows `isLowStock` from the API (no client-side rule).
6. Code splitting: navigating to a page loads its chunk (Suspense fallback rendered, then content).
Backend: Part A 2 and 3 tests.

## Acceptance criteria
- [ ] Backend build 0 warnings, all tests pass; frontend typecheck, lint, test, build pass (report outputs and chunk sizes).
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no new client-side business rules (low stock, SKU normalisation are server-side).
- [ ] `scripts/smoke/task-011.ps1`: API checks for Part A (Vietnamese messages for SKU format and date range, `isLowStock` on
      products) + `npm run build` + `npm test`; run it and paste the table.
- [ ] Real-browser check like 010 (VI/EN measurements on the three new pages). Test accounts only via smoke-style names; no new Admin.
- [ ] Manual check for the user documented (short).

## Out of scope
Inventory pages and stock import UI (012), order entry/orders (013), product images (019), deletes.

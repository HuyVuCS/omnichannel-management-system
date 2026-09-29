# Review 002 — Catalog CRUD, inventory list, integration-test infrastructure

Reviewer: Cowork, 2026-09-29
Verdict: **Accepted**

## Checked
- Report, commits `daf481d`, `b16c026` (45 files), and in detail: `ProductService`, `InventoryQueryService`,
  `DbUpdateExceptionExtensions`, `PageQuery`/`PageQueryValidator`, `ProductsController`, `ApiFactory`.
- Controllers are thin; uniqueness is enforced by the database (23505 + constraint name → 409), no check-then-insert.
- Product + Inventory(0) created in one `SaveChanges`; list/detail share one projection; stable paging (`ThenBy(id)`).
- ILIKE input escaped, backed by a mutation check. Integration tests hit real PostgreSQL through one shared host.
- `.http` no longer holds credentials; `TreatWarningsAsErrors` on; 114/114 tests (88 unit, 26 integration).

## Deviations — accepted
1. Low-stock rule `LowStockThreshold > 0 AND AvailableStock <= LowStockThreshold` (threshold 0 disables the warning).
   The contradiction was in the task itself (rule vs. test 5), not in the implementation. Recorded in the implementation log;
   step 11 alerts must use the same rule.
2. `Infrastructure/Persistence/Inventories` namespace to avoid shadowing the `Inventory` entity.
3. README placement; explicit `HasDatabaseName` with unchanged index names.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Model-binding 400s (`pageSize=abc`, bad enum, malformed JSON) have no `errorCode` — inconsistent error contract for the frontend (also affects login). | Task 003 Part A |
| P3 | `PageQuery.Skip = (Page - 1) * PageSize` overflows `int` for huge `page` → negative `Skip` → 500. `Page` has no upper bound. | Task 003 Part A: cap `page` at 10,000 |
| P3 | SKU accepts any characters (spaces, `/`, unicode). SKUs travel to channel APIs and file exports. | Task 003 Part A: `^[A-Z0-9][A-Z0-9._-]*$` after upper-casing, ≤ 64 |
| P3 | `PUT /api/products/{id}` has no concurrency check (last write wins on catalog fields). | Accept for now; revisit if concurrent catalog editing becomes real |
| P3 | Expected unique violations are logged by EF at Error level → noise once error alerting exists. | Revisit in step 9 (observability) |

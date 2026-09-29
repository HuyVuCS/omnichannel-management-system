# Review 003 — Inventory import, movements, `inventory.changed` outbox

Reviewer: Cowork, 2026-09-29
Verdict: **Accepted**

## Checked
- Report, commits `98a88a1` (Part A) and `828ee18` (Part B); read in full: `InventoryImportService`, `InventoryPredicates`,
  `ICorrelationContext` + `HttpCorrelationContext` + `WorkerCorrelationContext`; migration SQL in the report.
- Import is correct: one explicit transaction; per item a single parameterised `UPDATE ... RETURNING` (no read-modify-write);
  movement + outbox row per item; one `SaveChanges`; unique violation → 409 with full rollback; unknown product → 400 with full
  rollback; `UpdatedAt/UpdatedBy` set explicitly because raw SQL bypasses the interceptor.
- Deadlock avoidance by `ProductId` lock order, and the test for it is proven effective (mutation check failed 3/3 without the sort).
- Low-stock rule defined once (`InventoryPredicates.IsLowStock`) and inlined into the projection — filter and flag cannot drift.
- Stock-write audit in the report: the only writers are the import `UPDATE` and the zero row at product creation.
- 165/165 tests (116 unit, 49 integration); migration is a straight index swap, safe on existing data.

## Deviations — accepted
1. `ICorrelationContext` abstraction (API: request header; Workers: per scope, set from the envelope in step 6+).
   Better than passing the id through controllers. **Task 004 is updated to use it.**
2. Movements query rejects `sortBy`, ignores `search`/`sortDir`; `from`/`to` converted to UTC. Fine.
3. Validation error keys are PascalCase (`Items[0].ProductId`) — consistent today, but see P2 below.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | Validation error keys are C# names (`Items[0].ProductId`) while the request JSON is camelCase (`items[0].productId`). The frontend (task 009+) maps errors to form fields by key, so the API contract should use the JSON names. Must be settled before the first frontend task. | Task 004 Part A |
| P3 | `TotalStock` is `integer`; an import pushing it past 2,147,483,647 raises PostgreSQL 22003 → 500. | Task 004 Part A: map 22003 on import to 409 `stock_limit_exceeded` |
| P3 | Lock order is the .NET `Guid` order of `ProductId` (same as PostgreSQL `uuid` order). The reservation path (task 006) must lock in the same order. | Noted in implementation log and roadmap for 006 |
| P3 | Expected unique violations still logged at Error by EF. | Step 9 (unchanged) |

## Manual step reminder
Apply migration `InventoryImportIdempotency` to the local database (command in the report).

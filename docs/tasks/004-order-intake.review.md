# Review 004 — Order intake `POST /api/orders/{slug}` (decision A)

Reviewer: Cowork, 2026-09-30
Verdict: **Accepted**

## Checked
- Report, commits `5333ae0` (Part A) and `b249f45`; read in full: `OrderIntakeService`, `ValidationConventions`,
  `OrdersController`, `SubmitOrderRequestValidator`; migration SQL in the report.
- Decision A implemented exactly: task + `order.created` outbox row in one `SaveChanges`, 202, no Order, no stock check,
  no RabbitMQ reference (asserted by a test on assembly references).
- Idempotency is race-safe: PostgreSQL makes the losing insert wait for the winner's commit before raising 23505, so the
  follow-up `SingleAsync` always finds the committed task; `ChangeTracker.Clear()` before re-reading is correct.
- Existence checks collect all failures into one 400 with camelCase keys; price snapshot taken at intake.
- Part A: error keys are JSON paths (process-wide resolver, also set in the test assembly for determinism); 22003 → 409.
- 193/193 tests (132 unit, 61 integration). Migration: nullable column + partial unique index, safe.

## Deviations — accepted
All five (resolver in the test assembly, `NotFoundException.errorCode`, separate `OrderCreatedPayload` record, pre-generated
outbox id, duplicate returns the original correlation id). The separate message record is the better design.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | A resubmission with the **same** external order id but **different content** (other items, quantities or customer) is answered `202 duplicate: true` and the new content is silently dropped. A channel that edits an order, or a client bug, becomes invisible. | Decision needed (see below) |
| P3 | Order items stay in request order in the payload; the Order Worker must lock inventory rows in `ProductId` order (see review 003). | Task 006 spec |
| P3 | `"system"` fallback actor is repeated in several services. | Leave; revisit if a third host appears |

### Decision needed for P2
- **Option 1 (recommended):** store a hash of the normalised content (customerId + items sorted by productId with stock) on
  the task; a resubmission with the same key but a different hash → 409 `order_conflict`. Same content → 202 duplicate as now.
  Needs one nullable column on `ProcessingTask`.
- **Option 2:** keep current behaviour and document that the first submission wins.

**Decided (user, 2026-09-30): option 1** — implemented in task 005 Part A.

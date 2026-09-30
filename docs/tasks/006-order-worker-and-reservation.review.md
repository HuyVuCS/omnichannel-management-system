# Review 006 — Order Worker, atomic reservation, attempt tracking

Reviewer: Cowork, 2026-09-30
Verdict: **Accepted**

## Checked
- Report, commits `3870ae8` (Part A) and `7c453ee`; read: `OrderReservationService.ReserveAsync` / `TryReserveAllAsync` /
  `StoreInsufficientStockOrderAsync` / `RecordTransientFailureAsync`, `ProcessingErrorClassifier`; handler flow and ACK rules in the report.
- Reservation: task row locked and re-checked inside the reservation transaction; per item a conditional
  `UPDATE ... WHERE "AvailableStock" >= @stock RETURNING ...` in `ProductId` order; Order + items + `Reserve` movement +
  `inventory.changed` outbox row per item + task/attempt completion in one commit; any short item → full rollback, then the
  `InsufficientStock` order in a fresh transaction after `ChangeTracker.Clear()`. ACK only after commit.
- Mutation evidence is strong and informative: without the stock condition the DB CHECK still prevents oversell but orders stall
  (the conditional update is the working check, the CHECK the last line of defence); without the `ProductId` sort real `40P01`
  deadlocks appear; the duplicate-delivery guard exists twice (handler + under the row lock).
- Stock-write audit repeated: only import, reservation and the zero row at product creation write stock.
- Part A (broker outages never fail outbox rows) proven by a replaced test with ≥ 5 backoff cycles and `MaxAttempts = 3`.
- 263/263 tests (183 unit, 80 integration), 4 full runs green. **Manual check confirmed by the user**: Reserved order with
  5/2/3 stock and a Reserve movement; InsufficientStock order with task `Succeeded` and `order.insufficient_stock` warning.

## Deviations — accepted
1. Confirmed rows of a batch interrupted by a broker failure are committed (avoids republishing delivered messages) — better than the task.
2. Concurrent duplicate deliveries: task row lock + re-check keeps stock correct; attempt history may show an overwritten `abandoned`. Fine.
3. Scoped, settable `WorkerCurrentUser` (`system:order-worker` on rows). 4. `order.already_exists` event. 5. Test 1 tolerance.

## Findings (all go into task 007, which owns retry/DLQ)
| Sev | Finding |
| --- | --- |
| P2 | Every failure — including non-transient ones (`unexpected`, CHECK `23514`, data errors `22xxx`) — is requeued immediately; a bug or bad data burns the 10 deliveries in seconds, then the broker dead-letters the message while the task stays `RetryScheduled`. No final `Failed` + visible error. |
| P2 | A closed consumer **channel** (as opposed to connection) is not re-created; the Order Worker silently stops consuming. |
| P3 | Tasks left `InProgress`/`RetryScheduled` when the broker dead-letters by `x-delivery-limit` — need finalisation. |
| P3 | Expected constraint violations still logged at Error by EF (step 9, unchanged). |

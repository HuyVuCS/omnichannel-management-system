# Review 024 — Payment step; Invoice Worker and Notification Worker with independent tasks; Part A (review 023)

Reviewer: Cowork, 2026-10-09
Verdict: **Accepted** — no P0–P2 finding. All ten deviations accepted. One P3 (question 2: simulated invoice numbers
can collide) and one small contract test go into **024b Part A**; question 4 is answered by 024b's tag map.

## Checked
- Report and commits `11bd1f1` (A1), `d634737` (Parts B–F + migration `PaymentAndInvoices`), `40a9398` (report).
  Migration additive: 5 × `AddColumn` (`PaymentStatus` `NOT NULL DEFAULT 'Unpaid'`, four nullable), `CreateTable
  Invoices`, `AddCheckConstraint CK_Orders_Payment_Consistent`, one unique index. Existing rows satisfy the check
  (default `Unpaid`, four nulls). No new package.
- Read in full: `OrderPaymentService.cs`, `Order.MarkPaid`, `OrderPaymentContracts.cs`, `InvoiceConfiguration.cs`, the
  migration, `TaskRequestProcessingService.cs`, `InvoiceProcessingService.cs`, `EmailProcessingService.cs`,
  `TaskRequestMessageHandler.cs`, `InvoiceMessageHandler.cs`, `EmailMessageHandler.cs`; the diffs of
  `OrderQueryService` / `OrderQueryContracts`, `RealtimeHintInterceptor`, `ProcessingErrorClassifier`,
  `SystemEventDetails`, `OrderConfiguration`; the invoice-number function of the simulator; `_common.ps1` /
  `_assert-selftest.ps1` of A1.
- Checked by Cowork against the code:
  - **Payment is one transaction under the order row lock.** `SELECT … FOR UPDATE` first, then the tracked read, both
    409 checks, `MarkPaid`, the invoice task + outbox row, the e-mail task + outbox row only for a non-blank address,
    `order.paid` — one `SaveChanges`, one commit. A refusal throws inside the transaction (rolled back by dispose).
    Only the `Orders` row is locked, so the payment cannot deadlock with the reserve path (which locks inventory rows).
    The domain guards repeat the rules (`MarkPaid`), and the check constraint repeats them in the database.
  - **`Order.Status` and stock untouched** — nothing in the payment path writes inventory or movements; `MarkPaid`
    sets the five payment fields only (mutant 07).
  - **No external call inside a transaction.** Both handlers: attempt committed first → read without a lock → provider
    / sender call with the host's stopping token → `CompleteTaskAsync` (task row locked, result stored, task
    `Succeeded`, `ProcessingFailed` alert resolved, one commit). A task completed by another delivery meanwhile only
    closes this attempt and stores nothing (mutant 19).
  - **One invoice per order** is decided by the key (`Invoices.OrderId` = PK, `INSERT … ON CONFLICT ("OrderId") DO
    NOTHING`) and the provider key `invoice:{orderId}` never contains the attempt number (mutant 15). A stored invoice
    completes the task without a call (mutant 13).
  - **Independence (doc §8.4).** `EmailProcessingService` never touches `Invoices`; `InvoiceMessageHandler` never looks
    at the e-mail task; neither changes the order. Both directions are tested with real PostgreSQL + RabbitMQ and by
    mutants 11–12.
  - **Secrets / personal data.** The payment reference is stored and shown on the detail but is not in `order.paid`
    nor in the allowlist (mutants 23–24); the sender logs `EmailAddress.Mask(...)` and the subject only (mutants 22,
    26); the log scan of the throwaway run found no full address, text or reference.
  - **Traceability.** Both workers record attempts with `WorkerName`, `WorkerInstanceId`, `AttemptNo`, correlation id
    from the envelope, `OrderId` in the log scope; rejected messages reuse `task_mismatch` / `unknown_task`.
  - **Realtime.** A modified `Order` → `order.changed` (one per saved order); the invoice row is written by raw SQL and
    gives no order hint, but its task's `processing.task.changed` carries `orderId`, so the open detail refetches.
  - **A1.** `SMOKE_SKIP_FRONTEND` is honoured only as `1` and only with `SMOKE_BASE_URL`; refused at script start on
    the local default; recorded as one passed step; nine self-test cases.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1756/1756; mutations 26/26 (one survivor of the
  first pass led to a new test); smoke self-test 64/64; `task-024.ps1` 51/51 on a throwaway environment and 52/52 on
  development (frontend build + tests included); migration applied on development as the migrator (exactly one
  pending before, none after). No frontend file changed.
- **Doc deviations 32–35 confirmed** as written in the log.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `AttemptNo` on `InvoiceRequest` / `EmailMessage` | Accepted — question 1. |
| 2 | `paidBy` is an actor field of the Logs API | Accepted — question 1. |
| 3 | `email_order_not_paid` next to `invoice_order_not_paid` | Accepted — a defensive guard; the payment step makes both unreachable today. |
| 4 | Invoice insert with `ON CONFLICT ("OrderId") DO NOTHING` | Accepted — better than catching 23505 inside the transaction. |
| 5 | One shared base for the two new workers | Accepted — question 1. |
| 6 | 409 order: already paid before not payable | Accepted — the more specific answer wins once paid orders can have other statuses. |
| 7 | `order.paid` without `taskId` (two tasks) | Accepted. |
| 8 | Smoke steps 44–48 (retry after address fix, alert clean-up) | Accepted — the run leaves no open alert. |
| 9 | Wrong-task rejections use `task_mismatch` | Accepted — no new label needed. |
| 10 | New folders (`Application/Invoices`, `…/Notifications`, `Infrastructure/ExternalProviders`, …) | Accepted. |

## Questions of the report
1. **`AttemptNo` on the provider contracts, `paidBy` as an actor field, the shared worker base — accepted, all three.**
   - `AttemptNo` follows `ChannelStockUpdate.AttemptNo` and keeps the simulators stateless; the rule that matters —
     it never enters the idempotency key — is pinned by mutant 15. A real adapter may log it; it must not send it as
     part of the key.
   - `paidBy` is exactly what `requestedBy` / `acknowledgedBy` are: the actor of a financial action, resolved to an
     e-mail on the Logs page.
   - The shared base (`TaskRequestMessageHandler`, parser, `ITaskRequestProcessing`, `ProviderWorkerOptions`) is
     justified: two workers with identical delivery / attempt / failure handling, the export worker untouched. **Do not
     move the Report Export Worker onto it** as a refactor; revisit only when that worker changes for another reason
     or a third `{ taskId }` worker appears (recorded in the log).
2. **Invoice-number collisions — lengthen, in 024b A1.** 32 bits of hash give a birthday collision at a few thousand
   invoices per month (the report's ~1 % at 9,000 is right), and the effect is bad: the unique index fails the
   completion, the deterministic number fails every retry the same way, the task ends `Failed`, and a manual retry
   can never succeed — a permanent dead task caused by the simulator alone. 16 hex digits (64 bits) make it
   negligible (≈ 2 × 10⁻¹² at 9,000/month); `SIM-{yyyyMM}-{16 hex}` is 24 characters (column: 64). No migration:
   stored numbers stay; an in-flight task after the change gets the longer number, which is harmless because nothing
   was stored for it (one invoice per order is decided by `OrderId`, not by the number). Not reclassifying the
   collision as permanent: with 64 bits it does not occur, and a real provider guarantees unique numbers.
3. **A confirmation e-mail can be sent twice — accepted (at-least-once).** Exactly-once across a non-transactional
   sender is not achievable; the window is only between the send and the completion commit (crash, lost database,
   host killed without graceful stop). The alternative — marking "sent" before the call — turns it into at-most-once
   and loses a payment confirmation on the same crash, which is worse than a rare duplicate, and the attempt history
   shows what happened either way. The key `email:payment:{orderId}` is already passed: when a real mail provider is
   integrated, its adapter maps the key to that provider's idempotency / deduplication feature **if it has one**
   (not every provider does — check at that time). Recorded in the log with that trigger. The invoice has the same
   window but no visible effect (deterministic number, one row per order).
4. **`order.changed` — mapped in 024b.** Task hints already refresh an open detail; what they do not cover is the
   payment itself seen from **another session** (detail and list), because the payment response is only in the
   paying session. 024b maps `order.changed` with `orderId` to the order and the order lists; `all` → every order
   entry. Not `DashboardSummary`: the summary has no payment figure.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **Simulated invoice numbers collide** at a few thousand invoices a month and turn the task into one that can never succeed (question 2). | **024b A1:** 16 hex digits; tests and README. |
| P3 | **An unknown `paymentMethod` text** is refused by model binding with an error key no test pins (report follow-up). The 024b dialog puts field errors on fields by key, so the key is part of the contract. | **024b A1:** one integration test pins the 400 and its key (`paymentMethod` expected; if the binder answers with another key, report it — the frontend sends only known values, so no code change unless the key is unusable). |
| — | Attempt error codes of the new workers (`invoice_provider_*`, `email_*`) have no labels. | **Decision for 024b:** attempt error codes stay raw everywhere, as the `channel_*` codes have since 016 (they are the search key in logs and the message column carries the readable text); 024b labels event types and payment values, not attempt codes. Log entry. |
| — | The redirected console log of a script-started host shows Vietnamese subjects with replaced characters. | No action: code page of the redirect, not of the application (Serilog output itself is correct). |
| — | The dashboard has no "paid orders" figure for the doc's "200 paid / 50 invoices / 30 e-mails" view; the processing card already counts Invoice and Email tasks by status. | Not planned; would need a summary field. Candidate if the user asks for it. |

## Assessment
A clean, well-bounded piece of work that implements the doc's central reliability example literally: payment, invoice
and e-mail are three independent facts with independent histories, and the tests prove the independence in both
directions with real infrastructure instead of asserting it. The transaction boundaries are exactly where they belong
(payment + queued work in one; provider calls outside; completion in one), the idempotency of the invoice rests on the
order's key rather than on luck, and the personal-data rules (masked addresses, reference out of the audit payload)
are enforced by tests, not comments. The shared worker base is a justified abstraction — introduced for two concrete
consumers, without touching the export worker. The only real defect is in the simulator's number length, which the
report found and quantified itself.

## Manual check
None for the user. Optional demo: `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024.ps1`, or the payment
sample in `backend/src/Omnichannel.Api/Omnichannel.Api.http`; README section 10.

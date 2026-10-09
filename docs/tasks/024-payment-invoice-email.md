# Task 024 — Payment step; Invoice Worker and Notification (e-mail) Worker with independent tasks; Part A (review 023)

Status: Open
Written by: Cowork, 2026-10-09
Depends on: 023 (accepted)

## Context
Doc §8.4 ("Payment Succeeded → invoice.requested → Invoice Worker; email.requested → Notification Worker. Each task has
independent status and attempts. One failed email must not make the whole Order Failed."), §5.3 (the "200 customers
paid, 50 invoices, 30 e-mails" example), §10 (routing keys and queues `invoice.processing`, `notification.email` —
already declared since 005), §14 (no single status for payment / invoice / e-mail / sync); CLAUDE.md.

**Decision (user, 2026-10-09):** payment is a **simulated payment step** — a Manager/Admin marks a `Reserved` order as
paid; that is the doc's "Payment Succeeded". Every other decision is in the implementation log, "Task 024 plan" —
read it first; doc deviations 32–35 are listed there. **This task is backend only** (as 020): the frontend shows the new
event types raw and has no payment button until **024b**; no frontend file changes here.

Read first: implementation log "Task 024 plan", "Task 014 plan/result" (simulated external system behind Polly, error
classes), "Task 020 plan/result" (a worker with `{ taskId }` messages, completion transaction, alerts), "Task 007
plan" (retry tiers, final failure), "Task 008 plan" (manual retry), "Task 015 plan" (hint interceptor), "Task 018b
result" (allowlist), "Task 023 result"; review 023; backend `Order.cs`, `OrderQueryService` / `OrderQueryContracts`
(detail response, `createdByEmail` lookup), `OrdersController`, `OrderReservationService` (row locking), the Report
Export Worker (`ReportExportMessageHandler`, `ReportExportProcessingService`, parser, options, service) and the Stock
Sync Worker (`StockSyncMessageHandler`, `ResilientChannelStockClient`, `SimulatedChannelStockClient`,
`ChannelSimulationOptions`), `ProcessingAttemptStore`, `ProcessingErrorClassifier`, `MessageRejection`,
`RabbitMqTopology`, `RealtimeHintInterceptor`, `SystemEventDetails`, `WorkerHostSetup`; `scripts/smoke/_common.ps1`,
`task-020.ps1`.

**Stop and report (`Blocked`)** if: paying would have to change `Order.Status` or any stock to work; an invoice or
e-mail task would need to read or wait for the other; a provider or sender call would have to run inside a database
transaction; the migration needs anything but additions (a `NOT NULL` column with a default is an addition); or a
decision is needed that is not in this file or the log.

## Part A — Follow-up of review 023 (one commit, first)

### A1. Skip the frontend step of a smoke script for a foreign environment (P3, review 023)
`Invoke-FrontendChecks` (`scripts/smoke/_common.ps1`) honours `SMOKE_SKIP_FRONTEND=1`: it records one step
"frontend checks skipped (SMOKE_SKIP_FRONTEND)" with expected = actual, so the table shows it. Allowed only together
with `SMOKE_BASE_URL`; on the local default the variable makes the script fail at its start with a clear message.
`_assert-selftest.ps1` (if it covers the function) and README "Smoke scripts". (The `unexpected` label of review 023 is
frontend work: it goes to 024b.)

## Part B — Payment

### B1. Model and migration (one additive migration with C1)
`Orders`: `PaymentStatus` varchar(32) not null default `'Unpaid'`, `PaidAt` timestamptz, `PaidBy` varchar (as the
audit columns), `PaymentMethod` varchar(32), `PaymentReference` varchar(100); check constraint: `Unpaid` ⇔ the four
others null, `Paid` ⇔ `PaidAt`, `PaidBy`, `PaymentMethod` set. Enums `PaymentStatus`, `PaymentMethod` (strings, as
every enum). `Order.MarkPaid(method, reference, paidBy, paidAt)` guards the rules in the domain too.

### B2. `POST /api/orders/{orderId}/payment` (Manager/Admin)
- Body `{ paymentMethod, paymentReference? }` (FluentValidation; reference trimmed, ≤ 100, blank = none).
- One transaction: the order row locked (`FOR UPDATE`); 404 unknown; 409 `order_not_payable` unless `Status =
  Reserved`; 409 `order_already_paid` when `Paid`; then the payment columns, `SystemEventLog` `order.paid`
  (Information; allowlist `orderId`, `paymentMethod`, `amount`, `paidBy`; never the reference), a
  `ProcessingTask(Invoice)` (`OrderId` set, key `invoice:{orderId}`) + outbox `invoice.requested` (payload
  `{ taskId }`), and — only when the customer has an e-mail address — `ProcessingTask(Email)` (key
  `email:payment:{orderId}`) + outbox `email.requested` (payload `{ taskId }`). Localised messages (vi/en).
- 200 `{ orderId, paymentStatus, paidAt, invoiceTaskId, emailTaskId | null }`.
- Two concurrent requests for one order → one 200, one 409 `order_already_paid`, one invoice task (test).

### B3. Order responses
`OrderDetailResponse` gains `payment { status, paidAt, paidByEmail, method, reference } ` (`paidByEmail` resolved like
`createdByEmail`; reference shown to every role that reads orders) and `invoice { number, issuedAt } | null`. The list
response gains `paymentStatus`. No new list filter.

### B4. Realtime
`RealtimeHintInterceptor` also captures a **modified** `Order` → `order.changed` with `orderId` (one hint per saved
order; same `NOTIFY` and size rules). Unit test as for the other hint kinds; the frontend maps it in 024b.

## Part C — Invoice Worker

### C1. Model (same migration as B1)
`Invoices`: `OrderId` PK + FK → `Orders` (1:1, no separate id), `InvoiceNumber` varchar(64) unique, `IssuedAt`,
`TotalAmount` numeric (snapshot of the order's total), `ProviderReference` varchar(128) (the idempotency key sent),
audit fields. `ProcessingTaskType.Invoice` exists already.

### C2. Provider
`IInvoiceProviderClient.IssueAsync(InvoiceRequest { key, orderId, externalOrderId, customer name, lines, total,
paidAt }, ct)` → `{ invoiceNumber, issuedAt }`; failures as `InvoiceProviderException(code, message, isTransient)` with
`invoice_provider_timeout`, `invoice_provider_unavailable`, `invoice_provider_rejected` (permanent),
`invoice_provider_circuit_open`. `ResilientInvoiceProviderClient` (timeout + circuit breaker, **no retry inside** — log
014) in front of `SimulatedInvoiceProviderClient`: section `InvoiceSimulation` (`Mode`, `LatencyMs`, `Rules`), live
reload; Development-only rules by external order id prefix `SIM-INV-PERM-` (rejected) / `SIM-INV-TEMP-` (unavailable on
attempt 1); number `SIM-{yyyyMM of paidAt}-{first 8 hex of SHA-256(key)}` — deterministic, so a repeated call for one
key returns the same number without state.

### C3. Worker (`InvoiceWorker`, queue `invoice.processing`, options as the other workers: concurrency 1, `MaxAttempts` 5)
terminal task → ACK; attempt committed first; order + lines + customer read without a lock; an invoice already stored
for the order → complete without a call; order not `Paid` → permanent `invoice_order_not_paid`; provider call outside
any transaction; one transaction: invoice row (a unique violation on `OrderId` = stored by another delivery → fine) +
attempt and task `Succeeded` + the task's `ProcessingFailed` alert resolved (as `CompleteAsync` of the export worker);
ACK. Transient → delay tiers; permanent / exhausted → final failure as every worker; poison → `invoice.message_rejected`
with `reasonCode` (allowlisted) and DLQ. Host stopping during the call → abandon (as stock sync).

## Part D — Notification Worker

### D1. Sender
`IEmailSender.SendAsync(EmailMessage { key, to, subject, textBody }, ct)`; failures `EmailSendException` with
`email_timeout`, `email_unavailable`, `email_rejected` (permanent), `email_circuit_open`. `ResilientEmailSender`
(timeout + breaker) in front of `SimulatedEmailSender`: section `EmailSimulation` (`Mode`, `LatencyMs`, `Rules`),
Development-only rules by recipient domain `@sim-reject.test` (rejected) / `@sim-temp.test` (unavailable on attempt 1);
it logs the subject and a masked address (`a***@domain`), never the body or the full address.

### D2. Worker (`NotificationWorker`, queue `notification.email`, same options shape)
As C3, but: the recipient is the customer's **current** e-mail address (none → permanent `email_no_recipient`); the
message is the payment confirmation built at send time — subject and text with the external order id, lines (SKU,
name, quantity, unit price), total and paid-at in `Notifications:Language` (default `vi`, `en` allowed;
`Notifications:TimeZone` default `Asia/Ho_Chi_Minh` for the paid-at time) from resources, not string concatenation in
code; key `email:payment:{orderId}`; completion: attempt and task `Succeeded`, alert resolved. **It never reads or
writes `Invoices`**; poison → `email.message_rejected`.

## Part E — Tests (required)
Unit: `Order.MarkPaid` guards; hint capture of a modified order; signature-free parts of both simulators (modes, rules,
deterministic invoice number, masked address); confirmation text in vi and en; options validators.
Integration (API): payment 200 with both task ids and outbox rows in the same transaction (failure injection before
commit → nothing stored); without customer e-mail → no e-mail task; 404; 409 `order_not_payable` (`InsufficientStock`,
`Pending`), 409 `order_already_paid`; concurrent payments → one wins; Staff → 403; validation keys en/vi; `order.paid`
event and allowlist (no reference); detail and list responses (`payment`, `invoice`, `paymentStatus`); stock and
`Order.Status` unchanged.
Integration (Workers): invoice success (row, number deterministic, task / attempt `Succeeded`, alert resolved after a
manual retry of a failed one); redelivery after the provider call and before commit → one invoice, same number;
`SIM-INV-TEMP-` → retry then success; `SIM-INV-PERM-` → `Failed`, `ProcessingFailed` alert, DLQ; e-mail success;
`@sim-reject.test` → e-mail `Failed` while **the invoice task of the same order succeeds and the order is unchanged**
(the doc §8.4 case); address removed after payment → `email_no_recipient`; poison messages of both queues; host stopping
during a call → abandoned and redelivered; logs contain no e-mail body and no full address.
End to end: order intake → reserve → pay → invoice and e-mail tasks `Succeeded`, order detail shows the invoice.
Mutations: about fifteen (payment preconditions, the e-mail-only-with-address rule, independence, idempotent invoice,
completion transaction) — each killed; list in the report.

## Part F — Smoke script `scripts/smoke/task-024.ps1`
Strict `Assert-Step`; API + Workers; smoke Manager and Staff; product, stock 10; customer A with
`…@example.com`, customer B without e-mail, customer C with `…@sim-reject.test`; one order each through
`POST /api/orders/{slug}` → `Reserved`:
1. Staff pays A → 403. Manager pays A (`Cash`) → 200 with both task ids; both tasks `Succeeded` (bounded wait); the
   detail shows `payment.status` `Paid` and an invoice number starting `SIM-`; the order status is still `Reserved`
   and the stock unchanged.
2. Pay A again → 409 `order_already_paid`.
3. Pay B → 200, `emailTaskId` null; its invoice task `Succeeded`.
4. Pay C → its e-mail task ends `Failed` (`email_rejected`) with an open `ProcessingFailed` alert, **its invoice task
   `Succeeded`**.
5. An `InsufficientStock` order → 409 `order_not_payable`.
6. `order.paid` events for A, B, C on `GET /api/system-events`.
7. Clean-up: smoke users locked. Then `Invoke-FrontendChecks`.

## Acceptance criteria
- [ ] Part A first, own commit; Parts B–D may share commits (the migration in the commit that adds the model).
- [ ] Backend build 0 warnings, all tests pass; mutation list in the report.
- [ ] Migration additive only; on development `migrations list` shows exactly this task's one migration pending, then
      `database update` as the migrator.
- [ ] No frontend change; `Invoke-FrontendChecks` in the smoke run green.
- [ ] `scripts/smoke/task-024.ps1` on development; table in the report.
- [ ] README: "Payment, invoices and e-mails" (the step, roles, what is queued and when, independence, simulators and
      their Development rules, configuration, error codes), events table, §6 table of workers and queues; `.http`
      sample for the payment.
- [ ] "Development steps (applied)" in the report; "Manual steps for the user": none, or only a step that could not be
      applied, with the reason.

## Out of scope
Frontend (024b: payment on the order detail, invoice display, labels of the new task outcomes and event types, the
`unexpected` label, the `order.changed` hint); refunds, partial or several payments, cancelling a payment; fulfilment /
`Completed` / stock `Ship`; invoice documents (PDF) and their storage; e-mails other than the payment confirmation;
real invoice providers or mail servers; customer language preferences; payment by webhook; exports with payment
columns.

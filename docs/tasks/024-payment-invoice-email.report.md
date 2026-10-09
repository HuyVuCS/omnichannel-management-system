# Report 024 — Payment step; Invoice Worker and Notification Worker with independent tasks; Part A (review 023)

Status: Done
Commit: `11bd1f1` (Part A1), `d634737` (Parts B–F with the migration); this report in its own commit

## Summary
A Manager or Admin can mark a `Reserved` order as paid (`POST /api/orders/{orderId}/payment`). In one transaction
under the lock of the order row the payment fields, the audit event `order.paid`, an `Invoice` task with
`invoice.requested` and — only when the customer has an e-mail address — an `Email` task with `email.requested` are
stored; `Order.Status` and every stock stay as they are. Two new workers consume the queues that exist since 005: the
Invoice Worker issues the invoice at a simulated provider (call outside any transaction, key `invoice:{orderId}`) and
stores it in the new `Invoices` table (one per order); the Notification Worker sends the payment confirmation (vi / en
from resources) through a simulated sender to the customer's current address. The two tasks never read or wait for
each other: a refused e-mail leaves the invoice issued and the order `Reserved` and `Paid` (doc §8.4), and the other
way round. Part A1: `SMOKE_SKIP_FRONTEND=1` for a foreign environment. Migration `PaymentAndInvoices` (additive) is
applied on development; smoke 52/52 there. No frontend file changed.

Nothing was blocking. Three small decisions I took are listed under "Deviations" and repeated as questions.

## Files changed
**Part A1 (`11bd1f1`)**
- `scripts/smoke/_common.ps1` — `Test-SkipFrontend` (only the value `1`, only with `SMOKE_BASE_URL`; on the local default
  the script stops at its start with a message that names both variables); `Invoke-FrontendChecks` records the one step
  `NN frontend checks skipped (SMOKE_SKIP_FRONTEND)` with expected = actual.
- `scripts/smoke/_assert-selftest.ps1` — 9 cases for the switch (64 in all).
- `README.md` — new section "Smoke scripts".

**Parts B–F (`d634737`)**
- Domain: `Enums/PaymentStatus.cs`, `Enums/PaymentMethod.cs`; `Entities/Order.cs` (payment fields, `MarkPaid` with its
  guards); `Entities/Invoice.cs` (`OrderId` key, `ProviderKeyOf`).
- Persistence: `Configurations/OrderConfiguration.cs` (columns, default `Unpaid`, `CK_Orders_Payment_Consistent`),
  `InvoiceConfiguration.cs`, `OmnichannelDbContext.cs`, `Migrations/20261009133050_PaymentAndInvoices*.cs` + snapshot.
- Payment: `Application/Orders/OrderPaymentContracts.cs` (request, response, validator, `PaymentTaskKeys`,
  `OrderPaymentEvents`), `IOrderPaymentService.cs`, `OrderIntakeContracts.cs` (two error codes);
  `Infrastructure/Persistence/Orders/OrderPaymentService.cs`; `Api/Controllers/OrdersController.cs` (the action, thin).
  Resources: `OrderNotPayable`, `OrderAlreadyPaid` (vi/en); field names `paymentMethod`, `paymentReference` (vi/en).
- Order responses: `Application/Orders/OrderQueryContracts.cs` (`payment`, `invoice`, `paymentStatus`),
  `Infrastructure/Persistence/Orders/OrderQueryService.cs` (still one statement for the detail).
- Realtime: `Application/Realtime/RealtimeHints.cs` (`order.changed`), `Infrastructure/Realtime/RealtimeHintInterceptor.cs`.
- Messages and events: `Common/Messaging/PaymentFollowUpPayloads.cs`, `MessageEnvelope.cs` (task id of the two events),
  `EventTypes.cs`, `MessageRejection.cs` (docs), `SystemEvents/SystemEventDetails.cs` (three entries, actor field `paidBy`).
- What both workers share: `Application/Processing/ITaskRequestProcessing.cs`,
  `Infrastructure/Persistence/Processing/TaskRequestProcessingService.cs` (task state, attempts, rejected message,
  completion in one transaction), `Workers/Processing/TaskRequestMessageParser.cs`, `TaskRequestMessageHandler.cs`
  (delivery, attempt, retry / fail / abandon), `ProviderWorkerOptions.cs`, `ProcessingErrorClassifier.cs` (the two
  provider exceptions keep their code).
- Invoice Worker: `Application/Invoices/IInvoiceProviderClient.cs` (contract, exception, error codes, `InvoiceEvents`),
  `IInvoiceProcessingService.cs`; `Infrastructure/Persistence/Invoices/InvoiceProcessingService.cs`;
  `Workers/InvoiceWorker/InvoiceMessageHandler.cs`, `InvoiceWorkerOptions.cs`, `InvoiceWorkerService.cs`.
- Notification Worker: `Application/Notifications/IEmailSender.cs` (contract, exception, error codes,
  `NotificationEvents`, `EmailAddress.Mask`), `IEmailProcessingService.cs`, `PaymentConfirmationEmail.cs`
  (`NotificationsOptions`), `NotificationTexts.cs`, `Resources/NotificationTexts.resx` / `.vi.resx`;
  `Infrastructure/Persistence/Notifications/EmailProcessingService.cs`; `Workers/NotificationWorker/EmailMessageHandler.cs`,
  `NotificationWorkerOptions.cs` (with `NotificationsOptionsValidator`), `NotificationWorkerService.cs`.
- Providers: `Infrastructure/ExternalProviders/ProviderSimulation.cs` (modes, latency, rules switch),
  `SimulatedInvoiceProviderClient.cs`, `SimulatedEmailSender.cs`, `ProviderResilience.cs` (timeout + breaker, no retry),
  `ResilientProviderClients.cs` (both wrappers, `AddSimulatedPaymentProviders`).
- Hosts: `Infrastructure/DependencyInjection.cs`, `Workers/WorkerHostSetup.cs`, `Workers/Program.cs` (comment),
  `Workers/appsettings.Development.json` (`InvoiceSimulation`, `EmailSimulation` with `Rules: true`).
- Tests (new): `Domain/OrderPaymentTests.cs`, `Workers/PaymentProviderTests.cs`, `Workers/PaymentWorkerHandlerTests.cs`,
  `Notifications/PaymentConfirmationEmailTests.cs`, `Integration/OrderPaymentEndpointsTests.cs`,
  `Integration/PaymentEndToEndTests.cs`, `Integration/PaymentTesting.cs`, `Integration/Workers/PaymentWorkersTests.cs`
  + `.Failures.cs`, `Integration/Workers/PaymentWorkersTesting.cs` (probe). Extended: `Realtime/RealtimeHintUnitTests.cs`
  (+2), `SystemEvents/SystemEventDetailsTests.cs`, `Integration/SystemEventEndpointsTests.cs` (+3 detail cases).
- `scripts/smoke/task-024.ps1`; `README.md` (section 6 table of workers and queues, new section 10 "Payment, invoices and
  e-mails", hints, events table, rejection codes, alerts); `Omnichannel.Api.http` (payment sample).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` (backend, state of `d634737`) | 0 errors, **0 warnings** |
| `dotnet test` (backend, state of `d634737`) | **1756 passed**, 0 failed (3 m 45 s) — 197 more than 023 |
| `dotnet test --filter "FullyQualifiedName~Payment\|FullyQualifiedName~OrderPayment"` | 189 passed (35 s), the end-to-end test included |
| `dotnet test` before any new test, after the model change | 1561 passed, 2 failed — both the allowlist completeness tests (expected; updated) |
| Mutation run, one targeted test filter each | **26 of 26 killed** (list below; one survivor of the first pass led to a new test) |
| `scripts\smoke\_assert-selftest.ps1` | PASSED: all 64 cases; with `SMOKE_SKIP_FRONTEND=1` and no `SMOKE_BASE_URL`: exit 1 with the refusal |
| `dotnet ef migrations add PaymentAndInvoices` | generated; reviewed: 5 × `AddColumn`, `CreateTable`, `AddCheckConstraint`, `CreateIndex` — additions only |
| Throwaway environment (containers of its own, 14 migrations as the migrator, hosts in environment `Smoke`) + `task-024.ps1` | **PASSED: all 51 checks** (frontend step skipped with `SMOKE_SKIP_FRONTEND=1`, shown as step 51) |
| `task-024.ps1` on development | **PASSED: all 52 checks**, `npm run build` and `npm test` included |
| Log scan of the throwaway Workers / API logs | 0 full addresses, 0 lines of an e-mail text, 0 payment references; the three "sent" lines show `s***@example.com` / `s***@sim-reject.test` |

Frontend: no file changed, so no typecheck / lint / five-run series was run; the only frontend evidence is the green
`npm run build` + `npm test` of the development smoke run.

### Mutations (26 of 26 killed)
| # | Mutant | Killed by |
| --- | --- | --- |
| 01 | payment service lets an `InsufficientStock` order pass the status check | `An_order_that_is_not_reserved_cannot_be_paid_and_nothing_is_written` |
| 02 | `Order.MarkPaid` accepts every status but `Cancelled` | `Only_a_reserved_order_can_be_paid` |
| 03 | the already-paid check of the service never fires | `A_second_payment_is_refused_and_the_first_stays_as_it_was` |
| 04 | `Order.MarkPaid` pays a paid order again | `An_order_is_paid_once_and_the_first_payment_stays` |
| 05 | the order row is not locked (`FOR UPDATE` removed) | `Of_several_payments_at_once_one_wins_and_the_order_gets_one_invoice_task` |
| 06 | every role may pay (`AnyStaff`) | `Staff_is_forbidden_an_anonymous_caller_unauthorized_and_an_admin_may_pay` |
| 07 | paying sets `Status = Completed` | `Paying_records_the_payment_and_changes_neither_the_status_nor_the_total` |
| 08 | a blank address still gets an e-mail task (`is null` only) | `A_customer_without_an_e_mail_address_…(email: "")` |
| 09 | a customer without an address gets an e-mail task | `A_customer_without_an_e_mail_address_…(email: null)` |
| 10 | the worker sends although the address is blank | `A_customer_without_an_address_fails_the_e_mail_for_good_and_nothing_is_sent` |
| 11 | the e-mail waits for the invoice of the order | `A_refused_invoice_fails_its_own_task_while_the_e_mail_of_the_same_order_is_sent` |
| 12 | the invoice waits for the e-mail task of the order | `A_rejected_e_mail_fails_its_own_task_while_the_invoice_of_the_same_order_succeeds_and_the_order_is_unchanged` |
| 13 | the provider is called although the invoice is stored | `An_order_that_has_its_invoice_completes_the_task_without_a_call_to_the_provider` |
| 14 | no `ON CONFLICT` on the invoice insert | `An_invoice_stored_by_another_delivery_while_this_one_called_the_provider_stays_and_the_task_completes` |
| 15 | the provider key depends on the attempt | `An_invoice_is_issued_with_the_key_of_its_order_and_stored_with_the_total_of_the_order` |
| 16 | the simulator's invoice number does not depend on the key | `A_second_call_for_one_key_answers_with_the_same_number_without_any_state` |
| 17 | the task succeeds without its invoice being stored | `A_paid_order_gets_its_invoice_and_its_confirmation_by_two_independent_tasks` |
| 18 | the alert of an earlier failure is not resolved at completion | `A_provider_that_stays_down_fails_the_invoice_after_the_last_attempt_and_a_manual_retry_issues_it_and_resolves_the_alert` |
| 19 | a task another delivery completed is completed again | `A_task_completed_by_another_delivery_while_this_one_called_the_provider_only_closes_its_attempt` (**added for this mutant**: it survived the first pass) |
| 20 | a delivery for a finished task starts an attempt | `A_delivery_for_a_finished_task_is_acknowledged_without_an_attempt` |
| 21 | a refusal of the provider is retried (classifier) | `An_answer_of_the_invoice_provider_keeps_its_code_and_says_itself_whether_a_retry_can_help` |
| 22 | `EmailAddress.Mask` returns the address | `The_sender_logs_the_subject_and_a_masked_address_and_never_the_text_or_the_full_address` |
| 23 | the payment reference is put into the `order.paid` payload | `A_payment_stores_the_payment_two_tasks_their_messages_and_the_audit_event_…` |
| 24 | the allowlist returns `paymentReference` | `Details_are_the_allowed_fields_of_the_payload_and_nothing_else(order.paid)` |
| 25 | a modified order gives no hint | `A_modified_order_gives_one_order_changed_with_its_id_and_an_unchanged_one_gives_nothing` |
| 26 | the simulated sender logs the full address | `The_sender_logs_the_subject_and_a_masked_address_and_never_the_text_or_the_full_address` |

Mutant 10 did not compile in its first form (nullable analysis) and was rewritten as `is null`.

### What the tests cover (Part E, by the task's list)
- Unit: `Order.MarkPaid` guards and the invoice entity; hint of a modified order (and its place before the two task
  hints); both simulators (modes, latency, rules on / off, deterministic number, masked address, live settings change);
  timeout and breaker of both providers (own codes, own options, no retry inside, a refusal does not open the breaker);
  the confirmation text in vi and en, exact; options validators; the parser and both handlers step by step.
- API: 200 with both task ids, tasks, outbox rows, event and hints of the one transaction; commit failure → nothing
  stored and the payment can be made again; no / blank e-mail → no e-mail task; 404; 409 `order_not_payable`
  (`InsufficientStock`, `Pending`, `Cancelled`, `Completed`); 409 `order_already_paid`; 8 concurrent payments → one
  200, seven 409, one invoice task; Staff 403, anonymous 401; validation keys and messages en / vi; `order.paid` on the
  Logs API without the reference and with the payer's e-mail; `payment` / `invoice` on the detail, `paymentStatus` in
  the list; stock and `Order.Status` unchanged.
- Workers (real PostgreSQL + RabbitMQ): success of both; redelivery after the provider call and before the commit → one
  invoice, same number; an invoice stored by another delivery meanwhile stays; a task completed by another delivery
  meanwhile; a stored invoice → no call; duplicate delivery → no second attempt, call or e-mail; `SIM-INV-TEMP-` and
  `@sim-temp.test` → retry then success; `SIM-INV-PERM-` and `@sim-reject.test` → `Failed`, event, alert, DLQ, with the
  other task of the same order `Succeeded` and the order unchanged (both directions); provider down for good → `Failed`
  after `MaxAttempts`, manual retry → `Succeeded`, alert `Resolved`; timeout codes; address removed →
  `email_no_recipient`; address changed → the new one; tasks of an unpaid order; seven poison messages on both queues;
  host stopping during a call → abandoned, redelivered, second attempt succeeds; two instances → one invoice and one
  e-mail per order; no address and no text of an e-mail in any log line, scope value or exception.
- End to end: intake → reserve → pay → both tasks `Succeeded` → the order detail shows the invoice.

## Deviations from the task
1. **`AttemptNo` on `InvoiceRequest` and `EmailMessage`.** The task's shapes have none, but the Development rules
   "unavailable on attempt 1" need it and the simulators must stay stateless. Same as `ChannelStockUpdate.AttemptNo`.
2. **`paidBy` is an actor field of the Logs API** (`SystemEventDetails.ActorFields`), so `actorEmail` of `order.paid`
   is the payer's address. The task only named the allowlist.
3. **`email_order_not_paid`** exists next to `invoice_order_not_paid` (D2 says "as C3"; the confirmation needs
   `PaidAt`). Neither can come from the payment step — it writes the tasks with the payment.
4. **Invoice insert with `ON CONFLICT ("OrderId") DO NOTHING`** (one raw statement in the completion transaction,
   audit columns set explicitly) instead of catching a unique violation: nothing fails inside the transaction, and no
   entry in `ExpectedUniqueViolations` is needed.
5. **One shared base for the two workers** (`TaskRequestMessageHandler`, `TaskRequestMessageParser`,
   `ITaskRequestProcessing` / `TaskRequestProcessingService`, `ProviderWorkerOptions`) instead of two more copies of the
   export worker. The existing three workers are untouched. Both workers name their timeout `CallTimeout`.
6. **Order of the two 409 checks:** already paid first, then not payable. Today a paid order is always `Reserved`, so
   the answers are the same as with the task's order; a later `Completed` paid order would answer `order_already_paid`.
7. **`order.paid` has no `taskId`** in the event's own column (a payment has two tasks); the order id is set.
8. **Smoke script, steps 44–48** beyond the task's list: the address of customer C is corrected and the failed e-mail
   task retried (it succeeds, its alert is resolved), and one import resolves the insufficient-stock alert of step 34 —
   so the run leaves no open alert on development.
9. **Rejections of a wrong task use the existing `task_mismatch`** (now also "not an invoice / e-mail task with its
   order"); no new rejection code, so 024b needs no new label for it.
10. New folders next to the documented ones: `Application/Invoices`, `Application/Notifications`,
    `Infrastructure/ExternalProviders`, `Infrastructure/Persistence/Invoices`, `…/Notifications`.

## Blocked / questions for Cowork
Not blocked. For the review:
1. **Deviations 1, 2 and 5** — are `AttemptNo` on the provider contracts, `paidBy` as an actor field, and the shared
   worker base accepted?
2. **Simulated invoice numbers can collide.** `SIM-{yyyyMM}-{8 hex}` is what the task specifies; two orders of one
   month can get the same number (about 1 % at 9,000 invoices a month). The unique index on `InvoiceNumber` then fails
   that insert, the attempt is `unexpected`, and the task ends `Failed` after its attempts. A property of the simulator
   only; a longer hash would end it. Leave, or lengthen in 024b / 026?
3. **An e-mail can be sent twice.** The sender is called before the completion is committed, so a crash or a failed
   commit in between sends again on redelivery (at-least-once; the key `email:payment:{orderId}` is passed for a
   provider that deduplicates — the simulator does not). The invoice has the same window but a deterministic result.
   Accepted as it is?
4. **`order.changed` hint** carries only `orderId`; the task hints of a payment already carry `orderId`, so the open
   order detail refetches today without the 024b mapping.

## Out of scope / follow-ups
- 024b (as planned): payment on the order detail, invoice display, labels of the task types / error codes
  (`invoice_provider_*`, `email_*`, `invoice_order_not_paid`, `email_order_not_paid`, `email_no_recipient`) and of the
  event types `order.paid`, `invoice.message_rejected`, `email.message_rejected`, the `order.changed` mapping, the
  `unexpected` label of review 023.
- An unknown `paymentMethod` text is refused by model binding (400 `validation_failed`); its error key was not pinned
  by a test.
- The redirected console log of a host started by a script shows Vietnamese subjects with replaced characters (code
  page of the redirect, not of the application).

## Development steps (applied)
1. Storage key helper and `docker compose up -d`: not needed (neither the storage keys nor a service definition changed).
2. **Throwaway environment first** (scratch script; random credentials generated in-process, never printed):
   `docker create` postgres:17 on 55432 with the repository's `01-create-roles.sh`, rabbitmq:4-management on 55672 →
   `dotnet ef database update --no-build` as `omnichannel_migrator` (… `ChannelCustomers`, `PaymentAndInvoices`,
   `Done.`) → API (5081) and Workers host from `bin\Debug` in environment `Smoke` as `omnichannel_app` →
   `task-024.ps1` with `SMOKE_BASE_URL` / `SMOKE_SKIP_FRONTEND=1`: **51/51** → hosts stopped, both containers removed.
3. **Development**, as the migrator (connection from the user-level variable, never printed):
   - `dotnet ef migrations list --no-build` → 14 migrations, 1 pending: `20261009133050_PaymentAndInvoices (Pending)` —
     exactly this task's migration;
   - `dotnet ef database update --no-build` → `Applying migration '20261009133050_PaymentAndInvoices'.` `Done.`;
   - `dotnet ef migrations list --no-build` → pending 0.
4. Hosts: none was running before; the smoke script built, started and stopped its own API and Workers host; checked
   afterwards: 0 hosts running.
5. `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024.ps1` on development: **PASSED: all 52 checks**.

```
OK    Step                                                             Expected                           Actual
True  01 create smoke manager                                          201                                201
True  02 create smoke staff                                            201                                201
True  03 create smoke product                                          201                                201
True  04 import 10 units                                               200                                200
True  05 create three smoke customers (A with an address, B without,   201 201 201                        201 201 201
      C at sim-reject.test)
True  06 one order each, processed by the Order Worker                 True                               True
True  07 ... all three Reserved                                        Reserved Reserved Reserved         Reserved Reserved Reserved
True  08 ... and unpaid, without an invoice                            Unpaid True                        Unpaid True
True  09 stock: 10 total, 6 reserved, 4 available                      10 6 4                             10 6 4
True  10 staff pays order A -> 403                                     403                                403
True  11 the manager pays order A (Cash) -> 200                        200                                200
True  12 ... the answer names an invoice task and an e-mail task       True                               True
True  13 the invoice task succeeds within 90 s (Invoice Worker)        Succeeded Invoice InvoiceWorker    Succeeded Invoice InvoiceWorker
True  14 the e-mail task succeeds (Notification Worker)                Succeeded Email NotificationWorker Succeeded Email NotificationWorker
True  15 order A: payment Paid, by Cash, recorded by the smoke manager Paid Cash smoke-024-mgr-35692@...  Paid Cash smoke-024-mgr-35692@...
True  16 ... with the reference that was entered                       SMOKE-REF-35692                    SMOKE-REF-35692
True  17 ... an invoice whose number starts with SIM-                  True                               True
True  18 ... and the order is still Reserved                           Reserved                           Reserved
True  19 the stock is what it was                                      10 6 4                             10 6 4
True  20 the order list shows it as Paid and Reserved                  Paid Reserved                      Paid Reserved
True  21 paying order A again -> 409                                   409                                409
True  22 ... with                                                      order_already_paid                 order_already_paid
True  23 ... and the first payment stays (Cash)                        Cash                               Cash
True  24 the manager pays order B (customer without an address) -> 200 200                                200
True  25 ... no e-mail task                                            (null)                             (null)
True  26 ... its invoice task succeeds                                 Succeeded                          Succeeded
True  27 ... the order has one task of the payment: the invoice        Invoice                            Invoice
True  28 the manager pays order C (address at sim-reject.test) -> 200  200                                200
True  29 its e-mail task ends Failed                                   Failed                             Failed
True  30 ... after one attempt with email_rejected (no retry)          1 email_rejected                   1 email_rejected
True  31 ... with an open ProcessingFailed alert                       Open                               Open
True  32 the invoice task of the same order succeeds                   Succeeded                          Succeeded
True  33 order C: Reserved, Paid, with its invoice - one failed        Reserved Paid True                 Reserved Paid True
      e-mail fails nothing else
True  34 an order of 100 units is stored as InsufficientStock          InsufficientStock                  InsufficientStock
True  35 paying it -> 409                                              409                                409
True  36 ... with                                                      order_not_payable                  order_not_payable
True  37 ... and it stays unpaid                                       Unpaid                             Unpaid
True  38 after all payments the stock is still what it was             10 6 4                             10 6 4
True  39 order.paid in the system events: one for each of A, B, C      3                                  3
True  40 ... with the payment methods                                  Cash BankTransfer EWallet          Cash BankTransfer EWallet
True  41 ... each by the smoke manager                                 True                               True
True  42 ... and the reference of the payment is not in the event      False                              False
True  43 staff cannot read the events                                  403                                403
True  44 the address of customer C is corrected                        200                                200
True  45 the manager retries the failed e-mail task -> 202             202                                202
True  46 ... it succeeds with its second attempt                       Succeeded 2                        Succeeded 2
True  47 ... and its alert is resolved                                 Resolved                           Resolved
True  48 an import of 1 unit (resolves the insufficient-stock alert    200                                200
      of the rejected order)
True  49 lock smoke account 1                                          200                                200
True  50 lock smoke account 2                                          200                                200
True  52 npm run build                                                 0                                  0
True  53 npm test                                                      0                                  0

PASSED: all 52 checks.
```
(Two cells shortened here for the line width: step 15 shows the full smoke address in the run, step 25 an empty cell
for null. Step 51 is `npm ci`, recorded only when packages are missing.)

Left on development by the run: three paid smoke orders with their invoices, one `InsufficientStock` order, 11 units
of the smoke product, two locked smoke accounts. The script asserts that the alert of the failed e-mail task is
`Resolved`; that the import of step 48 resolved the insufficient-stock alert follows from the rule of task 017 and was
not asserted.

## Manual steps for the user
None.

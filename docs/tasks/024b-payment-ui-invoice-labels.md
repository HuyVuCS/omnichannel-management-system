# Task 024b — Frontend: payment on the order detail, invoice and confirmation status, payment in the orders list, `order.changed`, labels; Part A (review 024, review 023)

Status: Open
Written by: Cowork, 2026-10-09
Depends on: 024 (accepted)

## Context
Doc §5.3 (the order tree "Payment = Succeeded / InvoiceTask = Failed / EmailTask = Succeeded" — this task puts it on
the order detail), §8.4 (payment → independent invoice and e-mail tasks), §3.2 (pages "Đơn hàng", "Logs/Audit"), §3.4
(a failed task links to its detail and retry), §12 (realtime → refetch); CLAUDE.md. Task 024 delivered the API
(README §10 "Payment, invoices and e-mails"): `POST /api/orders/{orderId}/payment` (Manager/Admin; body
`{ paymentMethod, paymentReference? }`; 200 `{ orderId, paymentStatus, paidAt, invoiceTaskId, emailTaskId | null }`;
409 `order_not_payable` / `order_already_paid`), `payment { status, paidAt, paidByEmail, method, reference }` and
`invoice { number, issuedAt } | null` on `GET /api/orders/{id}`, `paymentStatus` on `GET /api/orders`, the hint
`order.changed { orderId }`, the event types `order.paid`, `invoice.message_rejected`, `email.message_rejected`.

Every decision of this task is in the implementation log, "Task 024b plan" and "Task 024 result" — read them first.

Read first: log "Task 024b plan", "Task 024 plan/result", "Task 021 result" (labels, client copies of server limits),
"Task 018 result" (form-dialog reset from inside the form, wide tables at 1280 px), "Task 018b result" (Logs labels,
request guard), "Task 016 plan/result" (tag map, throttle, resync), "Task 022 result" (slow-run discipline); reviews
024 and 023; README §10, "Frontend", "System events"; frontend `features/orders/*` (`ordersApi.ts`,
`OrderDetailPage.tsx`, `OrdersPage.tsx`, `components/OrderStatusTag.tsx`, tests), `features/processing/*`
(`RetryTaskButton.tsx` — role hook, confirmation, error handling; `processingApi.ts` — `retryProcessingTask`
invalidation; `TaskStatusTag`, `paths.ts`), `features/realtime/hintTags.ts` (+ tests), `features/logs/*`
(`systemEventsApi.ts` `EVENT_TYPES`, `eventSummary.ts`, `LogsPage.tsx`), `features/users/components/ResetPasswordModal.tsx`
and `shared/components/ResetFormOnOpen.tsx` (dialog pattern), `shared/api/problemDetails.ts` (`problemMessages`,
`applyProblemToForm`), `shared/components/MoneyInput.tsx` / `shared/utils/format.ts`, `shared/i18n/*.json`, `test/*`;
backend `SimulatedInvoiceProviderClient.InvoiceNumberOf`, `PaymentProviderTests.cs`, `OrderPaymentEndpointsTests.cs`;
`scripts/smoke/task-024.ps1`, `_common.ps1`.

**Stop and report (`Blocked`)** if: the page needs an API change not listed here (a new field, a payment filter on the
list, an endpoint to cancel or edit a payment); A1's model-binding key is unusable for a field error and fixing it would
change the validation pipeline of other endpoints; or a decision is needed that is not in this file or the log.

## Part A — Follow-ups (separate commits, first: A1 backend, A2 frontend)

### A1. Longer simulated invoice numbers; pinned key of an unknown payment method (P3, review 024 question 2 and finding 2)
- `SimulatedInvoiceProviderClient.InvoiceNumberOf`: `SIM-{yyyyMM}-{first 16 hex of SHA-256(key)}` (24 characters;
  `InvoiceNumber` is varchar(64) — **no migration**). Still deterministic per key; stored invoices are not touched.
  Update the doc comment, README §10 (number format; remove the collision note) and the tests that pin the format;
  one test that the number has 16 hex digits after the month. Mutation: back to 8 digits → killed.
- `OrderPaymentEndpointsTests`: `paymentMethod: "Cheque"` (unknown text) → 400 `validation_failed`; pin the error key
  and its localised message (en/vi). Expected key `paymentMethod`. If the binder produces another key (e.g.
  `$.paymentMethod`), pin what it is and report it under "Deviations" — no pipeline change (the dialog sends known
  values only).
- Smoke 024 step 17 (`SIM-` prefix) stays valid; 024b's smoke checks the full format (Part E).

### A2. `unexpected` on the Logs page (P3, review 023 question 3)
`logs.channelErrors.unexpected` (vi/en): "unexpected error (see the Workers log)" and its code in
`CHANNEL_ERROR_CODES`; a `reconciliation.completed` event with an unreadable channel of reason `unexpected` shows the
label (test).

## Part B — API client and labels

### B1. `ordersApi.ts`
- `PAYMENT_STATUSES = ['Unpaid', 'Paid']`, `PAYMENT_METHODS = ['Cash', 'BankTransfer', 'Card', 'EWallet']` (the API's
  enums as strings, as `ORDER_STATUSES`); `OrderSummary.paymentStatus`; `OrderDetail.payment`
  (`{ status, paidAt, paidByEmail, method, reference }`, nullable as the API) and `OrderDetail.invoice`
  (`{ number, issuedAt } | null`).
- `payOrder` mutation: `POST orders/{orderId}/payment`, body `{ paymentMethod, paymentReference? }` (reference trimmed,
  omitted when blank). Invalidation as `retryProcessingTask`: on any API answer except an unexpected error (network,
  5xx) → `{ Order, orderId }`, `ORDER_LIST_TAG`, `PROCESSING_LIST_TAG`, `DashboardSummary` (the processing card counts
  the new tasks); a 409 means the screen is stale, so it is refetched too.
- `PAYMENT_ROLES = ['Admin', 'Manager']` in `features/orders/paths.ts` (as `RETRY_ROLES`).

### B2. Labels (vi/en, no hard-coded text)
- `orders.paymentStatuses.{Unpaid,Paid}`, `orders.paymentMethods.{Cash,BankTransfer,Card,EWallet}`; a
  `PaymentStatusTag` next to `OrderStatusTag` (one colour per value: `Unpaid` default, `Paid` success; unknown value
  shown raw).
- `errors.order_not_payable`, `errors.order_already_paid` (the codes of 024; `translateApiError` picks them up).
- Logs/Audit: `EVENT_TYPES` gains `order.paid`, `invoice.message_rejected`, `email.message_rejected`; event-type labels;
  summaries — `order.paid`: "{{method}} · {{amount}}" (method label, `formatMoney`; the payer is the existing actor
  column via `actorEmail`); the two rejected types as `report_export.message_rejected` (`reasonCode` label; no new
  rejection code exists — review 024, deviation 9).
- **Attempt error codes stay raw** (`invoice_provider_*`, `email_*`, `invoice_order_not_paid`, `email_order_not_paid`,
  `email_no_recipient`) — log "Task 024b plan". No label map for them.

## Part C — Order detail: payment and follow-up (every role reads; Manager/Admin pay)

### C1. "Payment" card (between the summary and the lines)
The doc §5.3 tree for this order, from the detail response only (nothing computed but the choice of task):
- **Payment:** `PaymentStatusTag`; when `Paid`: method label, reference (`TruncatedText`, "—" when none), paid at
  (`formatDateTime`), recorded by (`paidByEmail`, "—" when null).
- **Invoice** (only when `Paid`): the number (`Typography.Text code copyable`) and issued at when `invoice` is set;
  otherwise the status of the order's `Invoice` task (`TaskStatusTag`) with a link to its task detail
  (`processingTaskPath`) — retry stays where it is (task blocks below / task page).
- **Confirmation e-mail** (only when `Paid`): the status of the order's `Email` task (`TaskStatusTag` + link); when the
  order is paid and has **no** `Email` task: "Not sent — the customer had no e-mail address when the order was paid".
- Task choice: the order's task of that type (one per order by the keys of 024); if several exist, the newest by
  `createdAt`.
- While `Unpaid`: for Manager/Admin and `status = Reserved` the **"Mark as paid"** button; for Manager/Admin and any
  other status a secondary text "Only orders with reserved stock can be paid"; Staff sees the status only.
- Fits the detail page at 1280 px (log "Task 018 result"); `Descriptions` as the summary.

### C2. "Mark as paid" dialog (Manager/Admin)
- Modal with a form, reset on open from inside the form (`ResetFormOnOpen`, as `ResetPasswordModal`):
  read-only line with the order number and **total** (`formatMoney`) so the amount is confirmed; **payment method**
  (`Select`, required, **no default** — a financial record must be chosen, not accepted); **reference** (optional,
  `maxLength` 100 — a client copy of `Order.MaxPaymentReferenceLength`, commented like `MAX_IMAGE_BYTES`, the server
  stays the authority; trimmed; help text "receipt or transfer number"). OK button "Mark as paid", disabled and loading
  while the request runs (no second POST; the server's 409 is the guard anyway).
- 200 → dialog closed; success message: "Payment recorded. Invoice requested; confirmation e-mail requested." or,
  with `emailTaskId: null`, "Payment recorded. Invoice requested; no confirmation e-mail (the customer has no e-mail
  address)." The page shows the new state through the invalidation (B1) — and the task states change live by the
  existing task hints.
- 400 → field errors on `paymentMethod` / `paymentReference` (`applyProblemToForm`), others form-level; 409
  `order_already_paid` / `order_not_payable` → form-level message in the dialog (localised by code), the page data
  refetched (B1); 403 / 404 / network / 5xx → as everywhere (`problemMessages`, global notifier).
- No confirmation step on top of the dialog: the dialog itself is the confirmation (total shown, method chosen).

## Part D — Orders list and realtime

### D1. Orders list
- A "Payment" column right after "Status" (`PaymentStatusTag`, width ≤ 130). The columns users scan (created, order
  number, status, payment, total) still fit 992 px; update `COLUMNS_WIDTH` and its comment. No filter (the API has
  none — out of scope).

### D2. `order.changed`
- `tagsForEvent`: `order.changed` with a string `orderId` → `{ Order, orderId }` and `{ Order, LIST }`; with `all` or
  without an id → `Order`. Not `DashboardSummary` (the summary has no payment figure). Throttled with everything
  else; the resync set needs no change (`Order` is in it already).
- Remove the outdated sentence "realtime updates come with SignalR in a later task" from the `OrdersPage` doc comment
  only if it is touched anyway by D1.

## Part E — Tests (required)
Frontend (MSW, fake hub, every test ends `settled()`; request tests also pass under `npm run test:slow`):
- **Payment card:** unpaid `Reserved` order — Manager and Admin see the button, Staff does not; unpaid
  `InsufficientStock` — no button, the hint for Manager, nothing for Staff; paid with invoice and e-mail `Succeeded`
  (method label, reference, paid at, recorded by, number copyable, issued at); paid with the invoice task
  `InProgress` (status + task link, no number); paid without an `Email` task (the "no address" text); paid with the
  e-mail task `Failed` (status + link); a reference of 100 characters truncated with its tooltip; vi and en.
- **Dialog:** method required (no request without it); a reference of 101 characters blocked on the client; the body
  sent (method; reference trimmed; blank reference omitted); success → both message variants (`emailTaskId` set /
  null), dialog closed, detail and list refetched once each; 409 `order_already_paid` and `order_not_payable` →
  localised form-level messages (en/vi) and the detail refetched; 400 with `paymentMethod` / `paymentReference` keys →
  on the fields (en/vi); the OK button disabled while in flight (one POST for a double click); reopened dialog is empty.
- **List:** the payment column for `Paid` / `Unpaid` and an unknown value (raw).
- **Realtime:** `hintTags` unit tests for `order.changed` (id, `all`, no id); an `order.changed` hint for the open
  order refetches the detail once and the open list once (throttle); a hint for another order does not refetch the
  detail.
- **Logs:** labels and summaries of the three event types; `unexpected` (A2).
Backend: A1 tests as listed; all existing tests green.

## Part F — Smoke script `scripts/smoke/task-024b.ps1`
Strict `Assert-Step`; API + Workers (`Start-SmokeWorkers`); smoke Manager; product, stock, a customer with
`…@example.com`; one order → `Reserved`; Manager pays it → 200; invoice task `Succeeded` (bounded wait); the detail's
invoice number matches `^SIM-\d{6}-[0-9A-F]{16}$` (A1); `GET /api/orders` shows `paymentStatus` `Paid`; a payment with
`paymentMethod: "Cheque"` on another `Reserved` order → 400 with the key pinned by A1; lock the smoke user;
`Invoke-FrontendChecks`.

## Acceptance criteria
- [ ] Part A first, in separate commits (A1 backend, A2 frontend), then Parts B–D.
- [ ] Backend build 0 warnings, all tests pass; A1 mutation killed (in the report).
- [ ] Frontend typecheck, lint, test, build pass; **5 consecutive full `npm test` runs green** (wall times) and one
      full `npm run test:slow` green (log "Task 022 result": slow runs on an idle machine; a red test is not attributed
      to load without a second run of that file alone).
- [ ] Bundle: first-load set before/after (`npm run measure`); no new dependency.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity.
- [ ] `scripts/smoke/task-024b.ps1` on development (no migration in this task); table in the report.
- [ ] Real-browser check on the production build (`npm run build` + `vite preview`), 1280 px, vi and en, on
      development: Manager opens a `Reserved` unpaid order, marks it as paid (method + reference), sees the payment
      recorded and the invoice and e-mail statuses turn `Succeeded` **without pressing Refresh**, the invoice number
      appears; the orders list shows "Paid"; an order of a customer without an address shows the "no e-mail" text;
      Staff sees the payment card without the button; the Logs page shows `order.paid` with method, amount and payer.
- [ ] README: §10 gets a short "On the order detail" part (who pays, what the card shows, live updates) and the new
      invoice-number format; "Frontend" notes only if a new pattern was needed.
- [ ] "Development steps (applied)" in the report; "Manual steps for the user": none, or only a step that could not be
      applied, with the reason.

## Out of scope
A payment filter or sort on the orders list; cancelling, editing or refunding a payment; partial payments; paying from
the list; a "resend e-mail" action beyond the existing retry of a `Failed` task; labels for attempt error codes;
invoice documents (PDF) and downloads; a paid-orders figure on the dashboard; payment columns in the Excel export;
any change to the payment API beyond A1's test.

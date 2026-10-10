# Report 024b — Frontend: payment on the order detail, invoice and confirmation status, payment in the orders list, `order.changed`, labels; Part A (review 024, review 023)

Status: Done
Commit: `ea8cebf` (Part A1, backend), `b77f006` (Part A2, frontend), `43e0478` (Parts B–F); this report in its own commit

## Summary
The order detail has a **Payment** card (every role): payment status, and once paid the method, reference, time and
who recorded it, the invoice (number with copy button and issue time, or the status of the `Invoice` task with a link)
and the confirmation e-mail (status of the `Email` task with a link, or "not sent — no address"). Manager and Admin get
**"Mark as paid"** while the order is `Reserved` and unpaid: a dialog with the order number and total, a required
method without default and an optional reference. The orders list has a "Payment" column; `order.changed` refreshes
the order and the order lists; the Logs page names `order.paid` and the two rejected-message types. Part A: simulated
invoice numbers carry 16 hex digits; the 400 of an unknown payment method text is pinned; `unexpected` has a label.
No migration, no new dependency, no API change.

Nothing was blocking. Two findings need a decision of Cowork (questions 1 and 2).

## Files changed
**Part A1 (`ea8cebf`)**
- `backend/src/Omnichannel.Infrastructure/ExternalProviders/SimulatedInvoiceProviderClient.cs` — `hash[..16]`, doc comment.
- `backend/src/Omnichannel.Tests/Workers/PaymentProviderTests.cs` — format `^SIM-202610-[0-9A-F]{16}$`, length 27, new
  test "sixteen hex digits of the hash after the month".
- `backend/src/Omnichannel.Tests/Integration/Workers/PaymentWorkersTests.cs` — format with 16 digits.
- `backend/src/Omnichannel.Tests/Integration/OrderPaymentEndpointsTests.cs` — theory (en / vi) for `paymentMethod: "Cheque"`.
- `README.md` §10 — number format, collision note removed.

**Part A2 (`b77f006`)**
- `frontend/src/features/logs/eventSummary.ts` — `unexpected` in `CHANNEL_ERROR_CODES`.
- `frontend/src/shared/i18n/vi.json`, `en.json` — `logs.channelErrors.unexpected`.
- `frontend/src/features/logs/logs.test.tsx` — the label in both languages.

**Parts B–F (`43e0478`)**
- `frontend/src/features/orders/ordersApi.ts` — `PAYMENT_STATUSES`, `PAYMENT_METHODS`, `paymentStatus`, `payment`,
  `invoice`, `payOrder` (reference trimmed, omitted when blank; invalidation as the manual retry).
- `frontend/src/features/orders/paths.ts` — `PAYMENT_ROLES`.
- `frontend/src/features/orders/components/PaymentStatusTag.tsx` (new), `OrderPaymentCard.tsx` (new),
  `PayOrderModal.tsx` (new, `MAX_PAYMENT_REFERENCE_LENGTH`).
- `frontend/src/features/orders/OrderDetailPage.tsx` — the card between the summary and the lines; doc comment.
- `frontend/src/features/orders/OrdersPage.tsx` — "Payment" column (130 px) after the status, `COLUMNS_WIDTH` 1270 and
  its comment, the outdated SignalR sentence replaced.
- `frontend/src/features/realtime/hintTags.ts`, `hub.ts` — `order.changed`.
- `frontend/src/features/logs/systemEventsApi.ts`, `eventSummary.ts` — three event types, their summaries.
- `frontend/src/shared/i18n/vi.json`, `en.json` — 37 keys each (729 leaf keys in all, identical sets).
- Tests: `features/orders/orderPayment.test.tsx` (new, 28), `orders.test.tsx` (+1, header list, one existing test now
  ends settled — deviation 6), `realtime/hintTags.test.ts` (+1), `logs/logs.test.tsx` (+1, filter option lists),
  `test/orders.ts` (fixtures).
- `scripts/smoke/task-024b.ps1` (new); `README.md` (§10 "On the order detail", the 400 of an unknown method, smoke
  script, the hint → tag list in "Frontend").

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` / `dotnet build --no-incremental` (backend, state of `ea8cebf`) | 0 errors, **0 warnings** |
| `dotnet test` (backend, state of `ea8cebf`) | **1759 passed**, 0 failed (3 m 42 s) — 3 more than 024 |
| A1 mutation: `hash[..16]` back to `hash[..8]`, `dotnet test --filter FullyQualifiedName~PaymentProviderTests` | **killed**: 2 failed (`…carries_sixteen_hex_digits…`, `…month_of_the_payment_and_the_start_of_the_hash…`), 39 passed; restored, rebuilt `--no-incremental` |
| `npm run typecheck` | clean |
| `npm run lint` (eslint + prettier) | clean |
| `npm run build` | built |
| `npm run measure` | first-load set **969.55 → 969.84 kB raw** (+0.29), 317.94 → 318.00 kB gzip; main chunk 170.68 → 170.96 kB (the payment enums and `payOrder` in the shared orders API); pages stay lazy: `OrdersPage` 5.71 kB, `OrderDetailPage` 9.25 kB |
| `npm test`, full runs | **486 passed (31 files)** — 31 more than before; series below |
| `npm run test:slow`, full | **486 passed**, 282 s, first full attempt |
| `scripts\smoke\task-024b.ps1` on development | **24 of 24** (table below) |

**Full `npm test` runs** (4 workers, nothing of mine next to them; the user's own applications were running):

| Run | Result | Wall time |
| --- | --- | --- |
| first series 1, 2 | green | 272 s, 475 s |
| first series 3 | **1 failed** — my new test "removes the order list from the cache…": `findByRole('link', …)` on the list page timed out. Changed to `findByText` (the log's rule for role queries over large tables), file alone green; series restarted | 365 s |
| second series 1, 2, 3 | green | 260 s, 246 s, 261 s |
| second series 4 | **6 failed in 4 files** (`stockImport`, `users`, `products`, `orders`: four 20 s timeouts, two first-render waits), none in a file or test this task wrote; the run took 670 s instead of ~260 s; 611 MB of 7.5 GB memory free right after it | 670 s |
| each of the four files alone | green: 20 / 14 / 17 / 17 passed | 32, 25, 28, 27 s |
| **5, 6, 7, 8, 9** | **five consecutive green**, 486 passed each | 278, 311, 347, 280, 331 s |

Run 4 is attributed to load on the evidence above (2.6 × the duration, unrelated files, all four green alone, five
green runs after it) — I did not find its cause on the machine beyond the low free memory.

**Smoke `task-024b.ps1` on development** (hosts started and stopped by the script; frontend steps after stopping them):
```
OK    Step                                                              Expected                           Actual
True  01 create smoke manager                                           201                                201
True  02 create smoke product                                           201                                201
True  03 import 10 units                                                200                                200
True  04 create smoke customer with an e-mail address                   201                                201
True  05 an order, processed by the Order Worker (...)                  True                               True
True  06 ... Reserved, Unpaid, without an invoice                       Reserved Unpaid True               Reserved Unpaid True
True  07 the order list shows it as Unpaid                              Unpaid                             Unpaid
True  08 the manager pays the order (BankTransfer, with a reference)    200                                200
True  09 ... the answer names an invoice task and an e-mail task        True                               True
True  10 the invoice task succeeds within 90 s (Invoice Worker)         Succeeded                          Succeeded
True  11 the e-mail task succeeds (Notification Worker)                 Succeeded                          Succeeded
True  12 the detail: Paid by BankTransfer, recorded by ..., reference   Paid BankTransfer smoke-024b-mgr-44732@example.com SMOKE-024B-REF-44732 (both)
True  13 ... the invoice number is SIM-{yyyyMM}-{16 hex digits}         True                               True
True  14 ... the order has an Invoice task and an Email task            Email Invoice                      Email Invoice
True  15 ... and is still Reserved                                      Reserved                           Reserved
True  16 the order list shows it as Paid                                Paid                               Paid
True  17 a second Reserved order                                        True                               True
True  18 paying it with paymentMethod "Cheque" -> 400                   400                                400
True  19 ... validation_failed                                          validation_failed                  validation_failed
True  20 ... with the error keys $.paymentMethod and request            $.paymentMethod request            $.paymentMethod request
True  21 ... and the order stays Unpaid                                 Unpaid                             Unpaid
True  22 lock smoke account                                             200                                200
True  24 npm run build                                                  0                                  0
True  25 npm test                                                       0                                  0
PASSED: all 24 checks.
```
(Step 23 is the helper's reserved `npm ci` step; it did not run.)

**Real-browser check** — production build (`npm run build` + `vite preview`, port 4173), 1280 × 800, development
database, API and Workers host of this repository, throwaway smoke users (logged in through the API, no password
typed). The Browser pane was not displayed, so values were read from the DOM; two screenshots were taken (dialog, paid
card).
- Manager, `Reserved` unpaid order, vi: card "Thanh toán" with "Chưa thanh toán" and the button (176 px); dialog shows
  the order number and `30.000 ₫`, no method chosen. Chose "Chuyển khoản", typed a reference with spaces around it,
  submitted.
- **Without Refresh**, measured by a `MutationObserver` on the card: POST at 07:41:57.156; card "Đã thanh toán" with
  method, time, payer, trimmed reference and both tasks "Đang chờ" at .605; invoice number
  `SIM-202610-6598B6E00F31D23F`, issue time and e-mail "Thành công" at 07:41:58.853. Two reads of the order after the
  POST (the mutation's invalidation, then one for the task hints one second later — the throttle). Toast: "Đã ghi nhận
  thanh toán. Đã yêu cầu xuất hoá đơn; đã yêu cầu gửi e-mail xác nhận."
- Orders list (`?search=SMOKE-024B-UI`): column "Thanh toán" after "Trạng thái"; the paid order "Đã thanh toán", the
  others "Chưa thanh toán"; the columns up to "Tổng tiền" end at x = 1074 of 1256.
- en, order of the customer **without an address**: submit without a method → field error, no request; with "Cash" →
  toast "Payment recorded. Invoice requested; no confirmation e-mail (the customer has no e-mail address)."; card
  "Confirmation e-mail: Not sent — the customer had no e-mail address when the order was paid", invoice issued.
- Logs (`?eventType=order.paid`), en and vi: "Order paid · Cash · 30.000 ₫ · smoke-024b-ui-mgr-…@example.com" /
  "Thanh toán đơn hàng · Chuyển khoản · 30.000 ₫ · …".
- **Staff** on a third unpaid order: the card without a button. Then the order was paid **from another session** (a
  request with the Manager's token, outside the application's store): the Staff page showed "Đã thanh toán" 0.43 s
  later (`order.changed`) and the invoice number and e-mail "Thành công" after another second.
- Layout at 1280 px: no horizontal scroll of the page; every row of the card one line high after the change described
  in deviation 3.
- Not checked in the browser (tests only): the "only orders with reserved stock can be paid" hint, the 409 and 400
  messages in the dialog, the reopened dialog.
- Clean-up: both smoke users locked, hosts and preview stopped, the temporary `.claude/launch.json` and the session
  file removed.

No mutation run on the frontend code (the task asks for A1's only).

## Deviations from the task
1. **The invoice number has 27 characters, not 24** (`SIM-` 4 + month 6 + `-` 1 + 16). The task's arithmetic was off;
   the format is as specified and fits `varchar(64)`. Test and README say 27.
2. **A1 key: `$.paymentMethod` and `request`, not `paymentMethod`** — the outcome the task foresaw. The JSON binder
   refuses the text before validation; both messages are the framework's English texts in every language. Pinned by
   the test (keys, `Path: $.paymentMethod`, "The request field is required."), README updated, no pipeline change. In
   the dialog such an answer is shown above the form (tested); the dialog sends the four known values only. See
   question 1.
3. **Invoice row.** Number and issue time are on **one** row ("Hoá đơn: `SIM-…` · Xuất lúc …") instead of two
   labelled fields: in the browser at 1280 px the number with its copy button did not fit one of two value columns
   (255 px) and wrapped. The payer's address is truncated with a tooltip at 220 px for the same reason.
4. **The dialog does not use `useFormSubmission`**: that hook has one fixed success text, the dialog needs two
   (`emailTaskId` set / null). It calls `applyProblemToForm` directly; the rest of the dialog pattern is the usual one.
5. **"Detail and list refetched once each"**: with the detail open no list is on screen, so RTK Query drops the cached
   list instead of fetching it. The test opens the detail from the list, pays, checks that the list entry is gone and
   that "Back" reads the list once more with "Đã thanh toán". The detail is read exactly once after a payment.
6. **One existing test changed**: `orders.test.tsx` "customer picker › makes the full text of a long name…" ended with
   the filtered list request in flight and failed **every** slow run of that file today — also on the commit before
   this task (checked with `git stash`). It now ends with `settled()`. Test-only.
7. The reference input counts typed characters (`maxLength` 100), spaces around the text included; the server limits
   the trimmed text.

## Blocked / questions for Cowork
1. **The binder's message of an unknown enum text names a CLR type**: "The JSON value could not be converted to
   Omnichannel.Application.Orders.PayOrderRequest. Path: $.paymentMethod | …". This is the behaviour of every endpoint
   for a malformed JSON value (log "Task 011 result": `$…` paths unchanged), not something 024 introduced, and no
   client of ours triggers it. Impact: an internal type name and an untranslated text reach an API caller. Fix if
   wanted: replace binder messages by one localised "the value is not valid" per `$…` path in
   `ValidationProblemResponse` — a change of the validation pipeline for all endpoints, so not done here. Candidate
   for 026 (hardening)?
2. **"Please enter Payment method" / "Vui lòng nhập …"** is AntD's default `required` text and reads oddly for a
   select. Every required `Select` of the application has it. A `validateMessages` override or a per-field message?
   Not changed.
3. Deviation 3 (one invoice row) — accept, or should the card use another layout for two labelled fields?

## Out of scope / follow-ups
- `orderPayment.test.tsx` is the slowest file of the suite under load (28 tests, ~38 s alone, ~100 s in a full run);
  splitting it (card / dialog / realtime) is the usual remedy if the suite's wall time matters.
- Data left on development by the browser check: three paid orders `SMOKE-024B-UI-*-46146` with their invoices, their
  product and two customers, two locked users `smoke-024b-ui-*`; by the smoke script: one paid and one unpaid order
  `SMOKE-024B-*-44732`, one locked user. No open alert was created.

## Development steps (applied)
- No migration, no changed container, no storage key in this task.
- Hosts: none was running before the builds; the smoke script and the browser-check preparation started the API and
  the Workers host from this repository and both were stopped afterwards (checked: no `Omnichannel.Api.dll` /
  `Omnichannel.Workers.dll` process left).
- `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024b.ps1` on development → 24 of 24 (table above).

## Manual steps for the user
None.

# Review 024b — Frontend: payment on the order detail, invoice and confirmation status, payment in the orders list, `order.changed`, labels; Part A (review 024, review 023)

Reviewer: Cowork, 2026-10-10
Verdict: **Accepted** — no P0–P2 finding. All seven deviations accepted (deviation 1 corrects an arithmetic error of
the task). Questions 1 and 2 are pre-existing, application-wide behaviours: recorded for **026**, nothing to do now.

## Checked
- Report and commits `ea8cebf` (A1, backend), `b77f006` (A2), `43e0478` (Parts B–F), `a94b322` (report). No migration,
  no new dependency, no API change.
- Read in full: `PayOrderModal.tsx`, `OrderPaymentCard.tsx`, `PaymentStatusTag.tsx`; the diffs of `ordersApi.ts`,
  `hintTags.ts`, `hub.ts`, `eventSummary.ts`, `OrdersPage.tsx`, `SimulatedInvoiceProviderClient.cs`, the A1 test; the
  credential handling of `task-024b.ps1` (random password, cleared after use, nothing printed).
- Checked by Cowork against the code:
  - **The dialog is the confirmation, and it is safe against double submission:** the order number and total are
    read-only, the method has no default and is required, `confirmLoading` + disabled OK button while in flight, and
    `onFinish` ignores a submit while loading; the server's 409 remains the real guard. Reset on reopen by
    `ResetFormOnOpen` + `clearOnDestroy` + `destroyOnHidden` (log "Task 018 result" pattern); form-level errors cleared
    on cancel.
  - **Errors:** `applyProblemToForm` puts `paymentMethod` / `paymentReference` errors on the fields and everything else
    (409 codes, binder keys of A1) above the form; on a 409 the dialog stays open with the localised reason while the
    page behind it is refetched — correct, the user sees why and closes it.
  - **Invalidation** as manual retry: every API answer except network / 5xx refetches `{ Order, id }`, the order lists,
    the task lists and the summary. The reference is trimmed and omitted when blank in `query`, so the server and the
    client agree on "no reference".
  - **The card shows only the API's facts** (doc §5.3 tree): payment fields; invoice number or the `Invoice` task's
    status with a link; `Email` task status with a link, or "no address at payment time" when paid without an `Email`
    task — which is exactly the 024 rule (no task without an address). The button needs role, `Unpaid` and `Reserved`;
    the hint for Manager/Admin otherwise; Staff sees no modal at all (not rendered without the role).
  - **`order.changed`:** id → `{ Order, id }` + `{ Order, LIST }`; `all` / no id → `Order`; no `DashboardSummary`. The
    browser check shows it end to end: a payment made in another session reached a Staff page in 0.43 s.
  - **Orders list:** payment column after status; the scanned columns are 810 px (< 976 px next to a scrollbar).
  - **A1:** `hash[..16]`, test of the format and the digit count, mutation back to 8 killed; README note removed. A2:
    `unexpected` labelled.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1759/1759; A1 mutant killed; frontend typecheck,
  lint, build clean; 486 tests; five consecutive green full runs (5–9) and one full slow run green at the first attempt;
  first-load set +0.29 kB raw (969.84 kB), both order pages still lazy; smoke 024b 24/24 on development; real-browser
  check on the production build at 1280 px in vi and en (payment, live invoice / e-mail status without Refresh, "no
  address" text, list column, Logs line, Staff view, payment from another session).
- **Flaky-run handling followed the rule** (log "Task 022 result"): the one red of this task's own test was fixed
  (`findByText` instead of a role query over the list, as the log prescribes); run 4 of the second series (6 reds in
  four files this task did not touch, 2.6 × the usual wall time, 611 MB free memory) was attributed to load only after
  each of the four files passed alone, followed by five green runs.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | Invoice number is 27 characters, not 24 | Accepted — the task's arithmetic was wrong (`SIM-` + 6 + `-` + 16 = 27); fits `varchar(64)`. |
| 2 | A1 keys are `$.paymentMethod` and `request` | Accepted — the outcome the task foresaw; pinned; question 1. |
| 3 | Invoice number and issue time on one row; payer truncated at 220 px | Accepted — question 3. |
| 4 | Dialog without `useFormSubmission` (two success texts) | Accepted — the hook has one fixed success text; the rest of the pattern is kept. |
| 5 | The list is dropped from the cache, not refetched, while the detail is open | Accepted — correct RTK Query behaviour for an unmounted query; the test proves "Back" shows the new state. |
| 6 | Existing `orders.test.tsx` test ends with `settled()` | Accepted — a pre-existing defect of the test (red in every slow run, also before this task), test-only. |
| 7 | `maxLength` counts untrimmed characters | Accepted — at worst a user trims by hand; the server limits the trimmed text. |

## Questions of the report
1. **Binder messages name a CLR type and stay English** ("could not be converted to
   Omnichannel.Application.Orders.PayOrderRequest. Path: $.paymentMethod", key `request` "The request field is
   required."). **Recorded for 026 (P3, information disclosure + localisation), no change now.** It is the behaviour of
   every endpoint for a malformed JSON value since 001, no client of ours triggers it, and the fix touches the
   validation pipeline of all endpoints. The 026 item: setting `JsonOptions.AllowInputFormatterExceptionMessages =
   false` (ASP.NET Core MVC option; replaces the converter text by a generic one — verify the exact text and that it
   covers both messages) and mapping `$…` binder errors to one localised "this value is not valid" per path in
   `ValidationProblemResponse`, with the companion `request` / body-required entry dropped when a `$…` error exists;
   tests across a few endpoints.
2. **"Please enter …" for a required `Select`.** **Recorded for 026 (UX, with the accessibility pass)**: one
   application-wide `form.validateMessages` in the `ConfigProvider` with a neutral text per language ("{label} is
   required" / "{label} là bắt buộc"), instead of per-field messages; existing tests that assert AntD's default text
   follow. It affects every form, so it does not belong in a payment task.
3. **One invoice row.** **Accepted.** A copyable number next to its issue time is one fact ("the invoice"), and the
   evidence (wrapping at 255 px) is a better argument than the task's layout guess.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | Binder error texts expose a CLR type name and are not localised (question 1). Pre-existing, all endpoints. | **026**. |
| P3 | AntD's default "Please enter" for required selects (question 2). Pre-existing, all forms. | **026**. |
| — | `orderPayment.test.tsx` is the slowest file under load (~100 s in a full run). | No action unless it shows up in reds; split by card / dialog / realtime then. |
| — | `taskOf` picks the newest task by comparing `createdAt` strings. | No action: one task per type per order by the keys of 024; the API's ISO strings are UTC, so the comparison is only imprecise inside one second. |
| — | `eventSummary.ts` (Logs) imports `PAYMENT_METHODS` from `ordersApi`. | Accepted: the same as the Logs page's use of `TASK_TYPES`; `ordersApi` is in the main chunk already. |

## Assessment
The payment UI is small, correct and honest about what it knows: every value on the card is the API's, the only
client-side choice is which task to show, and the dialog makes the financial record deliberate (total shown, no default
method) without adding a second confirmation. The realtime path was verified the way it matters — a payment from a
different session reaching a Staff page — not only in the fake hub. The report's handling of the red runs is exactly
what the log asks for: the own red fixed at its cause, the unrelated reds attributed to load only on evidence. With 024
and 024b the doc's §8.4 / §5.3 flow is complete end to end, from the order intake to the invoice and the confirmation.

## Manual check
None for the user. Optional demo: open a `Reserved` order on the order detail as Manager, "Mark as paid"; README §10
"On the order detail".

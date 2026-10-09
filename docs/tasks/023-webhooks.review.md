# Review 023 — Webhooks `POST /api/webhooks/{slug}`; Part A (review 022)

Reviewer: Cowork, 2026-10-09
Verdict: **Accepted** — no P0–P2 finding. Question 1 shows an error **in the task, not in the work**: the plan asked for
`CreatedBy` columns that `ProcessingTasks` / `OutboxEvents` never had; Claude Code was right not to add them. Two P3
follow-ups go into **024 Part A**, two observations to 026.

## Checked
- Report and commits `6b87779` (A1–A2), `919620a` (A3 + migration `ChannelProductExternalSku`), `f440944` (Parts B–C
  + migration `ChannelCustomers`), `57d0560` (report). Both migrations additive (one `CreateIndex`; one `CreateTable`
  with two indexes), no new package.
- Read in full: `WebhookSignatureFilter.cs`, `WebhookSignature.cs`, `WebhookCaller.cs`, `WebhooksController.cs`,
  `WebhookOrderService.cs`, the webhook part of `RateLimitPolicies.cs`, `WebhookOptions` (`SecretsOf`),
  `WebhookRejection.Log`; the A1–A2 diff of `StockReconciliation.cs`; `OrderCreatedPayload` / `NormalizedOrder`
  (`ReceivedBy`) for question 1.
- Checked by Cowork against the code:
  - **Nothing of the body before authentication.** The resource filter runs after routing, the rate limiter and the
    (anonymous) authorization, before model binding: it reads the raw bytes (bounded, also without `Content-Length`),
    checks channel + secret, the timestamp window and the signature, and only then hands model binding a read-only
    stream over **the same bytes** it verified. The signature covers `"{timestamp}.{bytes}"` exactly as sent.
  - **Comparison.** Every (secret, signature) pair is compared with `FixedTimeEquals` and all pairs are always
    compared; only well-formed `v1=<64 hex>` values are considered; the timestamp parser takes digits only.
  - **Identity.** On success `HttpContext.User` is replaced by a principal with one claim (`sub` =
    `webhook:{slug}`), no authentication type, no role: every policy, the fallback included, refuses it (tested),
    and a bearer token sent along never acts. The identity exists only inside the one action that has the filter —
    a better choice than an authentication scheme that any policy could name (report, step 5).
  - **Rate limit first, per address + configured slug;** unknown slugs share one window per address, so made-up
    slugs multiply neither the limit nor the limiters in memory (deviation 3 — correct).
  - **Normalisation.** Items before the customer (a line the channel does not sell creates nobody); one query for all
    lines, Active mappings of this channel only, the key rule shared with the simulator (`ChannelProductKey`);
    customer by `ChannelCustomers` with the unique index deciding a race, never updated afterwards; then the intake
    **unchanged**, so duplicate / conflict behave exactly as for the order-entry page.
  - **Logs.** One Warning per refusal with the slug only when it has the form of a slug, the reason code and the
    client address; no body, header value, signature or secret (capturing-logger test).
  - **A1/A2.** Any read failure except a shutdown cancellation ends only its channel (`unexpected`, Error with the
    exception) — a Polly-internal cancellation while the host runs is correctly treated as such a failure; in-sync
    pairs with a transient final failure get one task, drift first under the cap.
  - **A3.** Partial unique index on `(ChannelId, ExternalSku) WHERE ExternalProductId IS NULL`; development was probed
    first through the API (0 duplicated SKUs) and the migration applied.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1559/1559; mutations 19/19 (Part A) and 37/37
  (C2, one equivalent explained); frontend typecheck, lint, build, 2 of 2 full runs and the slow run green at the first
  attempt, first-load set unchanged (969.55 kB raw); migrations applied on a throwaway environment, then on
  development as the migrator; webhook secrets set on development by the key helper; smoke 023 34/34 on development.
- **Doc deviations 29–31 confirmed** (31 reads: the webhook identity is `webhook:{slug}` without a role; it is recorded
  as `receivedBy` of the order message and `CreatedBy` of the customer and its channel mapping — see question 1).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | No `CreatedBy` on task and outbox row | Accepted — the columns do not exist; see question 1. |
| 2 | Codes `webhook_body_too_large`, `webhook_rate_limited` as ProblemDetails | Accepted. |
| 3 | Shared rate-limit window per address for slugs without a webhook | Accepted. |
| 4 | `ChannelProductKey` in Application, the simulator delegates | Accepted — one rule, the right layer. |
| 5 | `CustomerRules` shared by the customer form and the webhook | Accepted. |
| 6 | `AttemptErrorCodes.Unexpected` | Accepted. |
| 7 | A2 counting (`tasksCreated` includes repeats; drift first) | Accepted. |
| 8 | Validation choices (neither/both identifiers on `externalProductId`, blank = not given, limits only ≥ 1) | Accepted. |
| 9 | Extra switches of the helpers; `.env` created when missing | Accepted. |
| 10 | Existing tests adapted (unique SKUs per test; A1 failure moved to the run's own write) | Accepted. |
| 11 | `unexpected` without a Logs label | Accepted for 023; label in 024 A1. |

## Questions of the report
1. **No `CreatedBy` on `ProcessingTasks` / `OutboxEvents`.** **`receivedBy` is enough; no columns.** The plan
   (Cowork) was wrong to name those columns — both tables have timestamps only since task 001, and the submitter of any
   order (staff on the order-entry page, now `webhook:{slug}`) has always been recorded as `receivedBy` in the
   `order.created` payload. What a webhook created is identifiable without it: the order's channel, and `CreatedBy` =
   `webhook:{slug}` on the customer and its `ChannelCustomers` row. The gap that remains is older than 023 and not
   specific to webhooks — **no page can show who submitted an order**. Recorded in the log with its trigger: when
   users ask for it, add `ReceivedBy` to `Orders` (set by the Order Worker from the payload), not audit columns on
   processing tables. Implementation log corrected.
2. **Different timing of the 404s.** **Acceptable.** The only thing it can reveal is whether a slug is configured
   for webhooks but inactive; slugs are not secrets (they are in every order route), and nothing is learned about
   secrets or data. Equalising the time would cost a query per made-up slug — the opposite of what deviation 3
   protects.
3. **`unexpected` on the Logs page.** **Yes, in 024 A1** (024 adds event types and task types that need Logs labels
   anyway).
4. **Deviations 2 and 3.** Confirmed (table above).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **`unexpected` has no label** on the Logs page (question 3). | **024 A1:** `logs.channelErrors.unexpected` (vi/en) — "unexpected error (see the Workers log)". |
| P3 | **A smoke script's frontend step cannot be skipped** for a foreign environment, where it runs next to foreign hosts and is load-sensitive (report follow-up; the red step 35 of the throwaway run said nothing about the task). | **024 A2:** `_common.ps1` `Invoke-FrontendChecks` honours `SMOKE_SKIP_FRONTEND=1` (recorded as one step "frontend checks skipped (SMOKE_SKIP_FRONTEND)" with expected = actual, so the table shows it); allowed only together with `SMOKE_BASE_URL` (a foreign environment) — on the local default the variable is refused with a clear message. README "Smoke scripts". |
| — | **Request paths in Development text logs** are written decoded (an encoded line break becomes a line break in the console template); production output is compact JSON. Pre-existing, every endpoint. | Recorded for **026** (log forging in the text template). |
| — | **Rate limits are per API instance** (in-memory fixed windows): with N instances a sender gets N × the limit. | Accepted — recorded for **025** next to `ForwardedHeaders`; a shared limiter would need new infrastructure. |

## Assessment
A careful piece of security work. The order of steps is exactly the plan's, and the code proves the property that
matters most — the signature is checked over the bytes that are then bound, and nothing parses the body before it —
instead of assuming it. The identity design is better than either option the task offered: an unauthenticated,
role-less principal that only one filter on one action can create, so no policy can ever accept it by accident. The
normalisation reuses the intake without touching it, which keeps duplicate and conflict semantics identical for every
input path. Thirty-seven mutants, including the order of the filter's steps and the body limit without
`Content-Length`, show the tests hold the line where a webhook endpoint usually leaks. The one mismatch (question 1)
was in the plan; the report flagged it instead of inventing columns.

## Manual check
None for the user. Optional demo: `scripts\send-webhook.ps1 -Channel website -BodyFile <file>` with a body as in README
"Webhooks" (a mapping with that external product id must exist and be Active).

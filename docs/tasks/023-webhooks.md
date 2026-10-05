# Task 023 — Webhooks `POST /api/webhooks/{slug}`: signed, rate-limited, normalised through channel mappings; Part A (review 022)

Status: Open
Written by: Cowork, 2026-10-05
Depends on: 022 (accepted)

## Context
Doc §8.1 ("UI / Webhook / Polling → … Identify CHANNEL → Normalize → Common Order DTO → order.created"), §11
(`POST /api/webhooks/{slug}` "Nhận webhook"), §6 (Webhook: verify signature/secret; Rate limit: public/webhook
endpoints; Secrets in environment / secret manager; Logging: no secret), §13 ("Webhook duplicate → unique constraint +
idempotency"); CLAUDE.md.

**Decision (user, 2026-10-05):** the webhook body carries the **channel's own identifiers** — items by external product
id / external SKU through the channel's mappings, the customer by the channel's external customer id through a new
`ChannelCustomers` mapping (customer created on first sight). Every other decision is in the implementation log,
"Task 023 plan" — read it first; doc deviations 29–31 are listed there.

Read first: implementation log "Task 023 plan", "Steps 4–5" and "Task 004 result" (decision A, idempotency key,
request hash), "Task 014 plan" (mappings), "Task 022 result"; review 022; backend `OrdersController`,
`OrderIntakeService`, `SubmitOrderRequestValidator`, `OrderRequestHash`, `OrderIdempotencyKey`, `CustomerValidators`,
`ChannelService` (mapping writes, 23505 handling), `ChannelProductConfiguration`, `ExpectedUniqueViolations`,
`SimulatedChannelStock.ExternalKeyOf`, `RateLimitPolicies`, `Program.cs` (middleware order, fallback policy),
`HttpCurrentUser`, `CorrelationIdMiddleware`, `GlobalExceptionHandler`, `ValidationMessages` / `FieldNames`,
`StockReconciliation.cs`, `StockReconciliationContracts.cs`; frontend `ChannelProductFormModal.tsx` (`CODE_FIELDS`),
`features/logs/eventSummary.ts`; `scripts/setup-object-storage.ps1` (pattern of a key helper), `scripts/smoke/_common.ps1`.

**Stop and report (`Blocked`)** if: verifying the signature needs anything but the raw request bytes and the
configured secret (e.g. a body re-serialised before hashing); a webhook could reach any other endpoint or act with a
role; creating the A3 index fails on development (existing duplicates — the migration runs in a transaction, nothing is
applied; report the duplicates, do not change data); the intake would need a change of its contract or its
idempotency; or a decision is needed that is not in this file or the log.

## Part A — Follow-ups of review 022 (first; A1–A2 one commit, A3 its own)

### A1. A failed read stops only its channel (P3, review 022 question 2)
In `StockReconciliation`, any exception of the channel read except `OperationCanceledException` while the host stops
makes that channel unreadable for the run with code **`unexpected`** (the classifier's code for unknown failures; one
constant), logged at **Error with the exception**; `ChannelApiException` stays a Warning without the exception. A
failure of the run's own database work still ends the run.
Tests: an adapter that throws `InvalidOperationException` for one channel → that channel in `unreadable` with
`unexpected`, the other channels reconciled, one Error log carrying the exception; shutdown during a read → the run
ends, no event.

### A2. Failed pairs that are already in sync (P3, review 022)
A pair whose newest finished stock-sync task is `Failed` with a **transient** code (anything but `channel_rejected`)
gets one task **even when the channel shows the central value**, within the cap. Counted as `failedRetried` (result,
`reconciliation.completed` payload + allowlist, Serilog line); the event is written when such a task was created
(it is a created task). Logs/Audit line: "N failed syncs repeated" when > 0 (vi/en). The run still writes no alert —
the task's success resolves the pair's `StockSyncFailed` alert through `CompleteAsync`.
Tests: in sync + newest `Failed` with `channel_circuit_open` → one task, `failedRetried` 1, end to end the alert is
`Resolved` after the task succeeds and the next run creates nothing; in sync + newest `Failed` `channel_rejected` →
skipped as today; in sync + newest `Succeeded` → nothing; the cap counts these tasks. Mutants: the condition without
"transient", the counter, the cap — each killed (list in the report).

### A3. One external SKU per channel (P3, review 022 question 4)
- Partial unique index `IX_ChannelProducts_ChannelId_ExternalSku` on `(ChannelId, ExternalSku) WHERE
  "ExternalProductId" IS NULL` (Inactive mappings included, like the external product id index), in its own additive
  migration; registered in `ExpectedUniqueViolations`; `ChannelService` maps it to **409 `external_sku_taken`** (field
  `externalSku`, localised vi/en) on create and update.
- Frontend: `CODE_FIELDS` of `ChannelProductFormModal` gets `external_sku_taken: 'externalSku'`; texts vi/en.
- Tests: create and update with a taken SKU → 409 on the field (en/vi); the same SKU on another channel, or next to a
  mapping that has an external product id → allowed; the dialog shows the message on the field.
- On development the migration fails if duplicates exist — then **stop** (see above).

## Part B — Webhook infrastructure

### B1. Options `Webhooks` (API, validator + `ValidateOnStart`)
`TimestampTolerance` (5 min; 30 s–15 min), `MaxBodyBytes` (65,536), `PermitsPerMinute` (120, per client IP and slug),
`Channels:{slug}:Secrets` (list of one or two secrets, each ≥ 32 characters; empty or missing = no webhook for that
slug). A configured secret that is too short fails the start (the message names the slug, never the value). Secrets
only from user-secrets / environment — none in `appsettings*.json`.

### B2. Key helper `scripts/setup-webhook-secrets.ps1` (Windows PowerShell 5.1, ASCII)
For each seeded slug (`website`, `pos`, `digital-platform`): when `backend/src/Omnichannel.Api/.env` (git-ignored) has
no `WEBHOOK_SECRET_<SLUG>`, generate one (32 random bytes, base64url) and add it; then set the API user-secret
`Webhooks:Channels:<slug>:Secrets:0` to the `.env` value (stdin, never on a command line). Idempotent, prints no
secret. Try it on a throwaway target first, as the storage helper.

### B3. Signing helper `scripts/send-webhook.ps1`
`-Channel <slug> -BodyFile <json> [-Timestamp <unix>] [-BaseUrl]`: reads the secret from the `.env`, signs the **file's
bytes as sent**, posts them, prints status and response body (never the secret or the signature). Used by the smoke
script and for demos; README shows an example body.

### B4. Model and migration `ChannelCustomers`
Entity `ChannelCustomer` (`AuditableEntity`): `ChannelCustomerId` PK (UUIDv7), `ChannelId` FK, `ExternalCustomerId`
varchar(128) trimmed, `CustomerId` FK; unique `(ChannelId, ExternalCustomerId)` (in `ExpectedUniqueViolations`),
index on `CustomerId`. Additive migration (may share the commit of B5; not the A3 migration).

## Part C — The endpoint

### C1. `POST /api/webhooks/{slug}` (`WebhooksController`, `[AllowAnonymous]` to JWT)
Steps, in this order (log "Task 023 plan"):
1. Rate limit policy `webhook` (fixed window per client IP + slug) → 429.
2. Raw body read up to `MaxBodyBytes` → 413 beyond.
3. Active channel with configured secrets → otherwise 404 `channel_not_found` (unknown, inactive and unconfigured look
   the same).
4. `X-Webhook-Timestamp` integer within ±`TimestampTolerance` → otherwise 401 `webhook_timestamp_invalid`;
   `X-Webhook-Signature` with one or more `v1=<hex>` values, HMAC-SHA256(secret, `"{timestamp}.{raw bytes}"`), each
   configured secret tried, `CryptographicOperations.FixedTimeEquals` → otherwise 401 `webhook_signature_invalid`.
   Nothing of the body is parsed before this step.
5. The request now acts as `webhook:{slug}` **without any role** (audit fields of everything it writes); it can reach
   no other endpoint. Choose the mechanism (an authentication handler of its own scheme, or a scoped caller identity);
   say which and why.
6. JSON (camelCase, API serializer options) and FluentValidation of the webhook body (log "Task 023 plan"): order id
   (≤ 128 after trim), customer (`externalCustomerId` ≤ 128, name / phone / email with the rules of
   `CustomerValidators`), items 1–100, each with **exactly one** of `externalProductId` / `externalSku` and `quantity`
   1–10,000, no item key twice → 400 with camelCase keys (`items[1].externalSku`, `customer.phone`), localised.
   Malformed JSON → 400 as elsewhere.
7. Items → products by the key rule (`SimulatedChannelStock.ExternalKeyOf` semantics: product id, or the SKU of a
   mapping without a product id); the mapping must be Active → otherwise 400 on the item ("not sold on this channel",
   vi/en).
8. Customer: `ChannelCustomers` lookup; first sight → customer + mapping in one transaction (unique violation → read
   the winner's row); never updated by later webhooks.
9. `IOrderIntakeService.SubmitAsync(slug, normalised request)` unchanged → 202 `SubmitOrderResponse` (duplicate
   included), 409 `order_conflict`. The correlation id comes from `X-Correlation-Id` when valid (existing middleware).
- Errors as ProblemDetails with `errorCode` like every endpoint. Logs: rejected (401/404/413/429) → one Warning with
  slug, reason code and client IP; never body, headers' values, signature or secret.
- README: new section "Webhooks" (contract, signing with an example in PowerShell, secrets and rotation, the two
  helpers, limits, error codes, what a webhook can and cannot do); `.http` sample without a real secret.

### C2. Tests (required)
Unit: signature parsing (one / several `v1=`, other versions ignored, malformed), HMAC over timestamp + raw bytes,
timestamp window edges (exactly ±tolerance accepted, one second beyond refused), options validator (short secret, empty
list, tolerance range).
Integration (API host):
- Signature: valid → 202; wrong secret, tampered byte, body re-serialised with other whitespace, missing headers,
  timestamp too old / too new / not a number → 401 with the right code and **nothing written**; the second (rotation)
  secret works; a valid JWT without a signature → 401 (a JWT never replaces the signature).
- 404 for an unknown, an inactive and an unconfigured slug; 413 above the limit; 429 after the limit (small limit in
  the test configuration), per IP + slug (another slug still accepted).
- Validation 400 keys (en/vi); an item of an Inactive mapping, of another channel, by SKU of a mapping that has a
  product id → 400 on the item.
- Customer: first sight creates customer + mapping (`CreatedBy` = `webhook:{slug}`); a second webhook with other name /
  phone reuses it unchanged; two concurrent first sights → one customer.
- Intake reuse: the same webhook twice → 202 `duplicate`, same task; same order id, other items → 409 `order_conflict`;
  task and outbox row with `CreatedBy` `webhook:{slug}` and the caller's correlation id.
- The webhook identity has no role: with the request's identity, a staff endpoint is unreachable (e.g. an authorization
  test of the principal/caller used by the endpoint).
- Logs: a capturing logger over a run of all the above contains neither a secret nor a signature value nor a body.
- End to end (Order Worker running): a webhook → order `Reserved` with the mapped product, the price of the product and
  the channel's customer.
Mutations: about ten (signature compare, timestamp window, key rule, Active filter, customer reuse, the order of steps 3–6)
— each killed; list in the report.

### C3. Smoke script `scripts/smoke/task-023.ps1`
Strict `Assert-Step`; API + Workers (`Start-Smoke`, `Start-SmokeWorkers`); needs the webhook secrets (`setup-webhook-
secrets.ps1` — a clear failed step if the `.env` has none). Smoke Manager; product `SMOKE-023-<n>`, stock 10; mapping on
`website` with external product id `WEB-023-<n>`:
1. Signed webhook (via `send-webhook.ps1` or the same signing code) with a new external customer and 2 units → 202; the
   task `Succeeded`; the order shows the product, 2 units and the customer's name; the customer exists once.
2. The same body again (new timestamp) → 202 `duplicate` true, same task id.
3. Tampered body → 401 `webhook_signature_invalid`; timestamp 10 min old → 401 `webhook_timestamp_invalid`; unknown
   external product id → 400 on `items[0].externalProductId`.
4. A3: a second mapping on `website` (another product) with an external SKU already used by a mapping without a product
   id → 409 `external_sku_taken`.
5. Clean-up: smoke mappings `Inactive`, smoke users locked. Then `Invoke-FrontendChecks`.

## Acceptance criteria
- [ ] Part A first (A1–A2 one commit, A3 its own with its migration); Parts B–C after.
- [ ] Backend build 0 warnings, all tests pass; mutation lists (A2, C2) in the report.
- [ ] Migrations additive only; on development: `migrations list` shows exactly this task's two pending, then
      `database update` as the migrator (Development steps of `docs/tasks/README.md`).
- [ ] `scripts/setup-webhook-secrets.ps1` applied on development (development step 1 of the README, as the storage
      helper); no secret printed, committed or logged.
- [ ] Frontend (A2 line, A3 field): typecheck, lint, build; two consecutive full `npm test` runs and one full
      `npm run test:slow`, green, under the conditions of README "Frontend"; vi/en parity; first-load set before/after.
- [ ] `scripts/smoke/task-023.ps1` on development; table in the report.
- [ ] README: "Webhooks" (C1), A2 counter in "Reconciliation", A3 rule in the mapping section; `.http` sample.
- [ ] "Development steps (applied)" in the report; "Manual steps for the user": none, or only a step that could not be
      applied, with the reason.

## Out of scope
Polling of channels (doc §8.1 "Polling"); webhooks for anything but new orders (cancellation, updates, payment —
024 decides where payment comes from); customer updates from webhooks; a UI for webhook secrets or a webhook log page;
secrets in the database; per-channel signature schemes; IP allowlists; a nonce store (the timestamp window plus order
idempotency bound replays); `ForwardedHeaders` behind a proxy (025); a frontend webhook simulator (the Order Entry page
stays the demo input).

# Report 023 — Webhooks `POST /api/webhooks/{slug}`: signed, rate-limited, normalised through channel mappings; Part A (review 022)

Status: Done
Commit: `6b87779` (A1–A2), `919620a` (A3 with its migration), `f440944` (Parts B–C); this report in its own commit

## Summary
A channel can now send a new order to `POST /api/webhooks/{slug}` in its own identifiers. The request passes, in this
order: rate limit per client address and channel → raw body up to 64 KiB → Active channel with a configured secret →
timestamp window and HMAC-SHA256 over `"{timestamp}.{raw bytes}"` → identity `webhook:{slug}` → JSON + validation →
items through the channel's Active mappings → customer through the new `ChannelCustomers` mapping (created on first
sight, never updated) → the existing order intake, unchanged (202, duplicate, 409). Verifying the signature needs only
the raw bytes and the secret; nothing of the body is parsed before it. Part A: a read failure that is no channel answer
ends only its channel (`unexpected`, Error with the exception), failed syncs of pairs that are in sync are repeated
(`failedRetried`), and an external SKU is unique per channel among mappings without an external product id (409
`external_sku_taken`). Both migrations and the webhook secrets are applied on development; smoke 34/34 there.

**One point needs Cowork's eye** (see "Blocked / questions", question 1): `ProcessingTasks` and `OutboxEvents` have no
`CreatedBy` column, so "task and outbox row with `CreatedBy` `webhook:{slug}`" could not be implemented as written.

## Files changed
**A1–A2 (`6b87779`)**
- `backend/src/Omnichannel.Application/Processing/AttemptErrorCodes.cs` — new: `Unexpected = "unexpected"`, the one constant; `Workers/Processing/ProcessingErrorClassifier.cs` refers to it.
- `…/Application/Channels/StockReconciliationContracts.cs` — `StockReconciliationRules.RepeatsFailedSync`, `TasksAllowed(wanted, …)`, `ReconciliationRunResult.FailedRetried`, docs of `Deferred` / `TasksCreated` / `UnreadableChannel.ErrorCode`.
- `…/Infrastructure/Persistence/Channels/StockReconciliation.cs` — catch of non-channel read failures (not while the host stops); in-sync pairs with a transient final failure get a task, drift first under the cap; `failedRetried` in the event.
- `…/Application/SystemEvents/SystemEventDetails.cs` — allowlist + `failedRetried`. `…/Workers/ReconciliationWorker/ReconciliationService.cs` — the log line names it.
- Tests: `Integration/Workers/ReconciliationTests.cs` (+8 tests / 12 cases, one adapted), `ReconciliationTests.EndToEnd.cs` (+1, service test strengthened), `Workers/StockReconciliationRulesTests.cs`, `SystemEvents/SystemEventDetailsTests.cs`, `Integration/SystemEventEndpointsTests.cs`.
- Frontend: `features/logs/eventSummary.ts`, `logs.test.tsx`, `test/systemEvents.ts`, `shared/i18n/vi.json`, `en.json` ("N failed syncs repeated" / "chạy lại N đồng bộ đã lỗi").
- `README.md` — section 8 (repeated failed syncs, unreadable channel with `unexpected`), event table, frontend note.

**A3 (`919620a`)**
- `…/Infrastructure/Persistence/ExpectedUniqueViolations.cs` (`ChannelProductExternalSku`), `Configurations/ChannelProductConfiguration.cs` (partial unique index), `Migrations/20261005143006_ChannelProductExternalSku*.cs` + snapshot (one `CreateIndex`).
- `…/Application/Channels/ChannelProductContracts.cs` (`ExternalSkuTaken`), `IChannelService.cs` (docs), `…/Persistence/Channels/ChannelService.cs` (409 on create and update).
- Tests: `Integration/ChannelProductEndpointsTests.cs` (+3 tests), `DatabaseFailureLogTests.cs`; `Workers/StockSyncWorkerTests.cs`, `StockSyncDeactivationTests.cs` — SKU-only mappings get a SKU of their own per test.
- Frontend: `features/channels/components/ChannelProductFormModal.tsx` (`CODE_FIELDS`), `channelDetail.test.tsx` (+1 test, one extended), `shared/i18n/vi.json`, `en.json`.
- `README.md` — "Channel product mappings": the key rule and what is unique.

**Parts B–C (`f440944`)**
- `backend/src/Omnichannel.Api/Auth/WebhookOptions.cs` — options + validator (B1). `WebhookSignature.cs` — headers, timestamp parsing and window, `v1=` parsing, HMAC, constant-time match. `WebhookCaller.cs` — the role-less principal, slug normalisation, slug for logs. `WebhookRejection.cs` — error codes, ProblemDetails and the one Warning of a refusal.
- `…/Api/Filters/WebhookSignatureFilter.cs` — steps 2–5 as a resource filter. `…/Api/Controllers/WebhooksController.cs` — the action (thin).
- `…/Api/Auth/RateLimitPolicies.cs` — policy `webhook`, `OnRejected` for it. `AuthServiceCollectionExtensions.cs` (`AddWebhooks`), `Program.cs`.
- `…/Application/Orders/WebhookOrderContracts.cs`, `WebhookOrderRequestValidator.cs`, `IWebhookOrderService.cs` — body, rules, service contract. `…/Application/Channels/ChannelProductKey.cs` — the one key rule (`id:` / `sku:`). `…/Application/Customers/CustomerValidators.cs` — `CustomerRules` shared by the customer form and the webhook. Resources: `ItemIdentifierExactlyOne`, `ItemNotSoldOnChannel` (vi/en); field names `customer`, `externalCustomerId`, `quantity` (vi/en).
- `…/Domain/Entities/ChannelCustomer.cs`; `…/Infrastructure/Persistence/Configurations/ChannelCustomerConfiguration.cs`, `OmnichannelDbContext.cs`, `ExpectedUniqueViolations.cs` (`ChannelCustomerExternalId`), `Migrations/20261005144400_ChannelCustomers*.cs` + snapshot (B4).
- `…/Infrastructure/Persistence/Orders/WebhookOrderService.cs` — steps 7–9. `DependencyInjection.cs`. `ExternalChannels/SimulatedChannelStock.cs` — `ExternalKeyOf` delegates to `ChannelProductKey`.
- Tests: `Auth/WebhookSignatureTests.cs` (83 cases), `Validation/WebhookOrderRequestValidatorTests.cs` (20), `Integration/WebhookEndpointsTests.cs` + `.Orders.cs` (22 tests / theory rows), `WebhookEndToEndTests.cs`, `WebhookTesting.cs` (an independent signer), `ApiFactory.cs` (test secrets, limit 20/min), `Persistence/ModelConfigurationTests.cs`, `DatabaseFailureLogTests.cs`.
- `scripts/setup-webhook-secrets.ps1` (B2), `scripts/send-webhook.ps1` (B3), `scripts/smoke/task-023.ps1` (C3).
- `README.md` — new section 9 "Webhooks"; `Omnichannel.Api.http` — a sample without a secret.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` (backend, state of `f440944`) | 0 errors, **0 warnings** |
| `dotnet test` (backend, state of `f440944`) | **1559 passed**, 0 failed (4 m 15 s) — 163 more than 022 |
| `dotnet test` after A3 (state of `919620a`) | 1426 passed, 0 failed |
| `dotnet test --filter FullyQualifiedName~Webhook` | 126 passed (33 s), the end-to-end test included |
| Mutation run Part A (A1, A2), one test filter each | **19 of 19 distinct mutants killed** (list below) |
| Mutation run C2, one targeted test each | **37 of 37 killed** + 1 equivalent mutant (list below) |
| `npm run typecheck` / `npm run lint` / `npm run build` | exit 0 / exit 0 / built |
| `npm test` × 2 consecutive (final state) | **2 of 2 green**, 455 tests, 30 files; 356 s, 260 s |
| `npm run test:slow` (full, final state) | **green at the first attempt**, 455 / 455 (435 s) |
| `npm run measure` | first-load set **969.55 → 969.55 kB raw**, 317.94 → 317.94 kB gzip (the new texts are in the language chunks) |
| `scripts\setup-webhook-secrets.ps1` on a throwaway `.env` + throwaway user-secrets id | generates 2, keeps a hand-made one, second run changes nothing, no BOM, no secret in its output; a too-short value fails naming the entry only; a missing file is created |
| Throwaway environment (own PostgreSQL + RabbitMQ, hosts as `omnichannel_app`, environment `Smoke`) | 13 migrations from an empty database, the two of this task last, 0 pending; smoke steps 01–34 green, **step 35 `npm test` red** (see below) |
| Read-only probe of development through the API before A3 | 4 channels; website 11 / pos 1 mappings without an external product id, **0 duplicated SKUs** |
| `dotnet ef migrations list` / `database update` on development | exactly the task's two pending → applied → 0 pending |
| `scripts\smoke\task-023.ps1` on development | **PASSED: all 34 checks** (table below) |

Frontend runs: no Browser pane was opened in this session; no API / Workers host, backend build or test ran next to
the two full runs and the slow run (the development containers were up). vi/en parity is part of the suite.

**The red `npm test` of the throwaway run.** The script cannot stop hosts it did not start, so its frontend step ran
next to the throwaway API, Workers host and two extra containers: two tests of `orders/orderEntry.test.tsx` timed out
(20 s). Not attributed to load without checking: that file alone afterwards — 27 / 27; then the two full runs, the slow
run and step 35 of the development run (hosts stopped by the script) — all green. `orderEntry.test.tsx` is not touched
by this task.

**Mutations, Part A** (all killed). A2: the condition without "transient"; without "failed"; an in-sync pair never
repeated; the counter stays zero; counts drift tasks too; counts pairs beyond the cap; the cap ignores repeats; the cap
not applied to repeats; repeats before drift; `deferred` counts drift only; the event without `failedRetried`; the log
line names another number (survived at first — the service test now has a repeated pair — then killed). A1: the catch
never applies (replacement for a variant that did not compile); a cancelled read while the host runs ends the run; the
host stopping is an unreadable channel (replacement, same reason); another code than `unexpected`; Warning instead of
Error; Error without the exception; a channel answer logged with its exception.

**Mutations, C2** (all killed). *Signature compare:* any well-formed value accepted; only the first secret; only the
first signature; timestamp not signed; body not signed; other schemes count; check skipped without a `v1` value.
*Timestamp window:* `<` instead of `<=`; one second beyond accepted; future always accepted; window not checked in the
filter (compilable variant); a signed number accepted. *Order of steps:* channel before body (3/2); headers before
channel (4/3); signature before timestamp; no 404 for a slug without a secret; inactive channel not refused (compilable
variant); customer before items (8/7). *Identity:* the request keeps its principal; the webhook principal is an
authenticated identity. *Body limit:* exactly the limit refused; a body without `Content-Length` not limited (survived
at first — the default test client buffers a body and so announces its length; the test now uses an unbuffered client —
then killed). *Rate limit:* per address only; per channel only; a window per made-up slug. *Key rule:* a mapping with
both ids known by its SKU; id and SKU share a key. *Active filter and friends:* no Active filter; no channel filter;
ids compared ignoring case. *Customer reuse:* mapping found whatever the channel; no mapping written; id not trimmed;
losing the race is an error. *Body rules:* at-least-one instead of exactly-one; a product named twice; no upper
quantity limit. **Equivalent (survives by construction):** the early exit on an announced `Content-Length` — the read
stops at the limit anyway. Also equivalent and not run: dropping `ExternalProductId IS NULL` from the SKU branch of
the item query (the dictionary key of such a mapping is its `id:` key, so a SKU lookup still misses).

**Smoke on development**
```
OK    Step                                                             Expected                           Actual
True  01 WEBHOOK_SECRET_WEBSITE is in the .env (setup-webhook-secrets) True                               True
True  02 create smoke manager                                          201                                201
True  03 create smoke product                                          201                                201
True  04 import 10 units                                               200                                200
True  05 the website channel exists                                    True                               True
True  06 map on website by external product id                         201                                201
True  07 signed webhook is accepted                                    202                                202
True  08 ... as a new order (duplicate false)                          False                              False
True  09 the task of the webhook order                                 Succeeded                          Succeeded
True  10 the order is Reserved under the channel order id              Reserved SMOKE-023-ORDER-34527     Reserved SMOKE-023-ORDER-34527
True  11 ... with the mapped product, 2 units                          SMOKE-023-34527 x 2                SMOKE-023-34527 x 2
True  12 ... for the customer the webhook named                        Smoke Webhook Customer 023-34527   Smoke Webhook Customer 023-34527
True  13 ... on the website channel                                    website                            website
True  14 the customer exists once                                      1                                  1
True  15 the same webhook again is accepted                            202                                202
True  16 ... as a duplicate                                            True                               True
True  17 ... of the same task                                          01a10fbe-2f0f-7fb2-a762-f73c28d5a8a0 (same)
True  18 ... and the customer still exists once                        1                                  1
True  19 a body changed after signing is refused                       401                                401
True  20 ... with                                                      webhook_signature_invalid          webhook_signature_invalid
True  21 a timestamp of ten minutes ago is refused                     401                                401
True  22 ... with                                                      webhook_timestamp_invalid          webhook_timestamp_invalid
True  23 an external product id nobody maps is refused                 400                                400
True  24 ... on the line                                               items[0].externalProductId         items[0].externalProductId
True  25 stock after one order of 2 (total / reserved / available)     10 / 2 / 8                         10 / 2 / 8
True  26 create two more smoke products                                201 201                            201 201
True  27 map one on website by external SKU only                       201                                201
True  28 the same external SKU for the other product is refused        409                                409
True  29 ... with                                                      external_sku_taken                 external_sku_taken
True  30 deactivate a smoke mapping                                    200                                200
True  31 deactivate a smoke mapping                                    200                                200
True  32 lock the smoke manager                                        200                                200
True  34 npm run build                                                 0                                  0
True  35 npm test                                                      0                                  0
PASSED: all 34 checks.
```
(Step 33 is `npm ci`, recorded only when packages are missing.)

## Step 5: the mechanism of the webhook identity (the task asks which and why)
**A caller identity scoped to the request, set by a resource filter of the one action — not an authentication
scheme.** `WebhookSignatureFilter` (`[ServiceFilter]` on `WebhooksController.Receive` only) runs after routing and the
rate limiter and before model binding. On success it replaces `HttpContext.User` with
`WebhookCaller.PrincipalOf(slug)`: one claim (`sub` = `webhook:{slug}`), **no authentication type, no role**, and hands
model binding the very bytes it verified. `HttpCurrentUser` is unchanged (it reads `sub`), so audit fields follow.

Why not a scheme: a registered scheme can be named by any endpoint's policy (or become a default) and its handler
would have to answer 404 and 413 from an authentication handler, before or after the JWT handler depending on policy
wiring. With the filter, the only code that can create the identity is attached to one action; the identity is not
even "authenticated" to the authorization system, so the three role policies **and the fallback policy** refuse it
(test `The_identity_of_a_webhook_passes_no_authorization_policy_of_the_api`); no token exists; a bearer token sent
along is discarded on success and irrelevant on failure (tests: token without signature → 401, token + signature →
actor `webhook:website`, signature headers on four other endpoints → 401).

## Deviations from the task
1. **`CreatedBy` of task and outbox row** — not possible as written; see question 1. Asserted instead: `receivedBy`
   = `webhook:{slug}` in the `order.created` payload, `CreatedBy` = `webhook:{slug}` on the customer and the
   `ChannelCustomers` row, the caller's correlation id on task and outbox row.
2. **Error codes for 413 and 429** (the task names none): `webhook_body_too_large`, `webhook_rate_limited`, as
   ProblemDetails like the others. The login policy keeps its bare 429.
3. **Rate-limit partition:** per client address and slug only for slugs that have a secret; every other slug shares
   one window per address. Otherwise made-up slugs would multiply both the limit and the limiters kept in memory.
4. **`ChannelProductKey` (Application)** holds the key rule; `SimulatedChannelStock.ExternalKeyOf` delegates to it
   (same strings, its tests unchanged). The webhook validator (Application) could not call Infrastructure.
5. **`CustomerRules`** extracted from `CustomerRequestValidator` so the webhook customer uses the same rules by
   construction (behaviour of the customer form unchanged; its tests pass).
6. **`AttemptErrorCodes.Unexpected`**, not `ProcessingErrorCodes`: that name is taken by the API's error codes.
7. **A2 counting:** `tasksCreated` includes the repeated syncs, `failedRetried` is that part of it; `deferred` counts
   every pair the cap left out; within a batch drifted pairs come first.
8. **Validation choices:** neither or both identifiers → one message on `items[n].externalProductId`; a blank
   identifier counts as not given; a product named twice → `items` (existing message); `MaxBodyBytes` and
   `PermitsPerMinute` are only checked to be ≥ 1 (the task gives no range).
9. **Helpers:** `send-webhook.ps1` also has `-SignatureOf` (sign other bytes: the smoke's tampered body), `-PassThru`,
   `-CorrelationId`, `-EnvFile`; the smoke script reads `SMOKE_WEBHOOK_ENV_FILE` for another environment (used by the
   throwaway run). `setup-webhook-secrets.ps1` creates the API `.env` when it does not exist.
10. **Existing tests adapted** for A3 (fixed SKUs such as `"WEB"` reused across tests on shared channels) and A1 (the
    "run that fails" test now fails the run's own database write instead of a read).
11. **`unexpected` has no label on the Logs page** (shown as it is, like any unlisted code): the task limits the
    frontend to the A2 line and the A3 field.

## Blocked / questions for Cowork
1. **No `CreatedBy` on `ProcessingTasks` / `OutboxEvents`.** Both are `IHasTimestamps`, not `AuditableEntity` (since
   task 001); the intake has always recorded its actor as `receivedBy` in the message payload. The plan ("`CreatedBy`
   of task and outbox row") assumes columns that do not exist. I did not add them: a schema change of two core tables
   is not in the task, and the acceptance criteria name exactly two migrations. Is `receivedBy` enough, or should a
   later task add the columns (then for every intake path, not only webhooks)?
2. **Timing of the 404.** An unconfigured slug is answered without a query, a configured but inactive one after one
   query: the answers are identical, the time is not. Acceptable, or should every 404 cost the same?
3. **`unexpected` on the Logs page** — add a label (vi/en) in the next frontend task?
4. **Deviation 3** (shared window for slugs without a webhook) and **deviation 2** (the two new codes): confirm.

## Out of scope / follow-ups
- **Request paths in Development text logs** (pre-existing, every endpoint): Serilog request logging writes the decoded
  path, so an encoded line break in a URL becomes a line break in the Development console template. Production output
  is compact JSON (escaped). The webhook's own Warning never writes a raw slug. Candidate for 026.
- A smoke script's frontend step cannot be skipped for a foreign environment; next to foreign hosts it is
  load-sensitive (this run). A switch for that would save a red step that says nothing about the task.

## Development steps (applied)
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-webhook-secrets.ps1` — exit 0: added
   `WEBHOOK_SECRET_WEBSITE`, `WEBHOOK_SECRET_POS`, `WEBHOOK_SECRET_DIGITAL_PLATFORM` to the API `.env`, set
   `Webhooks:Channels:<slug>:Secrets:0` in the API user-secrets; no secret printed.
2. No compose service changed.
3. Migrations as the migrator (connection from the user-level variable, never printed): `dotnet ef migrations list` →
   13 migrations, pending exactly `20261005143006_ChannelProductExternalSku` and `20261005144400_ChannelCustomers`
   (both additive: one `CreateIndex`; one `CreateTable` with two indexes); `dotnet ef database update` → exit 0, both
   applied; pending afterwards 0. Applied on the throwaway environment first.
4. No API / Workers host was running before; none is running now (checked after the smoke).
5. `scripts\smoke\task-023.ps1` on development — PASSED, 34 checks (table above). Its two mappings are Inactive, its
   manager is locked.

## Manual steps for the user
None. (An API you start yourself picks up the webhook secrets at its next start.)

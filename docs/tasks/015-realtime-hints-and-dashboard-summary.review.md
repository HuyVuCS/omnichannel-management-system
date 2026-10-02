# Review 015 — Realtime hints (PostgreSQL NOTIFY → SignalR) + dashboard summary

Reviewer: Cowork, 2026-10-02
Verdict: **Accepted** — no P0–P2 findings. The P3 findings are either accepted or go to 016 Part A, 016 design, or 026.

## Checked
- Report and commits `55993e4` (A1), `47ff45a` (A2), `a41165a` (A3), `f74251b` (Parts B and C), `d15281e` (README, A4,
  smoke script), `945e9a8` (strengthened test), `a8c0cfd` (report). Frontend: no file changed (`git diff --stat` empty).
  No migration in the range.
- Read in full: `RealtimeHintInterceptor`, `RealtimeHints.cs`, `PostgresHintListener`, `RealtimeHintBatcher`,
  `NotificationsHub`, `RealtimeOptions`, `AccessTokenRedactionEnricher`, the `OnMessageReceived` change, the `Program.cs`
  change, `DashboardService`, `DashboardContracts`, `DashboardSummaryQueryValidator` / `DashboardRange`, the A1 diff of
  `StockSyncService`, `GetProductAsync`, the README section, the `.http` additions. Tests read where they carry a claim
  of the report: `RealtimeNotifyTests` (transaction / implicit-commit visibility), `RealtimeHubTests` (token in logs,
  listener reconnect), test lists of the producer and summary tests.
- Checked by Cowork against the code (not only the report):
  - every write path to `Inventories`, `Orders` and `ProcessingTasks` goes through the change tracker or writes a tracked
    `InventoryMovement` in the same save: the only raw-SQL statements are the two `UPDATE "Inventories"` (import,
    reservation; both add movements) and two `SELECT … FOR UPDATE` row locks. No `ExecuteUpdate` / `ExecuteDelete` exists,
    so no change can bypass the interceptor today;
  - the interceptor is **scoped** and resolved inside the `AddDbContext` options lambda (options lifetime scoped, no
    `AddDbContextPool`, no `IDbContextFactory`), so `_pending` is never shared between concurrent contexts;
  - `pg_notify` inside an explicit transaction is not caught (fails with the transaction), after an implicit commit it is
    caught and logged — matches the task;
  - the query-string token is accepted only under `/api/hubs/notifications` and only when no `Authorization` header is
    present; `OnTokenValidated` (security stamp) still runs; `CloseOnAuthenticationExpiration` is set;
  - the listener uses its own unpooled connection, never the `DbContext`; a payload that is not a hint batch is logged by
    length only;
  - the summary uses the shared `InventoryPredicates.IsLowStock`, inclusive range on `Orders.CreatedAt` and on
    `ProcessingTasks.UpdatedAt` for finished tasks, current numbers without the range.
- Not re-run by Cowork: there is no .NET SDK in the user's VM. Build (0 warnings), 745/745 tests, smoke 27/27 on a
  throwaway environment and the 15 mutations are the report's.
- **The user reports (2026-10-02) that the 015 checks are done** on the development environment (smoke script and the
  manual check of the hint batch in the API log) and that the 014 migration was applied. The smoke table of that run was
  not pasted; recorded as confirmed by the user.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `AccessTokenRedactionEnricher` (redacts `access_token=` in every string property of every log event) | **Accepted.** It turns the "token never logged" requirement from a property of today's log levels into a property of the pipeline. Limit: it covers Serilog events only (scalar string properties; rendered messages follow from them). A reverse proxy or load balancer logs the URL on its own — 025/026. |
| 2 | Second payload guard (per-type `all` when one save does not fit) + API-side batch limit 200 events / queue 10,000 → `resync` | **Accepted.** Correct direction: degrade to "refetch" instead of failing a business transaction or flooding clients. Consequence for 016: see finding "refetch storms". |
| 3 | `byStatus`, `processing`, `stockSync` as lists of objects | **Accepted** (camelCase everywhere, stable order, zeros included). |
| 4 | Range validated in the validator and in `DashboardRange.Resolve` (only `from` given) | Accepted — same messages, key of the field the user supplied. |
| 5 | Listener, batcher, options in `Api/Hubs`; reuse of `RetryBackoff` | Accepted. `RetryBackoff` lives in `Infrastructure.RabbitMQ` but is a pure function; moving it is not worth a change now. |
| 6 | Hub tests use long polling | Accepted — the test server has no sockets. **The WebSocket path (Vite proxy `ws: true`) is part of the 016 browser check.** |
| 7 | A4 in the same commit as README and smoke script | Accepted. |
| 8 | 201 `Location` stays relative | Accepted (resolves through the new GET, tested). |
| 9 | Existing test files: `partial`, four log categories, `IssueTokenFor`, connection string exposed | Accepted — no assertion changed. |
| 10 | Nine summary statements | Accepted — plain aggregates, measured (23–32 ms for the whole endpoint at 8k orders / 24k tasks / 40k outbox rows). |

## Questions of the report
- **Growth of three summary statements:** accept until 026, no index now. Measured cost is single-digit milliseconds at a
  size the development database will not reach soon. Recorded in the roadmap (026) with the candidates the report names
  (partial index on `OutboxEvents` for `Status = 'Failed'`, partial index on `ProcessingTasks` for non-terminal
  statuses, an index with `UpdatedAt` for finished tasks). Trigger to do it earlier: the endpoint logs > 200 ms.
- **`stockSync.lastSucceededAt` "ever" vs "in the range": keep "ever".** "When did this channel last receive stock"
  is exactly what an operator needs when the range shows no success; a range-bound value would be null for a channel
  that synced an hour before the range began. 026 adds the index for it (partial on `StockSync`/`Succeeded`,
  `(ChannelId, UpdatedAt)`).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **Refetch storms are a client concern now.** One import of N mapped products fans out into up to 3·N stock-sync tasks with three transitions each; batches above 200 events become `resync`. The server side is bounded (one message per 250 ms window), but a client that refetches every visible query on every message would issue several summary + list requests per second during a sync burst, per open tab. | **016 design:** the client coalesces invalidations (per tag, trailing, ≥ 1 s) and maps event types to the tags actually on screen; `resync` invalidates only the realtime-driven tags, not the whole cache. |
| P3 | **A failed `pg_notify` after an implicit commit is logged twice** — `DatabaseFailureLogInterceptor` writes the failed command at Error before the interceptor's Warning ("clients catch up"). Only happens when the connection breaks right after the commit, which is a real database problem, so an Error is not wrong. | Accepted. Revisit if it shows up as noise. |
| P3 | **Summary statements are not one snapshot** (nine statements, no common transaction): sections can disagree by the changes committed between them (e.g. an order counted, its task not yet). | Accepted for an overview page; documented in the implementation log. A `REPEATABLE READ` read-only transaction is the fix if it ever matters. |
| P3 | **Smoke script started an API with the development secrets** while `SMOKE_BASE_URL` pointed at a throwaway port (the report's incident; no row written, one rejected login). | **016 Part A:** `_common.ps1` starts an API or a Workers host only when `SMOKE_BASE_URL` is not set (local default); otherwise an unreachable API is an error with a clear message. |
| P3 | **Hints go to every connected user** whatever the role. Hints carry ids only and every role may read orders, inventory and tasks, so nothing leaks today. | Accepted. Revisit with per-role data (e.g. a page only Managers may open whose ids are sensitive). |
| P3 | Hub `[Authorize(Policy = AnyStaff)]` is not observable by a test (fallback policy + every user has a role). | Accepted — keeps the intent explicit if a fourth role is ever added. |
| P3 | `ProcessingTaskSummaryResponse` carries `channelId` / `productId` but no SKU: a task list would show product GUIDs or need one lookup per row. | **016:** add `productSku` to the task summary and detail (join in the same query); channel names come from the channel list the client already loads. |

No P0–P2 findings.

## Assessment
The interceptor design meets the plan exactly: hints come from one place, cannot precede or outlive their data
(verified against PostgreSQL, including the mutant that sends on a second connection), and a failure after the commit
never fails the business operation. Token handling goes beyond the task in the right way. The listener/batcher pair is
small, bounded (queue, batch size, window) and degrades to `resync` rather than to errors. A1 is the minimal fix of the
014 P2 and is covered by three tests that fail on the old order of the checks.

## Manual check
Done by the user (2026-10-02). The visible realtime check (browser receives hints over WebSockets through the Vite proxy)
follows in 016.

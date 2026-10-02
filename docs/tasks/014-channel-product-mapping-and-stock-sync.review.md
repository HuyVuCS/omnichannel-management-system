# Review 014 — ChannelProduct mapping + Stock Sync Worker (simulated channels)

Reviewer: Cowork, 2026-10-02
Verdict: **Accepted** — one P2 finding goes into 015 Part A (it does not block: it needs a mapping or channel to be
deactivated while a sync of it is being retried).

## Checked
- Report and commits `6b62354` (A1), `bc68cc7` (A2), `4d06813` (Part B), `0c612a4` (README), `2a6ddfc` (report).
- Read in full: `DatabaseFailureLogInterceptor`, `ExpectedUniqueViolations`, `DbUpdateExceptionExtensions`,
  `StockSyncMessageHandler`, `StockSyncService`, `StockSyncRequests`, the mapping part of `ChannelService`, the retry part
  of `ProcessingTaskService`, `ResilientChannelStockClient`, `SimulatedChannelStockClient`, their registration,
  `StockSyncWorkerService`, the `Abandon` change of the consumer, the topology change, the migration, the Workers
  `appsettings.Development.json`, the `_common.ps1` change. `ProcessingAttemptStore` and the test files were read only
  where the report points to them.
- Re-run by Cowork on the user's machine (Linux VM): `npx tsc -b --noEmit` (frontend) → exit 0. No .NET SDK in that VM:
  backend build/test results (669/669), frontend tests (168/168), smoke 38/38 and the mutation table are the report's.
- **The user reports (2026-10-02) that the manual steps are done**: migration applied to the development database, smoke
  script and the manual check of 014, and the manual checks of 012 and 013. The smoke table of the development run was
  not pasted; recorded as confirmed by the user.
- Against the task: mapping + task + directed message in one `SaveChanges`; both 409s from the unique indexes; directed
  messages only for an Active channel with a queue; the handler never pushes the message snapshot, re-reads after each
  push, holds no transaction or lock during the channel call; one task per (channel, message) through the unique key with
  the 23505 resolved to the existing task; manual retry of a `StockSync` task writes a directed message instead of the
  fanned-out copy; Polly pipeline per slug as a singleton (breaker state survives across deliveries), breaker outside,
  timeout inside, only transient failures and timeouts counted, no Polly retry; no queue argument changed (tested against
  the literal 005/007 declaration); migration only adds nullable columns, two indexes, two FKs and a CHECK that every
  existing row satisfies; simulator settings contain no secret; `SkuRules` on only in `appsettings.Development.json`.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | A1 at EF Core level (event levels → Debug, interceptor writes the Error entries unless the cause is registered) instead of a Serilog filter | **Accepted** — answers the report's first question. The observation is decisive: `CommandError` carries no exception, so no log filter can classify it. One mechanism in `AddInfrastructure`, works in every host, no Serilog in Infrastructure. Known cost: with Debug enabled for those categories an unexpected failure appears twice; acceptable. A registered 23505 on a path that does *not* handle it is still visible: it propagates and is logged by `GlobalExceptionHandler` / the worker classifier at Error. |
| 2 | `IsUniqueViolation` never matches an unregistered name | **Accepted** — makes the registry impossible to bypass. |
| 3 | Existing test files changed (usings after the move; publisher test removes the new hosted service; two seeds use `Invoice` instead of `StockSync`) | Accepted — mechanical, no order assertion changed. |
| 4 | `ProcessingAttemptStore` extracted from `OrderReservationService` and shared | **Accepted** — the task said not to build a second mechanism; Order Worker tests unchanged and green. |
| 5 | `HandlerResult.Abandon` (delivery left unsettled during shutdown) | **Accepted.** Only returned while `ApplicationStopping` is signalled; the broker requeues on channel close and counts it in `x-delivery-count`, so repeated shutdowns mid-push are bounded by the delivery limit. Not for the Order Worker (answers the report's third question: its reservation is short and must finish). |
| 6 | Retry publisher logs the delay tier | Accepted (also useful for orders). |
| 7 | Channel errors logged without stack | Accepted (code + sanitised message describe them; unexpected exceptions keep the stack). |
| 8 | Simulation `Mode` bound as text, parsed per push | Accepted (a typo in a live-reloaded file must not break the reload). |
| 9 | Adapter as a keyed service behind the resilient client | Accepted (tests keep the real pipeline). |
| 10 | "At least one external id" reported on `externalProductId` | Accepted. |
| 11 | `Location` of the 201 points to a URL that has no GET | Accepted for now; a `GET` for one mapping is added in 015 Part A so the header is resolvable. |
| 12 | Poison messages write `stock_sync.message_rejected` (one row per queue for an unparseable fanned-out message) | Accepted (same as `order.message_rejected`). |
| 13 | Manual retry copies the source `RoutingKey` | Accepted (null for all existing rows). |
| 14 | `_common.ps1`: Workers start/stop, `SMOKE_ADMIN_*` alternative | Accepted (credentials are never printed). |
| 15 | Smoke run on a throwaway database | Accepted; the development run was done by the user afterwards. |
| 16 | Only `Polly.Core` 8.8.0 | Accepted (no DI integration needed). |

## Questions of the report
- **A1 mechanism:** EF Core-level hook accepted (deviation 1).
- **Open circuit and the attempt budget: accepted as it is.** Not counting `channel_circuit_open` would make the retry of
  a task unbounded during a long outage (the delay copies are new publishes, nothing else limits them). Because every
  push sends the *current* stock, only the newest task per (channel, product) matters after an outage; the recovery path
  is reconciliation (022, creates fresh sync tasks for drift) and, for operators, retry from the processing page (016),
  where failed stock-sync tasks must be filterable by channel. `StockSyncWorker:MaxAttempts` stays configurable.
- **`Abandon` for the Order Worker:** no (deviation 5).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P2 | **A stock-sync task can stay `RetryScheduled` (or `Pending`) forever.** `StockSyncService.ResolveInventoryChangeAsync` checks "channel Active and mapping Active" *before* it looks up the task by its idempotency key. Scenario: attempt 1 fails transiently → task `RetryScheduled`, delayed copy waiting; the mapping (or the channel) is deactivated; the copy returns → `NotMapped` → ACK, the existing task is never touched. The same in `RecordFinalFailureAsync` (`TaskId` null → ACK). The task is non-terminal with no message left: it shows as "retrying" on the dashboard indefinitely and cannot be retried manually (only `Failed` can). The handler already has the right behaviour one step later (`PushCurrentStockAsync`: inactive → task `Succeeded` without a push) but never reaches it. Not covered: the deactivation test only covers the directed initial sync. | 015 Part A1: look up the task by key first; the mapping/channel check only decides whether a *new* task is created. Integration test: transient failure → deactivate the mapping → the retry copy completes the task `Succeeded` without a push (and the same for the final-failure path). |
| P3 | `StockSyncWorkerService` awaits `Task.WhenAll` of three consumers: a topology mismatch thrown by one is only observed after the other two stop. Harmless today (the topology initializer and the other services stop the host on a mismatch), but the critical log line of this service is late. | Accepted; revisit if the consumers ever get their own topology steps. |
| P3 | Frontend bundle metric: the first-load set is now measured per file (984.04 kB) while 013 used a module-level method (978.50 kB); the script is not committed, so the next report cannot reproduce it. | 016 Part A: commit the measurement as a script (no new dependency) and use it in every frontend report. |
| P3 | README section 6 still says `order.processing` has no consumer (outdated since 006). | 015 Part A |
| P3 | `Omnichannel.Api.http` has no samples for the mapping endpoints. | 015 Part A (with the new endpoints of 015) |
| P3 | Several Workers instances or `Concurrency` > 1: convergence relies on the re-read loop (reasoned and tested within one instance), no multi-instance test. | 025 (when instances are scaled in compose) |
| P3 | Channels created through the API can be mapped but are never synced. | 016 shows it on the mapping UI |
| P3 | The simulator keeps no channel-side stock. | 022 decides the simulator state |

No P0–P1 findings.

## Manual check
Done by the user (2026-10-02), including the migration on the development database.

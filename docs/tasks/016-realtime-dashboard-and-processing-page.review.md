# Review 016 — Frontend: realtime client, dashboard, Sync & Processing page

Reviewer: Cowork, 2026-10-02
Verdict: **Accepted** — no P0–P2 findings. The three questions of the report are decided below; the changes they need
and the P3 findings go into 016b Part A.

## Checked
- Report and commits `d97b3ef` (A1), `c46e249` (A2), `cd78522` (A3), `78a71b8` (A4), `5cbe8c3` (A6), `c075867` (A5),
  `6101f5f` (Part B), `5de07dd` (Part C), `c6ee3dd` (Parts D, E), `4669e20` (smoke script), `054a42d` (browser fixes),
  `d4f7db5` (report). No migration in the range; the only backend change is Part B.
- Read in full: `realtimeConnection.ts`, `RealtimeManager.tsx`, `hintTags.ts`, `hub.ts`, `signalrHub.ts`,
  `realtimeSlice.ts`, `OrderOutcome.tsx`, `processingApi.ts`, `RetryTaskButton.tsx`, `dashboard/range.ts`, the
  `App.tsx` / `store.ts` / `baseApi.ts` / `vite.config.ts` / `main.tsx` / `shared/i18n/index.ts` /
  `useServerSearchSelect.ts` / `_common.ps1` diffs, the `ProcessingTaskService` diff. Skimmed: `DashboardPage.tsx`,
  `ProcessingPage.tsx`, tag providers of every list that realtime invalidates.
- Checked by Cowork against the code (not only the report):
  - **one connection at a time:** `RealtimeManager` starts one tick after the effect (StrictMode's immediate cleanup starts
    nothing) and chains every start on the previous `stop()`; `startRealtime` closes what is left of a failed attempt
    before the next one, ignores `onClose` of a connection it no longer owns, and `stop()` during `createHub` or
    `start()` ends without a connection;
  - **401 on negotiate** ends the session only when the token used is still the current one (same rule as
    `apiBaseQuery`); any other failure retries with 0, 2, 5, 10, 30 s, then every 30 s;
  - **the token never reaches the console:** client logging at Warning, every message passed through
    `redactAccessToken`; the URL is built from `window.location.origin` (dev proxy and same-origin deployment);
  - **tag map** matches the plan, and every tag it invalidates is provided (`Order`/`Product`/`ProcessingTask` `LIST`,
    `Order` id, `ProcessingTask` id, bare `Inventory`, `InventoryMovement`, `DashboardSummary`); a bare type in the
    coalescer supersedes its id tags; `resync` = the six realtime tags, never `resetApiState`;
  - **no client business rules:** the dashboard shows the server's numbers only (no sums, no thresholds, no range limit;
    the 400 is shown on the range control); `lastError` is never rendered; retry is hidden for Staff and for non-failed
    tasks, the API enforces both;
  - **Part B:** one `LeftJoin` in the page query and in the detail query (EF Core 10 operator), no second round trip;
  - **A1:** `_common.ps1` throws before starting anything when `SMOKE_BASE_URL` is set and the API (or the Workers host)
    is missing; **A5:** the active language is awaited before the first render, the other one is loaded before
    `i18n.changeLanguage`, a failed load is forgotten so the next switch retries.
- **Verification:** the report's runs (backend 0 warnings, 746/746; frontend 243/243, lint and typecheck clean at
  `c6ee3dd`; smoke `task-016.ps1` 23/23; the browser items listed under "Real-browser check") plus — **reported by the
  user on 2026-10-02 as done and passing** — what the report left open: `npm test` and `npm run lint` after `054a42d`,
  order entry with the Workers stopped for > 60 s ("still processing" → "Check again"), orders list at 1280 px in vi and
  en (A4), language switch on the task detail page. The output of those runs was not pasted; recorded as confirmed by
  the user. Not re-run by Cowork: the user's VM has no npm registry access and its `node_modules` are the Windows build
  (`@rolldown/binding-win32-x64-msvc`); the cloud workspace's registry access is blocked by policy.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | The library's automatic reconnect is not used; `realtimeConnection.ts` owns the retries | **Accepted.** `withAutomaticReconnect` does not retry a failed first start and cannot tell a refused token from an unreachable server — both are required. |
| 2 | "Offline" once the quick retries are used up (≈ 17 s) while retries go on every 30 s | **Accepted.** Matches what a user needs to know ("not live, not coming back by itself soon"). The tooltip says the screen may be outdated. |
| 3 | Dashboard links for "failed / succeeded in the range" carry type, status and `sortBy=updatedAt&sortDir=desc`, not the range | **Accepted.** The list filters on creation time, the summary counts by update time; a range on the link would hide counted tasks. A list filter on `updatedAt` is not worth a backend change now. |
| 4 | Relative times against `generatedAt`, not the browser clock | Accepted — consistent with the numbers next to them and immune to a wrong client clock. |
| 5 | A6 committed before A5 | Accepted (needed for the "before" number). |
| 6 | `test/server.ts` default handler for the summary, `uiSnapshot` finds the language switch by name, `task()` fixture fields, two `i18n.changeLanguage` → `changeLanguage` | Accepted — no assertion weakened. |
| 7 | `main.tsx` shows one bilingual line when the active language cannot be loaded | Accepted: without texts nothing else can be rendered. |

## Questions of the report
1. **New database connection costs ~2 s (`localhost` → `::1` first, PostgreSQL published on `127.0.0.1` only).**
   Decision: **use `127.0.0.1` in every local connection setting**, keep the loopback-only port binding. README examples
   (user-secrets of API and Workers, `OMNICHANNEL_MIGRATOR_CONNECTION`), the design-time factory's fallback and the
   Workers' `RabbitMq:HostName` (RabbitMQ is published the same way). The user changes the user-secrets and the
   environment variable (secrets are the user's). Alternative rejected: publishing the ports on `[::1]` as well —
   depends on Docker Desktop's IPv6 support and widens the binding for no gain. The containers (`Host=postgres`) are
   unaffected. → 016b Part A1.
2. **Latency budget "dashboard within ~2 s".** Path: outbox publisher poll (≤ 1 s) + Order Worker + hint batch
   (≤ 250 ms) + client window (1 s, trailing) + the refetch. The client window adds a full second even when nothing
   else is happening. Decision: **turn the coalescer into a throttle** — flush at once when no flush happened in the
   last second, otherwise at the end of that second with the union of what arrived. The bound review 015 asked for is
   unchanged (≤ 1 flush per second per tab), a burst still costs one refetch round per second, and the idle case loses
   up to 1 s. The criterion stays "typically ≤ 2 s" (a measured target, not a guarantee: publisher poll and connection
   pools vary). → 016b Part A2.
3. **Lists refetched twice per finished order in push mode.** The terminal task status reaches the outcome view through
   the same hint batch that carries `order.created` / `inventory.changed` (the Order Worker writes order, movements and
   task status in one `SaveChanges` → one `NOTIFY` → one batch → one flush), so the 013 invalidation is redundant while
   the connection is live; while it is not, it is the only one. Decision: **`OrderOutcome` invalidates the lists at the
   terminal state only when the connection is not `connected`.** (A task seen finished on mount while live finished
   either while live — its hints were flushed — or before the connection — the connect resync covered it.) → 016b
   Part A3.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | Questions 1–3 above. | 016b Part A1–A3. |
| P3 | **Hidden tabs refetch on every flush** (invalidation ignores `focused`). With several tabs open, each hidden one keeps refetching the dashboard during a sync burst. Bounded (≤ 1/s per tab). | Accepted. Revisit if users keep many tabs: pause the flush while hidden and resync on `visibilitychange`. |
| P3 | **First connect = one extra refetch** of every visible realtime query (mount fetch + connect resync). Planned ("the price"), measured: one summary refetch. | Accepted. |
| P3 | **Production deployment needs WebSocket upgrade** on whatever proxies `/api` (the dev proxy has `ws: true`). | 025: reverse proxy / load balancer config passes `Upgrade`/`Connection` for `/api/hubs/*`, idle timeout above the SignalR keep-alive; sticky sessions not needed with WebSockets only. |
| P3 | `products.test.tsx` "dialog reopened before its close animation" failed once under load (passed 3/3 alone and in two full runs). | Watch. If it fails again, fix the test's wait (animation end), not the timeout. |
| P3 | Retry confirmation uses `modal.confirm` (texts frozen while open) | Accepted — the existing rule for short yes/no confirmations (implementation log, task 012). |

No P0–P2 findings.

## Assessment
The realtime client meets the plan and goes beyond it where the library fell short (first-start retry, 401 detection
without parsing error texts). Its lifecycle is the riskiest part of the task and is correct on every path read:
StrictMode, logout during a pending start, token change, a lost connection that belongs to an older start. Hints map to
exactly the tags the pages provide; bursts are bounded per tab; the token stays out of the console and the API log.
The dashboard and the processing pages keep every rule in the server. The browser run found and fixed four layout and
label defects that tests could not see — the real run stays part of every frontend task.

## Manual check
Done by the user (2026-10-02): the open browser items of the report, `npm test` / `npm run lint` after the last fixes,
and the two-tab check.

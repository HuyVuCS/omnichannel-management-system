# Report 016 — Frontend: realtime client, dashboard, Sync & Processing page

Status: Partially done
Commit: `d97b3ef` (A1), `c46e249` (A2), `cd78522` (A3), `78a71b8` (A4), `5cbe8c3` (A6), `c075867` (A5), `6101f5f` (Part B),
`5de07dd` (Part C), `c6ee3dd` (Parts D, E), `4669e20` (smoke script), plus the browser fixes committed with this report.

The session ran into its usage limit during the real-browser check. Everything is implemented and committed; the
browser check is about two thirds done. What is missing is listed under "Not done".

## Summary
- Part A1–A6, Part B (`productSku` on task summary and detail, left join in the same statement), Part C (realtime client),
  Part D (dashboard), Part E (Sync & Processing list, detail, retry — also on the order detail) are implemented with tests.
- `@microsoft/signalr` 10.0.11 (latest stable), behind `features/realtime/hub.ts`, loaded with a dynamic `import()` after
  sign-in. The library's own automatic reconnect is not used: it neither retries a failed first start nor tells a refused
  token (401) from an unreachable server, so `realtimeConnection.ts` owns the retries.
- RTK Query with invalidated entries without subscribers (verified in `rtk-query.modern.mjs`, `invalidationByTags`, and by
  a test): the entry is **removed** from the cache, not refetched; with subscribers it is refetched; an invalidation waits
  while any request is pending (`invalidationBehavior: 'delayed'`).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend) | 0 errors, 0 warnings |
| `dotnet test` (backend) | 746 passed, 0 failed (745 + 1 new) |
| `npm run lint`, `npx tsc -b --noEmit` | clean (at `c6ee3dd`) |
| `npm test` (frontend, at `c6ee3dd`) | 25 files, 243 passed, 0 failed |
| `npm run build` | ok |
| `scripts\smoke\task-016.ps1` (development environment, hosts started by the script) | PASSED: all 23 checks |
| `task-015.ps1` with `SMOKE_BASE_URL=http://localhost:5999` (A1) | fails at once, no API process started |

**Not re-run after the last browser fixes** (column widths in `DashboardPage.tsx` / `ProcessingPage.tsx`, the label
"failed publish attempts" in both translation files and its assertion in `processingTask.test.tsx`): only
`prettier --check` and, for the dashboard change, `tsc`. Run `npm test` and `npm run lint` once.

One earlier full run had a single failure in `products.test.tsx` ("dialog reopened before its close animation…",
timeout under load); it passed 3/3 alone and in the two later full runs.

A1 output:
```
== Task 015 - realtime hub and dashboard summary ==
Start the API of that environment first: this script starts an API only for the local default (SMOKE_BASE_URL not set).
unexpected error: API at http://localhost:5999 (SMOKE_BASE_URL) is not reachable   no error error  False
FAILED: 1 of 1 checks.
```
The Workers branch of A1 (API reachable, `SMOKE_BASE_URL` set, no Workers host) was not run.

### Bundle (`npm run measure`)
| State | First-load set raw / gzip | Main chunk raw |
| --- | --- | --- |
| before A5 | 984.04 kB / 321.91 kB (12 chunks) | 254.46 kB |
| after A5 | 960.38 kB / 316.55 kB | 251.98 kB |
| after Parts C–E | 967.48 kB / 317.23 kB (9 chunks) | 260.70 kB |

+7.10 kB after C–E: connection manager, hint mapping, slice, indicator with three icons, paths. SignalR chunk
(`esm-*.js`): 55.07 kB raw / 13.99 kB gzip, not in the first-load set. Translation chunks: vi 17.91 kB, en 14.82 kB,
loaded on demand. (gzip differs from report 014's 322.58 kB for the same build: another gzip implementation.)

## Real-browser check (dev server + real API + Workers, 1280×800, smoke Staff / smoke Manager, both locked again)
Done:
- Login page: only `vi.json` loaded, no SignalR code, no hub request. A tab started in English loaded only `en.json`.
- Hub: WebSockets through the Vite proxy — API log `HTTP GET /api/hubs/notifications responded 101`; no token in any
  console line and none in the API log.
- Two tabs, order → dashboard without reload: **1.53 s** with warm connection pools; 3.7 s and 4.3 s in the two runs
  before (see "Questions"). Order outcome by push: exactly two task requests, no polling.
- API stopped: "Reconnecting" at once, negotiate at +0 / +2 / +7 / +17 s, "Offline" after 17 s, next try 30 s later;
  API started: "Live" and exactly one summary refetch.
- Logout: connection closed (101 request finished), no further hub request.
- Failed stock-sync task (`SIM-PERM-…` on website): appeared in the list without reload (one refetch), channel filter,
  detail with attempts; Staff has no retry button; Manager retry → Pending after 0.3 s → Failed again after 3.9 s with a
  second attempt. Mapping deactivated afterwards.
- VI/EN: dashboard and processing list geometrically identical; indicator 148 px in every state.

Defects found in the browser and fixed: two dashboard column titles cut ("… (trong khoảng)"); SKU column of the task
list too narrow; "Số lần chạy" title cut; "publish attempts" is the number of *failed* attempts and was labelled wrongly.

## Not done
- Browser: order entry with Workers stopped for > 60 s ("still processing", "Check again") — covered by tests only.
- Browser: orders list at 1280 px (A4) — widths 160/210/160/150/200/160/100/88 are checked by a test only, not measured
  in vi and en.
- Browser: language-switch measurement of the task detail page.
- Full `npm test` / lint after the last fixes (see above).

## Deviations from the task
- A6 was committed before A5 (the script was needed for the "before" number).
- "Offline" is shown once the quick retries are used up (17 s after the loss), while the connection is still retried
  every 30 s; the task names four slice states but not when `disconnected` applies with "never gives up".
- Dashboard links for "failed / succeeded (in the range)" carry task type, status and `sortBy=updatedAt&sortDir=desc`, not
  the range: the list filters on creation time, the summary counts by update time.
- Relative times ("5 minutes ago") are computed against the summary's `generatedAt`, not the browser clock.
- `test/server.ts` got a default handler for `GET /api/dashboard/summary` (many existing tests pass the landing page);
  `uiSnapshot` finds the language switch by name; `task()` fixture got the three new fields. No existing assertion changed
  except two calls of `i18n.changeLanguage` → `changeLanguage` (A5).
- When the active language cannot be loaded at start, `main.tsx` shows one bilingual line instead of the application.

## Blocked / questions for Cowork
1. **Every new database connection costs about 2 s on this machine.** `localhost` resolves to `::1` first, PostgreSQL is
   published on `127.0.0.1` only; a connect to `[::1]:5432` is refused after 2075 ms (measured). The README tells users
   to set `Host=localhost`. It hit the summary refetch (2073 ms in the API log) and the Order Worker. Suggested:
   `Host=127.0.0.1` in the README and in the user-secrets (manual step), or publish the port on `::1` as well.
2. Latency budget without that penalty: outbox poll (≤ 1 s) + hint batch (250 ms) + client window (1 s). "~2 s" holds
   in the typical case only; is that acceptable, or should the first hint flush at once (leading + trailing)?
3. In push mode the lists are refetched twice per finished order (hints, then the 013 invalidation the task keeps).

## Manual steps for the user
- `cd frontend; npm ci` (new dependency), then `npm test` and `npm run lint` once (last fixes).
- Manual check: two tabs (dashboard, order entry), submit an order, the dashboard's order count moves without reload.
- Test data left in the development database: `SMOKE-016-*` and `SMOKE-016-UI-*` products, their customers, four locked
  `smoke-016-*` users, two inactive website mappings.

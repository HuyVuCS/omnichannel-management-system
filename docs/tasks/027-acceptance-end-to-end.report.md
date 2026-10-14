# Report 027 — Acceptance end to end (doc §16): follow-ups of 026c, acceptance script, browser e2e suite, CI job, acceptance matrix, final release

Status: Done — Parts A–F implemented and verified; **Part G released to production on 2026-10-14** (`3b27e91`, image
tag `3b27e91c6daa`, migration `LastStockSyncIndex`, public `/health` down ≈ 6 s) after the user's go-ahead ("Release
+ live checks"); live acceptance: `task-027.ps1 -Mode NoDrills -Live` 75 / 75, `npm run e2e` 8 / 8.
Commit: `de99516` (Part A), `3a2abe0` (Parts B–E), `3b27e91` (`Microsoft.*` 10.0.12 → 10.0.13, CVE-2026-70159 — the
released commit); this report, the live evidence and the optional resolver setting of the e2e suite in a commit of
their own.

## Summary
- **A:** a second confirmation of one upload that loses the race answers 200 with the product (A1); the summary's
  channels statement measured at +12–14 ms for a channel without a successful sync → partial index
  `IX_ProcessingTasks_ChannelId_UpdatedAt` (0.07–0.19 ms with it; A2); one global focus ring, plus two refinements found
  in the browser (A3); one `<h1>` per page with the old look (A4); the tunnel's 502 for a chunked oversize body accepted
  only against a public origin (A5).
- **B:** `scripts/smoke/task-027.ps1` walks the criteria (`-Mode Full` / `NoDrills`, `-Live`) and prints a step table
  and a criterion table; simulator prefix `SIM-DOWN-`; `invariants.sql` extended; `run.ps1 -Size small`, portable to
  pwsh on Linux; `docker/docker-compose.acceptance.yml`; the missing test "a failure inside the reserve transaction
  leaves neither movement nor outbox row" written.
- **C:** Playwright 1.63.0 (exact pin, Chromium) — 7 specs + every menu page as Admin, axe-core in the real browser
  with colour contrast on, the focus ring and `page-has-heading-one` checked. Found and fixed on the way: the primary
  button's hover colour (white text 4.16:1 → 5.8:1).
- **D:** CI job `acceptance` green on both pushes of this task (runs 38548985519, 38552842941; the second all jobs).
- **E:** `docs/acceptance.md` (criterion → backend tests, frontend tests, script steps, specs, baseline, runs; known
  limitations), README "Acceptance", runbook §17.
- **F:** backend 1942 / 1942, frontend 546 / 546 twice and slow, build, measure; throwaway `omni027`: Full 106 / 106
  (every criterion PASS), Playwright 5 × 8 / 8 with stack control, smoke 025 53 / 53, 026 11 / 11, 026c 27 / 27; three
  mutants killed; development migrated, NoDrills 75 / 75, e2e 8 / 8.
- Outside the plan, needed for D and decided by the user: Microsoft's advisory of 2026-10-13 (CVE-2026-70159, a
  denial of service in ASP.NET Core SignalR) failed CI's restore (NU1903, warnings are errors); the ten `Microsoft.*`
  10.0.12 packages were raised to 10.0.13 and the .NET base images pulled, so the release runs runtime 10.0.13.

## Files changed
Part A (`de99516`)
- `backend/src/Omnichannel.Infrastructure/Persistence/Products/ProductImageService.cs` — upload gone (no `HEAD`, copy
  false) → read the product again; `IsFinalOf(current, uploadId)` → 200 (`AlreadyConfirmedOrNotUploadedAsync`).
- `backend/src/Omnichannel.Tests/Integration/ProductImageHygieneTests.cs` — the race at both hold points (before the
  look, before the copy) with a new `BeforeHead` storage hook.
- `.../Configurations/ProcessingTaskConfiguration.cs`, migration `20261014062556_LastStockSyncIndex` (+ designer,
  snapshot) — `CreateIndex` only.
- `frontend/src/index.css` — the focus ring (`:focus-visible` 2 px `#0958d9`, offset 2 px).
- `frontend/src/shared/components/PageHeader.tsx`, `shared/hooks/useHeadingLook.ts` (new),
  `shared/components/ResultTitle.tsx` (new), `features/auth/LoginPage.tsx`, `pages/ForbiddenPage.tsx`,
  `pages/NotFoundPage.tsx`, `pages/RouteErrorPage.tsx`, the not-found states of `ChannelDetailPage.tsx`,
  `OrderDetailPage.tsx`, `ProcessingTaskPage.tsx` — one `<h1>` per page, the old look.
- `frontend/src/app/headings.test.tsx` (new: every route, 20 tests), `app/routeErrors.test.tsx` (the error page's `<h1>`).
- `scripts/smoke/_hardening.ps1` (`Test-TunnelledOrigin`), `docs/runbook.md` (§15 note).

Parts B–E (`3a2abe0`)
- `scripts/smoke/task-027.ps1` (new) — the acceptance script.
- `scripts/load/run.ps1` — `-Size small`, k6 by `docker create` + `cp` + `start -a` (no stdin redirection; runs under
  pwsh on Linux), expectations from the plan; `scripts/load/reserve.js` — `ITERATIONS`; `scripts/load/invariants.sql`
  — four new rows; `scripts/smoke/_common.ps1` — repository root joined with `/`.
- `backend/src/Omnichannel.Infrastructure/ExternalChannels/ChannelSimulationOptions.cs`, `SimulatedChannelStockClient.cs`
  — `SIM-DOWN-` (`channel_unavailable` on every attempt, SKU rules only); `Omnichannel.Tests/Workers/ChannelStockClientTests.cs`
  — off by default, a theory over attempts 1, 2, 5, 100, case and prefix.
- `backend/src/Omnichannel.Tests/Integration/Workers/OrderWorkerTests.cs` — `A_failure_inside_the_reserve_transaction_
  leaves_neither_movement_nor_outbox_row_and_the_retry_reserves_once` with an interceptor that fails the commit of the
  first transaction holding a `Reserve` movement.
- `docker/docker-compose.acceptance.yml` (new).
- `frontend/e2e/*.spec.ts` (8), `frontend/e2e/support/{env,api,fixtures,global-setup,global-teardown}.ts`,
  `frontend/playwright.config.ts`, `frontend/tsconfig.e2e.json` (new); `package.json` (`@playwright/test` 1.63.0,
  `npm run e2e`), `package-lock.json`, `tsconfig.json` (reference), `vite.config.ts` (Vitest excludes `e2e/`),
  `eslint.config.js` (Node rules for `e2e/`), `.prettierignore`, root `.gitignore` (report, results).
- `frontend/src/test/accessibilityException.ts` (new) — failing impacts and the one documented exception, shared by
  `src/test/accessibility.ts` (jsdom) and `e2e/support/fixtures.ts` (browser).
- `frontend/src/index.css` — the ring around AntD field boxes and inside header cells; `frontend/src/shared/components/layout.ts`
  — `colorPrimaryHover: '#1f5fd1'`.
- `.github/workflows/ci.yml` — job `acceptance`.
- `docs/acceptance.md` (new), `README.md` ("Acceptance"), `docs/runbook.md` (§17).

`3b27e91`: `backend/Directory.Packages.props` — the ten `Microsoft.*` packages 10.0.12 → 10.0.13.

This commit: `frontend/playwright.config.ts` (optional `E2E_HOST_RESOLVER_RULES`), `docs/acceptance.md` (runs),
`README.md`, `docs/runbook.md` (live run), this report.

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (backend, 10.0.13) | 0 errors, 0 warnings; `dotnet restore --force-evaluate` without an advisory |
| `dotnet test` (final, `3b27e91`) | **1942 / 1942** in one run (5 min 25 s) |
| `dotnet test` (after Part A, before the bump) | 1940 / 1941 — `ReportExportEndToEndTests.A_second_delivery_of_a_finished_export_...`: `BrokerUnreachableException` ("connection.start was never received") from the test container; the class alone 14 / 14 |
| `dotnet test --filter ProductImageHygieneTests` | 14 / 14 (two new) |
| `npm run typecheck` / `npm run lint` | 0 / 0 |
| `npx vitest run` × 2 (final) | 546 / 546, 546 / 546 |
| `npm run test:slow` | 546 / 546 |
| `npm run build` / `npm run measure` | ok; main chunk 169.06 kB raw / 54.47 kB gzip, total 970.89 / 318.47 kB |
| CI run 38548985519 (`3a2abe0`) | acceptance, frontend, images success; **backend failure at restore** (NU1903, CVE-2026-70159) |
| CI run 38552842941 (`3b27e91`) | **all six jobs success** (backend, frontend, acceptance, three images) |

Mutants (each applied, built, the targeted tests run, reverted, rebuilt with `--no-incremental`):
| Mutant | Result |
| --- | --- |
| A1: the re-read skipped (`IsFinalOf(...) && imageKey is null`) | 2 of 2 race tests red — killed |
| B4: `SIM-DOWN-` succeeds (`... && update.AttemptNo < 0`) | 4 of 4 theory rows red — killed |
| B3: one inventory row broken by hand on `omni027` (`TotalStock + 1`) | `task-027.ps1 -Mode Full` red: 3 of 106 (step 97, the new history invariant; 27 and 32, the invariants inside both load scenarios) — killed |

A2 measurement (postgres:17 with `auto_explain`, `scripts/perf/seed-task-026b.sql`, the API in environment Smoke, two
warm-up requests then three rounds of `GET /api/dashboard/summary`; the channels statement):
| | Round 1 | Round 2 | Round 3 | Plan |
| --- | --- | --- | --- | --- |
| No index, every channel with successes | 0.152 ms | 0.251 ms | 0.222 ms | backward on `IX_ProcessingTasks_Status_UpdatedAt`, 5 rows removed, 24 buffers |
| No index, + a channel without a successful sync | 12.415 ms | 12.979 ms | 13.807 ms | the same walk, 5,764 rows removed per channel on average, 23,352 buffers |
| With `LastStockSyncIndex`, every channel with successes | 0.101 ms | 0.070 ms | 0.066 ms | Index Only Scan Backward on the new index, 0 heap fetches |
| With `LastStockSyncIndex`, + the channel without | 0.187 ms | 0.082 ms | 0.193 ms | the same, 9 buffers |

+12.2 to +13.6 ms ≥ 2 ms → the index (the task's rule).

Throwaway stack `omni027` (production compose + acceptance override, own tag `drill-omni027`, ports 18080 / 18081,
edge subnet 172.30.251.x, two Workers; the frontend built for it and served by `vite preview` on 4174):
| Run | Result |
| --- | --- |
| `migrate` | 17 migrations, `LastStockSyncIndex` included (applied here before development and production) |
| `task-027.ps1 -Mode Full` (first) | 86 / 94 — my faults and one wrong assumption, see "Deviations" 8; script fixed, stack recreated |
| `task-027.ps1 -Mode Full` | **106 / 106**; criteria 1–14 PASS (14: the expired-token step skipped); `duplicates` small: 100 requests → 50 orders; `contention` small: 60 orders, stock 20 → 20 reserved, 40 refused; 12 invariant rows 0 |
| `npm run e2e` × 5, `E2E_STACK_CONTROL=1` | 8 / 8 in each run: 73 s, 133 s, 130 s, 131 s, 132 s (runs 2–5 waited out the login limit once) |
| `task-025.ps1` / `task-026.ps1` / `task-026c.ps1` | 53 / 53, 11 / 11, 27 / 27 (026c without the clean-up drill) |
| Teardown | `--profile migrate down -v` of `omni027` only, its three images and its env file removed |

Criterion table of the Full run:
```
 1 Orders from Website / POS / Digital Platform          02-05, 10-11, 16-17, 22                  PASS
 2 Orders normalised and put on RabbitMQ                 05-09, 11-15, 17-23                      PASS
 3 Duplicate orders not processed twice                  24-27, 101                               PASS
 4 Concurrent orders do not oversell                     32, 92-94, 97                            PASS
 5 Total / Reserved / Available stock correct            28-30, 32, 92-94, 97                     PASS
 6 Movement + outbox event in one transaction            30-32, 92, 95-96, 98-100, 104            PASS
 7 Stock sync with retry and DLQ                         33-38, 43-47, 102-103                    PASS
 8 Near-real-time dashboard                              48-50                                    PASS
 9 Trace order -> task, worker, instance, attempt, error 06, 12, 18, 34, 39-42, 51-53             PASS
10 Low-stock alert by threshold                          54-58                                    PASS
11 Inventory and processing history                      59-61                                    PASS
12 Excel export                                          62-65, 91                                PASS
13 Product images in S3-compatible storage               66-69                                    PASS
14 API authentication / authorization                    01, 44, 56-57, 62, 70-90                 PASS (1 skipped)
15 Tests for the important flows                                                                  see docs/acceptance.md
PASSED: all 106 checks.
```

Role matrix as printed (Full run): Admin create user 201, list users 200; Staff order intake website / pos /
digital-platform 202, create product 403, list users 403, pay 403, acknowledge 403, export 403, system events 403,
retry 403; Manager pay 200, acknowledge 200, export 202, retry 202, list users 403 — every cell as the code defines it.

## Deviations from the task
1. **A3 — two more rules than "one global rule".** Checked in the browser (screenshots of the focused elements): the
   ring of an input inside one of AntD's field boxes was drawn inside the box's border (a box in a box), and the ring
   of a header cell was painted over by its neighbours (two edges visible). The ring is drawn around the field box
   (`:is(.ant-input-affix-wrapper, .ant-select, .ant-input-number, .ant-picker):has(input:focus-visible)`, the inner
   input none) and inside a header cell (`th:focus-visible { outline-offset: -2px }`). Same colour and width everywhere;
   `colorPrimaryBorder` untouched. The e2e focus check reads the ring where it is drawn.
2. **`colorPrimaryHover` set to `#1f5fd1`** — axe in the browser (C4) flagged the hovered primary button (white on
   AntD's derived `#2e7ae6`, 4.16:1). A local defect, fixed; the hover stays a slightly lighter blue (5.8:1).
3. **Trace spec uses `SIM-TEMP-`, not `SIM-DOWN-`.** `SIM-TEMP-` gives the failed attempt with its error code on every
   stack whose SKU rules are on, also with production retry delays (development), where `SIM-DOWN-` would take about
   12 minutes to reach its last attempt. The `SIM-DOWN-` trace (5 attempts, errors, `processing.failed` by correlation
   id) is in `task-027.ps1` B4/B5 (`Full`). The Logs page is filtered by the correlation id of an order that could not
   be reserved (its `order.insufficient_stock` event); the `SIM-TEMP-` flow writes no system event.
4. **The trace of B5 in `NoDrills`** is an order that cannot be reserved (task with its attempt, the event by
   correlation id) — the only failing business flow without a drill.
5. **`SIM-DOWN-` attempts are "temporary errors", not "each `channel_unavailable`".** With 1 s / 2 s retry delays the
   five attempts fall into one 30 s sampling window of the channel's circuit breaker, which opens after the third or
   fourth: the remaining attempts fail with `channel_circuit_open` (doc §13 behaviour). The drill asserts the first is
   `channel_unavailable` and all five are temporary, and waits the break (31 s) out before the correction.
6. **The drill retries every failed sync task of the pair**, not two: the import of the product's stock can become a
   third sync task when the Stock Sync Worker consumes it after the mapping exists (seen once). The DLQ count is
   polled (dead-lettering lands moments after the task is marked failed) and must be "before + failed tasks".
7. **`E2E_HOST_RESOLVER_RULES` (optional) in `playwright.config.ts`.** The first live e2e run failed at every sign-in
   ("Cannot reach the server"): on this machine the API host resolves to its tailnet address (100.93.210.19, also when
   8.8.8.8 is asked — Tailscale answers locally), and Chromium blocks a public page's requests to it (Local Network
   Access). The public answer (DNS over HTTPS: 103.84.155.217 / .153, the Funnel relays) was mapped for the browser
   only; the rerun went through Funnel like any visitor: 8 / 8. Not a product defect.
8. **The first Full run on `omni027` was red (86 / 94)** and is reported as such: an extra array wrapper in my
   `Invoke-Docker` helper made every `psql` read return `System.Object[]` (steps 9, 15, 21, 31, 69 and the invariants),
   and deviations 5 and 6 above. After the fixes, on a fresh stack: 106 / 106. Also fixed after the first development
   run (74 / 75): Windows PowerShell gives a single object no `Count` (step 37), and the criterion table was indexed
   by position.
9. **Expired token skipped** (B8 allows it): making one needs the signing key. Covered by JwtBearer's lifetime check
   (30 s clock skew — not zero, corrected in `docs/acceptance.md`) and `JwtAccessTokenIssuerTests.ExpiresAt_follows_configured_lifetime`.
10. **Vietnamese texts of the criteria are this repository's rendering**: the architecture document is not in the
    repository (it is in the Cowork project); `docs/acceptance.md` says so.
11. **The login limit (10 / min per address) is waited out** in the e2e suite (`Api.signIn`, the UI sign-in) once
    for 61 s, at most twice — the one fixed wait of the suite, the same rule as `_hardening.ps1` (accepted in 026c). No
    other sleep; all other waits are web-first assertions.
12. **`bypassCSP: true`** in the Playwright config: axe-core is injected as a script element and the production
    build's CSP allows only its own scripts. No spec checks the CSP (task 026 did, in two browser sessions).
13. **No trace, no video** in Playwright: a trace records request headers (the bearer token) and cannot leave them out;
    passwords are typed without a `fill` step (whose title would carry them). Screenshots only on failure.
14. **CVE-2026-70159** (outside the plan): after the first push CI's backend restore failed on a NuGet advisory
    published the day before; the user chose "bump + pull images": ten packages 10.0.12 → 10.0.13 (one servicing
    release), `mcr.microsoft.com/dotnet/{sdk,aspnet,runtime,runtime-deps}:10.0` pulled (runtime 10.0.13, checked in the
    built images). A third-party service image (pinned by digest) was not touched.
15. **The webhook of B1 and the load scenarios run only in `Full`**; in `NoDrills` they are reported as skipped steps.

## Blocked / questions for Cowork
None blocking. For the review:
1. **Focus of checkboxes, radios and switches**: AntD draws it on a sibling element (`.ant-checkbox-inner` etc.) with
   its own low-contrast colour; the global rule cannot reach it, and the hidden input's own ring is invisible
   (opacity 0). Not in A3's list; a follow-up if wanted.
2. **The migrate container prints `Error: libgssapi_krb5.so.2: cannot open shared object file`** (seen in the release;
   the migration then applied and the container exited 0). Npgsql probes Kerberos support at start; the
   `runtime-deps` image has no GSSAPI library. Harmless for password authentication; whether it was printed before
   the base images were pulled is not known (earlier release logs were not kept).
3. **Development machine runtime is still 10.0.12** (the API and Workers run there with the 10.0.13 packages); the
   images and CI use 10.0.13. Updating the local .NET runtime is the user's.
4. Live demo data left in production (as agreed): `ACC027-*` / `E2E027-*` users (locked), products, customers, orders,
   one image per run, exports; acceptance mappings deactivated.

## Development steps (applied)
| Step | Result |
| --- | --- |
| Migrations as the migrator (`OMNICHANNEL_MIGRATOR_CONNECTION`, user-level, read in-process, never printed): `dotnet ef migrations list` | pending: exactly `20261014062556_LastStockSyncIndex` (applied on `omni027` first) |
| `dotnet ef database update` | "Applying migration '20261014062556_LastStockSyncIndex'. Done."; list afterwards: 0 pending |
| API, Workers and dev server started from the current build | API `/health` 200, dev server 200 |
| `task-027.ps1 -Mode NoDrills` | **75 / 75** (criterion 4 Full only; webhook, load scenarios, DLQ drill skipped by design; SKU rules on: the `SIM-TEMP-` retry shown) |
| `npm run e2e` (dev server) | **8 / 8** |
| Hosts stopped | API, Workers, dev server |

No compose service of development changed; no storage key changed.

## Production steps (applied)
After the user's go-ahead, runbook §4 / §5:
| Step | Command (no secret) | Result |
| --- | --- | --- |
| 0 | `git log origin/production..origin/master`, diff of migrations / compose / Caddyfile | 3 task commits (+ docs); migration `LastStockSyncIndex` only; compose file and Caddyfile unchanged (no service definition changed) |
| §5 | `Get-AppliedMigrations` (read-only) | 16 applied, last `TaskListOrderIndex`; pending exactly `LastStockSyncIndex` (additive) |
| 1 | `set-image-tag.ps1 -EnvFile docker\.env.prod` | `IMAGE_TAG: 9712cf1f7544 -> 3b27e91c6daa` (rollback tag `9712cf1f7544`) |
| 2 | `docker compose @stack --profile migrate build` | exit 0; images `:3b27e91c6daa`, runtime 10.0.13 (`dotnet --list-runtimes` in the API and Workers images) |
| 3 | `docker compose @stack --profile migrate run --rm migrate` | "Applying migration '20261014062556_LastStockSyncIndex'. Done." (plus the `libgssapi` message, question 2), exit 0, 4 s |
| 4 | `docker compose @stack up -d --wait`, `ps` | exit 0; API (healthy) and two Workers on `3b27e91c6daa`, third-party services untouched; public `/health` probed every second: 3 failures, 18:10:59–18:11:04 → **≈ 6 s** |
| 5 | `git push origin <commit>:refs/heads/production` | `9712cf1..3b27e91`; Vercel served the new bundle after ≈ 50 s |
| 6 | `task-025b.ps1` (read-only) | **28 / 28** (the chunked step got the readable 413 through the tunnel) |
| 6 | `task-026.ps1` (read-only) | **11 / 11** |
| G3 | `task-027.ps1 -Mode NoDrills -Live` (first attempt) | stopped at its first step, nothing written: `docker\.env.prod` holds no Admin password since go-live |
| G3 | the same, the Admin from temporary user-level variables the user set (read in-process, never printed) | **75 / 75**; SKU rules off → the retry part reported skipped, criterion 4 Full only |
| G3 | `npm run e2e` against the Vercel origin and the public API (first attempt) | 0 / 8: every browser sign-in "Cannot reach the server" (deviation 7); setup data created, teardown locked / deactivated it |
| G3 | the same with `E2E_HOST_RESOLVER_RULES` mapping the API host to a public Funnel address | **8 / 8** |
| 7 | `docker image rm` of `:a980747a49fc` | removed; kept `3b27e91c6daa` (running) and `9712cf1f7544` (rollback) |

Nothing else on production: no load scenario, no drill, no container stop, no `psql` write.

## Manual steps for the user
- Remove the two temporary variables if not done yet:
  `[Environment]::SetEnvironmentVariable('OMNICHANNEL_LIVE_ADMIN_EMAIL', $null, 'User'); [Environment]::SetEnvironmentVariable('OMNICHANNEL_LIVE_ADMIN_PASSWORD', $null, 'User')`
- Optional: update this machine's .NET 10 runtime to 10.0.13 (question 3).

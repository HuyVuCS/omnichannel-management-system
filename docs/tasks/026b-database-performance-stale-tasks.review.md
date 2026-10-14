# Review 026b — Release of 026; error answers cross-origin; image digests; database and performance; stale tasks; load test

Reviewer: Cowork, 2026-10-13
Verdict: **Accepted.** Both releases are done (026 as `0d7abc5`, 026b as `a980747`), production runs the hardened
stack with the stale-task sweep and the two indexes; no P0 / P1. Task 026 is closed with it (its report completed in
`a1a4263`); the user's live browser check (F6) passed on 2026-10-13 without CSP messages. All eleven deviations accepted (11 with a
reminder). Questions: 1 → **(b) proxy limit above the API's**; 2 → accepted as designed; 3 → **026c Part A**;
4 → the user's call, recommendation below, plus the red CI run into 026c A; 5 → no drop.

## Checked
- Report 026b and the completed report 026; commits `66b6f86` … `a980747`, `2b8319c`.
- Read in full: `StaleTaskSweep.cs`; the diffs of `ProcessingAttemptStore.cs` (`StartAttemptAsync` message guard,
  `FailStaleAsync` / shared `FailAsync`) and `Program.cs` (order); `UserEmailLookup.cs`.
- Checked by Cowork against the code:
  - **Sweep:** candidates read without a lock, each failed in its own transaction that locks the row and re-checks
    status **and** age (`UpdatedAt ?? CreatedAt >= since` → left alone) — a task a worker touched meanwhile is never
    failed. One run at a time by a session advisory lock on an **unpooled** connection (closing it surely ends the
    session — right; a pooled connection would keep the lock alive). Failure goes through the workers' own final-failure
    path, so alert, hint and counters are the usual ones; one event per run with a capped id list. A failing task does
    not stop the run.
  - **Old-message guard** in `StartAttemptAsync` under the row lock: a message whose id is not the task's current
    `MessageId` starts no attempt and is acknowledged. Placing it in the store, not the handlers (deviation 5), is the
    only race-free place. `MessageId` null keeps the old behaviour. Retry/DLQ suites green in CI, so consumer retries
    keep their message id.
  - **Validation** (deviation 3): `StaleAfter ≥ 2 × max(longest consumer retry delay, sum of the publisher's
    backoffs)` — the sum is the right measure (the task is healthy while the publisher still tries). Checked even with
    the sweep off. Good.
  - **Pipeline order** after A1: … HTTPS redirect → CORS → body limit → rate limiter → auth. Correct and explained.
  - **E-mail lookup:** one `WHERE Id = ANY` per page; only the canonical `D` text counts as a user id — exactly what
    `Id::text =` matched, so the JSON is unchanged (tests with user, webhook, system and deleted user).
  - **Indexes:** two, each with a measured plan; the task's candidates that did not help were left out (deviation 2 —
    exactly the rule). `("Status", "UpdatedAt")` serving both arms of the summary's `OR`, the sweep and the status
    counts is a better choice than the partial index.
- **Verification** (not re-run by Cowork): backend 1872 tests — CI green on `3ecd073` and `a980747`, local runs with
  2–4 connection timeouts of varying tests (also on `0d7abc5`, see q4); negative control of A1 (4/7 fail on the old
  code); 5 mutants of C (4 killed, the fifth now pinned); frontend 524/524 ×2 + slow; `omni026b`: smoke 025 54/54,
  026 9/9, 026b 29/29 incl. the stale drill; load test with all invariants; development migrated as the migrator;
  production: 42 s downtime, migration in 9 s, sweep's first run failed nothing.

## Load-test baseline (recorded in the log)
Contention (300 orders, one SKU, stock 100): exactly 100 reserved / 200 rejected, p95 intake 58 ms, all finished in
≈ 3.1 s (serialised on one inventory row by design, ≈ 88 tasks/s). Spread (2,001 orders at 20/s, 50 SKUs): intake p95
22 ms, intake → finished p95 612 ms, Workers at ≈ 27 % CPU. Duplicates (200 requests, 100 ids): 100 orders. Zero
invariant violations, nothing unfinished, empty queues and DLQs. This is the evidence for the doc §16 criteria
"no oversell" and "duplicates not processed twice" under load.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | Chunked 413 tested with a start-up filter of the test host; the real Kestrel in the smoke | Accepted. |
| 2 | One `(Status, UpdatedAt)` index; two candidates not added | Accepted — measured. |
| 3 | Stricter `StaleAfter` validation (sum of the publisher's backoffs) | Accepted — correct. |
| 4 | First sweep run after one full `Interval`; `Interval` ≥ 1 s | Accepted — gives consumers time after an outage. |
| 5 | Old-message guard in the store, under the row lock | Accepted — race-free. |
| 6 | "Tasks of this product" link on the task detail | Accepted — there is no product detail page. |
| 7 | The first release with digests recreated the third-party containers once | Accepted. |
| 8 | Spread sends 2,001 | Accepted. |
| 9 | Smoke through a proxy asserts only the 413 status | Accepted until question 1 is done (026c A1). |
| 10 | Sweep tests share a database per class | Accepted. |
| 11 | `docker desktop restart` before Part 0; 30 oversize requests to production's local proxy port during analysis | Accepted. The restart was necessary and harmless. The 30 requests were read-only, but **requests to production outside a part's steps need a sentence to the user first** — say it, then do it. |

## Questions of the report
1. **Proxy 413 → option (b): the proxy's limit clearly above the API's, so the API decides.** Caddy's `max_size`
   only fails when the body is *read* past the limit, so with equal limits the outcome is a race between the API's
   early answer and the proxy's read error — as measured. With the proxy at **1 MiB** (8 × the API's 128 KiB) every
   body up to 1 MiB reaches the API, which answers a readable 413 (announced length: before reading; chunked: Kestrel
   cuts at 128 KiB); above 1 MiB the proxy still stops it as a backstop, bare 413 accepted there. Option (a) would
   configure the frontend origin and a problem body in a second place; (c) leaves a flaky live check. The backend test
   that compares the two numbers becomes "proxy limit = 8 × API limit" (a named constant / comment, not a free number);
   `_hardening.ps1` asserts the readable 413 for 128 KiB + 1 and only "refused" for > 1 MiB. → **026c A1**.
2. **Sweep after a long time without Workers → accepted as designed.** The first run waits one `Interval` (5 min);
   at the measured ≈ 80 tasks/s the consumers drain far more than any realistic backlog in that time. If a task is
   still failed, the outcome is safe: the waiting message is dropped by the guard and the operator retries. No broker
   management API in the Workers. Runbook §8 says it; nothing to add.
3. **Two query shapes → 026c A2.** (b) matters: the task list computes "last attempt" with `ROW_NUMBER()` over all
   attempts — cost grows with the attempts table (≈ 35 ms of 47 at 25k attempts). Rewrite as a per-row lateral lookup
   using the unique `(TaskId, AttemptNo)` index; measure the `(CreatedAt, TaskId)` index for the unfiltered sort. (a)
   `lastSucceededAt` as its own small statement only if the measured gain is real (5 ms today) — otherwise record and
   leave it.
4. **Local suite timeouts → the user's call; recommendation: restart WSL at a quiet moment.** `wsl --shutdown`, then
   start Docker Desktop: production is down about a minute and comes back by itself (`restart: unless-stopped`, as the
   reboot test showed). CI stays the reference meanwhile. The **red CI run of `294cb91`** must be explained, not
   ignored: **026c A3** (read the log; a flaky test is fixed by the rules of log "Task 017 result", not re-run until green).
5. **Unused indexes → no drop.** The tables are small, write cost is not measurable, and three of the "unused" belong
   to pages the measurement did not exercise. Recorded; revisit when an insert-heavy path shows index maintenance cost.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | Live check `task-025b.ps1` steps 21–23 are a race (q1). | 026c A1. |
| P3 | Task list "last attempt" scales with all attempts (q3b). | 026c A2. |
| P3 | Red CI run on `294cb91` not explained (q4). | 026c A3. |
| P3 | Production was down ≈ 40 min before Part 0 because Windows upgraded WSL and Docker Desktop did not recover by itself. | Runbook "Incidents": add "after a WSL upgrade: `wsl --status`, then `docker desktop restart`" (026c A4). Known limitation (uptime = the machine). |
| — | The user's live browser check of 026 (F6). | **Done by the user 2026-10-13: no Content Security Policy message in the console.** |

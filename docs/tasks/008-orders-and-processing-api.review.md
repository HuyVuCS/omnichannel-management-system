# Review 008 — Orders/processing read API, manual retry, audit events

Reviewer: Cowork, 2026-09-30
Verdict: **Accepted** (manual acceptance check pending — see below)

## Checked
- Report, commits `a1b588f` (Part A) and `886d31b`; API table, manual-retry transaction, query-shape table, migration SQL,
  mutation results.
- Manual retry: row lock → status check → copy of the original outbox row → `RequeueForManualRetry` (budget start = highest attempt
  number, attempts kept) → `processing.retry_requested` with the JWT subject → commit. Concurrent retries serialise on the lock
  (proven: without `FOR UPDATE` both were accepted, 3/3 runs).
- Fresh attempt budget proven end to end (attempts 1–3 → Failed → retry → 4–6 → Failed; mutation using `AttemptNo` fails the test).
- Task search uses one `EXISTS` so combined attempt filters must match the **same** attempt — correct reading of doc §5.3.
- No N+1: order detail 1 statement, lists COUNT + page, task detail 2 statements (measured by `QueryShapeTests`).
- Part A: unroutable retry copy redelivered 3–6 times per 10 s instead of 1,172; one Error log per 30 s.
- 366/366 tests (250 unit, 116 integration), 3 runs green.

## Deviations — accepted
All seven. Descending default for time sorts and SKU order for lines are sensible; `occurredAt` = retry time is correct for a new message.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | The report's manual check repeats "stop PostgreSQL right after submitting", which is a race (the worker finishes in < 1 s). | Use the table-lock procedure from the task-007 check instead (chat guidance) |
| P3 | Running API/Workers lock `bin/Debug`; Claude Code worked around it with `--artifacts-path`. | User: stop API/Workers before `/next-task`, restart after |
| P3 | DLQ copies accumulate; no system-event read endpoint; outbox retention must keep rows of `Failed` tasks. | Already planned (017/018, later retention) |

## Manual acceptance check
Pending the user's confirmation: order detail shows attempt #1 `timeout` + #2 `Succeeded` with worker name/instance; manual retry of a
`Failed` task → 202 → message `Published` → task processed again.

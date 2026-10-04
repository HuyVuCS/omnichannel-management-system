# Review 018b — Logs/Audit: `GET /api/system-events` + Logs/Audit page; Part A

Reviewer: Cowork, 2026-10-03
Verdict: **Accepted** — no P0–P2 finding. Two P3 follow-ups (report questions 3 and 5) go into 019 Part A.

## Checked
- Report and commits `b84ef98` (A1, 018 report), `1267307` (A2), `0f8259c` (Part B), `0570c8b` (Part C, smoke, README),
  `5b8fab0` (report). 54 files, +4458 / −29. No migration, no new dependency.
- Read in full: `SystemEventsController.cs`, `SystemEventContracts.cs`, `SystemEventDetails.cs`,
  `SystemEventListQueryValidator.cs`, `SystemEventService.cs`, `pendingRequests.ts`, the diffs of `setup.ts` and
  `interactions.ts`, `logs/range.ts`, `systemEventsApi.ts`, the first part of `LogsPage.tsx`, README "System events
  (Logs/Audit)", and the `renderLive` helper and the three "outcome by push" list-count tests of `orderEntry.test.tsx`.
- Checked by Cowork against the code:
  - **Role rule:** the controller passes `User.IsInRole(Admin)`; the service applies `NOT LIKE 'user.%'` before every
    other filter, so it holds for the page, the count and an explicit `eventType=user.*` (empty page). `ICurrentUser`
    untouched, as asked. A demoted Admin loses `user.*` with the token's security stamp (`UserSecurityStateProvider`,
    ≤ 30 s cache), not after the 60 min token lifetime.
  - **Allowlist:** one map in Application keyed by the existing constants (`OutboxEvents.PublishFailed` added so the
    map need not reference Workers — deviation 4, correct). Fields checked against the writers: `processing.failed`
    writes the attempt's sanitised `errorMessage` (`ProcessingErrorClassifier.Sanitize`), `processing.retry_requested`
    writes `requestedBy` as a user id. `reason` correctly left out for both `…message_rejected` events (parser exception
    text, values of the rejected message). Unknown type / non-object payload → `details: null`.
  - **Statements:** count, page, and one `WHERE Id IN (…)` only when the page names users; the e-mails are resolved from
    the *filtered* details, so a hidden field can never drive a lookup. The mutant "users looked up one by one" first
    survived because the counter missed synchronous commands — found and fixed by the mutation run, a good catch.
  - **Guard:** counts from the `fetch` call until its promise settles, takes the list as the first statement of
    `afterEach`, throws after the teardown (the next test still starts clean). One opt-out, unused.
  - **Window:** `readLogsRange` → custom range > preset > `since`; without any of them the page writes `since = now −
    24 h` and sends nothing until it is in the address.
- **Verification** (not re-run by Cowork): backend 890/890, 0 warnings, 19/19 compiling mutants killed; frontend 373
  tests, 5 of 5 full runs green with the guard (281 / 269 / 243 / 234 / 241 s); the guard's seven-case self-check; the
  full suite under a temporary 120 ms answer delay (66 → 3 failures, question 3); `EXPLAIN (ANALYZE)` at 60,000 rows
  (no sequential scan for a page query); smoke 37/37 on development; production-build browser check at 1280 px in vi /
  en, trace by correlation id, Admin / Manager / Staff views. First-load set +0.73 kB raw; `LogsPage` lazy (11.8 kB).
- **Doc deviation 18 confirmed** (`GET /api/system-events`).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | Guard at the `fetch` call, not MSW's life-cycle events | Accepted (question 2). Measured reason: MSW reports a request one macrotask late, which attributes a last-tick request to the next test. |
| 2 | Flagged tests found with a temporary 120 ms delay | Accepted, and the delay becomes a committed opt-in switch (019 A1) so the check can be repeated. |
| 3 | `reason` not returned | Accepted; a safe `reasonCode` is added in 019 A2 (question 5). |
| 4 | `OutboxEvents.PublishFailed` in Application | Accepted. |
| 5 | Open-ended `?since=` window | Accepted (question 1). |
| 6 | Range validation as the alerts list (92 days) | Accepted — the stricter of the two existing rules. |
| 7 | Presets from the dashboard's constants; `presetLocalDays` extracted | Accepted. |
| 8 | A Manager's `user.*` type in the address is not sent | Accepted; the server's answer would be the same empty page. |
| 9 | Event-type select without virtual scrolling (14 options) | Accepted. |
| 10 | Trace link keeps window, sort direction and page size | Accepted. |
| 11 | Statement-count test in the Workers collection | Accepted. |
| 12 | `level`, `eventType` in `FieldNames` | Accepted. |
| 13 | `EXPLAIN` on hand-written equivalent SQL, throwaway database | Accepted. |
| 14 | Column widths after the browser check; `ID_FILTER_WIDTH` | Accepted. |
| 15 | A1: backend sources of 018b written during the five runs (no build/test) | Accepted; disclosed in the 018 report. |
| 16 | Browser check by DOM clicks and session injection; no keyboard pass | Accepted; the keyboard pass stays an optional manual step for the user. |

## Questions of the report
1. **Default window.** Accept the open-ended `since`. A fixed end would hide every event newer than the first opening
   and make "Refresh" useless on an audit page. Known effect: while new events arrive, page 2+ shifts by the number of
   new rows — the same as every other list here (offset paging). The open end also means the 92-day rule does not
   apply to a `since` far in the past (P3, below).
2. **Guard at the `fetch` boundary.** Accept. It covers MSW's window and attributes each request to the test that sent
   it; the alternative would need the same wrapper anyway.
3. **Three order-entry tests under latency.** Cause (Cowork, from the code): `renderLive` returns after the indicator
   reads "Trực tiếp" and one flush window, but **does not wait for the refetch the connect resync starts** (`RESYNC_TAGS`
   after every (re)connect, decision of 016). With slow answers that refetch is still in flight when `watchLists`
   starts counting, so it is counted (3 instead of 2, 2 instead of 1). Application behaviour is correct — a resync after
   the first connect is intended (hints sent before the connection would otherwise be lost). Test defect → **019 A1**:
   `renderLive` ends settled; the full suite must be green with the delay switch on.
4. **No system event for a successful order.** **No new event type.** `SystemEventLog` stays the record of exceptions
   and of manual/audited actions; the normal flow of an order is traced by its processing task and attempts (order
   detail, Sync & Processing page — doc §5.3), which already show worker, instance and attempt. An event per order
   would add a statement to the reserve transaction (8 today) for information that exists. Revisit when operators need a
   single timeline of a normal order (then rather a "timeline" view joining tasks and events than a new event).
5. **`reason` of rejected messages.** Yes, but as a **code**, not the text: the two writers add `reasonCode` (one
   constant per rejection path), the allowlist returns it, the page shows its label. The text stays stored and stays
   hidden. → **019 A2**.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | Question 3: `renderLive` does not wait for the connect resync's refetch; list counts depend on latency. | 019 A1. |
| P3 | Question 5: rejected messages show no reason on the page. | 019 A2 (`reasonCode`). |
| P3 | An open-ended `from` (no `to`) is not bounded by the 92-day rule: a `since` years back counts most of the table. Manager/Admin only; the page never sends one. | Accepted; 026 (already lists `COUNT` over wide ranges). |
| P3 | `EVENT_TYPES` in the client repeats the keys of the server's allowlist; a new server type appears with its raw name and is not in the filter until the client lists it. | Accepted — README "Adding an event type" step 5 says so. |

## Assessment
The security-relevant parts are where they belong and are tested from both sides: the role rule is one `WHERE` on the
server that no filter can undo, and payloads leave the API only through an allowlist checked against every writer (with
a unit test that fails for a new constant without an entry). The A2 guard turns the gotcha that caused three red series
into an enforced rule, and the 120 ms delay run is the right way to find timing-dependent tests deterministically. The
measurement behind deviation 1 (MSW's 19 ms gap) is exactly the evidence a deviation should carry.

## Manual check
Optional for the user (3 minutes): the keyboard pass on **Nhật ký / kiểm toán** described in the report.

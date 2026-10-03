# Task 018 — Frontend: Alerts page, alert tiles on the dashboard + Part A (form-dialog reset, follow-ups)

Status: Open
Written by: Cowork, 2026-10-03
Depends on: 017 (accepted)

## Context
Doc §3.2 (page "Cảnh báo"), §12 (UI events `low_stock.created`, alert push), §16 "Low-stock alert hoạt động theo
threshold". Task 017 delivered the alert API (README "Alerts": types, keys, lifecycle, endpoints, hints, summary
section). **Roadmap change (user, 2026-10-03):** the planned 018 is split — **018** = Alerts page + dashboard alert tiles
+ Part A; **018b** = Logs/Audit (new backend endpoint + page). Do not build anything of the Logs page here; its menu
item stays a placeholder.

Read first: implementation log "Task 017 plan", "Task 017 result" (decisions of review 017), "Task 018 plan",
"Task 016 plan/result" (realtime client, event → tag map, dashboard), "Task 016b result" (form dialogs, production-build
browser checks), "Task 012 plan/result" (cache rules, confirmations); reviews 016b and 017; frontend
`shared/hooks/useResetFormOnOpen.ts` and its seven users, `features/products/products.test.tsx` (test "dialog reopened
before its close animation has finished"), `features/realtime/hintTags.ts`, `features/dashboard/*`,
`features/processing/ProcessingPage.tsx` (filters, local-day range, address state), `features/processing/components/*`
(`TaskStatusTag`, `RetryTaskButton` confirmation pattern), `features/products/components/ProductSelect.tsx`,
`shared/hooks/useListQueryState.ts`, `features/*/paths.ts`, `app/navigation.tsx`; backend `AlertContracts.cs`,
`AlertsController.cs`, `ChannelService.UpdateProductAsync`, `AlertStore.ResolveAsync`; `scripts/smoke/_common.ps1`.

If the implementation needs a decision that is not settled here or in the log (an alert detail route, a header badge or
toast, a new alert endpoint or field, an alert-related change in the hint contract, manual resolve), stop and report
(`Blocked`).

## Part A — Follow-ups (separate commits, A1 first)

### A1. Form dialogs: reset from inside the form (P1, review 017 question 1)
Defect: a dialog reopened for record B while its close animation for record A still runs can show A's values under B's
id (the dialog renders its content one commit after its parent; `useResetFormOnOpen` resets in the parent's effect,
against the previous `initialValues`). Saving writes A's values onto B.
- A shared component rendered **inside** each dialog's `<Form>` (it reads the instance with `Form.useFormInstance()`)
  resets the form in its effect whenever the dialog is opened. Because it renders in the same commit as the `Form` that
  received the new `initialValues`, the reset always uses them. Remove `useResetFormOnOpen` (file and its doc comment;
  move what is still true into the new component's comment) from all seven dialogs: `ChannelFormModal`,
  `ChannelProductFormModal`, `CustomerFormModal`, `ProductFormModal`, `ChangeRoleModal`, `CreateUserModal`,
  `ResetPasswordModal`. `clearOnDestroy` stays on every dialog `Form`.
- "Opened" must be detected on **every** open, also when the dialog is reopened for the **same** record before its close
  animation has finished (the memoised content may never render the closed state, so a dependency on the record value
  alone is not enough). How the open is identified (e.g. an open counter kept by the dialog's owner) is your choice;
  state it in the report.
- The "is the form mounted" heuristic (`getFieldsError().length > 0`) should no longer be needed; if it still is, say why.
- Tests:
  1. The existing 016b test ("dialog reopened before its close animation has finished …") unchanged in meaning.
     Measure it before and after: with the old hook, run it **30 times** (vitest `repeats` or a loop; temporary, not
     committed) and report the failures; with the fix, **50 times**, 0 failures expected. Same for the probe the 017
     report describes if you rebuild it.
  2. New: edit record A, change a field, Cancel, reopen **A** while closing → A's stored values, not the edit.
  3. New: the same "reopen for B while closing" scenario on one other edit dialog (`CustomerFormModal` or
     `ChannelProductFormModal`).
  4. Every existing dialog test green; any change to one is a deviation with the reason.
- Production-build browser check (`npm run build` + `vite preview`): products edit dialog — open A, Cancel, open B
  immediately (keyboard), 10 times; the same-record case 5 times; customers dialog once each. Report what you saw.

### A2. `currentData` on detail pages bound to a route parameter (P3, review 016b question 3)
`OrderDetailPage`, `ProcessingTaskPage`, `ChannelDetailPage` must never show the previous record while the next loads
when the route parameter changes on a mounted page: use `currentData` (or remount by key — pick one, the same on all
three, say which). One test per page: navigate from record 1 to record 2 with the second response delayed → record 1's
values are gone while loading.

### A3. Stale API errors on optional customer fields (P3, review 016b question 4)
Test first: a 400 on an optional field of `CustomerFormModal` (e.g. `email` or `phone`), then the user clears the field
and submits again → the old error must be gone. If it stays, clear the API errors of those fields before each submit,
the same mechanism as the mapping dialog (016b deviation 7). Report which case it was.

### A4. Products table at 1280 px (P3, review 016b findings)
No horizontal scroll at 1280 px in vi and en, same treatment and column-width test as the orders table (016 A4).

### A5. Smoke table never drops a column (P3, review 017 question 5)
`Complete-Smoke` in `_common.ps1`: `OK` is the **first** column; long values are wrapped or shortened inside their
column, never cut the table (`Format-Table -Wrap` / explicit widths — your choice). Run `_assert-selftest.ps1` and one
older script that has a long value; paste the table.

### A6. Deactivating a mapping resolves its stock-sync alert (backend, review 017 question 3 — user's decision)
- `ChannelService.UpdateProductAsync`: when the mapping goes from `Active` to `Inactive`, resolve the
  `StockSyncFailed` alert of its (channel, product) with `AlertStore.ResolveAsync` in the **same transaction** as the
  mapping change (the method gets an explicit transaction; the existing 23505 mapping stays as it is). No other mapping
  change touches alerts; channel deactivation does not touch alerts.
- Re-activation is unchanged (it starts the initial sync; a failure opens a **new** alert).
- Integration tests: failed pair → deactivate → `Resolved` + `alert.changed` hint; deactivating a mapping without an
  alert → no alert write, no alert hint; an update that keeps `Active` → alert unchanged; a rolled-back update (409 on
  the external id) → alert unchanged; deactivate → activate with `SIM-PERM-` SKU → new alert row.
- README "Alerts": the resolution table gets this cause.

## Part B — Alerts page (`/alerts`, every role; acknowledge Manager/Admin)
Remove `plannedTask` from the `/alerts` menu item; the `/logs` item's `plannedTask` becomes `'018b'`.

**Cache:** new tag type `Alert`; `alertsApi` with `listAlerts` (`GET /api/alerts`, provides `{ type: 'Alert', id: 'LIST' }`
plus one `{ type: 'Alert', id }` per item) and `acknowledgeAlert` (`POST /api/alerts/{id}/acknowledge`). Types follow
`AlertResponse` / `AlertListQuery` exactly (camelCase; enums as the API's strings). No `getAlert` query unless a part
of this task needs it.

**List** — every filter, sort and page in the address through `useListQueryState` (reload keeps them; the search-box
rules of 012 apply where a text field exists):
- Filters: status (multi; **no status parameter = the server's default Open + Acknowledged**, shown as such; "Resolved"
  or "All" are explicit choices), type (multi), product (`ProductSelect`, reused unchanged), channel (select from the
  channel list), last-occurred range in local days (same component and rules as the processing page; the 92-day
  limit is the server's — its 400 shown on the field). Changing a filter starts on page 1; "Clear filters" removes all.
- Sort: `lastOccurredAt` (default, newest first), `firstOccurredAt`, `occurrenceCount` — the server's whitelist.
- Columns (fit 1280 px in vi and en, column-width test like 016 A4): type (tag with icon + text), status (tag; not
  colour alone), product (SKU + name), channel (name, link to `channelPath`), details (per type, below), occurrences,
  first / last occurred (local time, tooltip with the full date-time as elsewhere), acknowledged (by e-mail + time,
  "—" otherwise), resolved at, action.
- Details per type, from the response only:
  - `LowStock`: "Available {availableStock} / threshold {lowStockThreshold}" — the **current** values the API returns
    (for a resolved alert they describe now, not the time of the alert; say so in a tooltip).
  - `InsufficientStock`: link to the latest order (`orderPath`).
  - `StockSyncFailed`, `ProcessingFailed`: `errorCode` (shown as code; no message — the API has none) + link to the
    latest task (`processingTaskPath`); `ProcessingFailed` also the order when there is one.
- Actions:
  - **Acknowledge** (Manager/Admin, only on `Open` rows; hidden for Staff) with a confirmation (it cannot be undone —
    same pattern as retry). 200 → success message; 409 `alert_resolved` and 404 `alert_not_found` → localised message,
    and the list is fetched again. The mutation invalidates `{ type: 'Alert', id }`, the list and `DashboardSummary` on
    success and on these two errors; nothing on other errors.
  - **Import stock** (Manager/Admin, `LowStock` and `InsufficientStock` rows that are not resolved) → `importPath(productId)`.
- Error codes `alert_not_found`, `alert_resolved` translated (vi, en).

## Part C — Realtime and dashboard
- `hintTags.ts`: `Alert` joins `REALTIME_TAG_TYPES` (so `resync` refreshes it).
  - `alert.changed` → `{ Alert, alertId }`, `{ Alert, LIST }`, `DashboardSummary`; `all: true` → `Alert`, `DashboardSummary`.
  - `low_stock.created` → `{ Alert, LIST }`, `DashboardSummary` (it always arrives with an `alert.changed` in the same
    batch; mapping it anyway keeps the client correct if that ever changes). **No toast, no sound, no badge** (log
    "Task 018 plan").
  - `inventory.changed` additionally → `{ Alert, LIST }` (a mounted list shows the current stock of low-stock alerts).
  - A product update (mutation) invalidates `{ Alert, LIST }` (SKU, name and threshold are shown — rule of log 012);
    a channel update too (name shown).
  - Unit tests in `hintTags.test.ts` for every new mapping, incl. `all: true`.
- Dashboard: a card "Alerts" from `summary.alerts` (current, independent of the range — say so in the card's
  tooltip): open, acknowledged, and per type open / acknowledged (every type, zeros included, as the API sends them).
  Each number links to the alerts page filtered by that status (and type). The `DashboardSummary` type gets the
  section exactly as the API returns it.

## Tests (required)
Frontend (mocked API; fake hub where realtime matters; fake timers only where needed — say where; every test ends with
its requests settled — handoff gotcha):
1. Part A tests as listed there (A1 incl. the repetition numbers, A2 ×3, A3, A4).
2. Alerts list: default request carries no `status`; each filter, sort and page round-trips through the address and
   resets the page; "Clear filters"; range 400 on the field; column widths at 1280 px; details per type (four types,
   incl. "—" where empty); links (order, task, channel, import).
3. Acknowledge: Manager → confirmation → request → row shows `Acknowledged` after the refetch; 409 and 404 → message +
   refetch; Staff sees no button; the button is absent on Acknowledged and Resolved rows.
4. Realtime: an `alert.changed` hint refetches a mounted alerts list and the dashboard summary; `inventory.changed`
   refetches a mounted alerts list; nothing is requested for a hint when neither is mounted.
5. Dashboard alert card: numbers, zeros, links with the right filters.
6. Language switch on the alerts page (filters set, confirmation open) keeps everything but the text
   (`expectLanguageSwitchChangesTextOnly`).
Backend: A6 integration tests; existing tests green without modification.

## Acceptance criteria
- [ ] Frontend typecheck, lint, test, build pass; **5 consecutive full `npm test` runs green** with PostgreSQL and
      RabbitMQ running (wall time of each); backend build 0 warnings, all tests pass.
- [ ] A1 numbers (old hook: failures of 30 repetitions; fix: 0 of 50) and the production-build dialog check in the report.
- [ ] Bundle: first-load set before/after (`npm run measure`); the alerts page is a lazy route (no growth of the first-load
      set beyond the menu/route entry; explain anything above 10 kB raw).
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no client-side business rules (which alerts exist, when they
      resolve, who may acknowledge — the server's; the client only hides the button by role as elsewhere).
- [ ] `scripts/smoke/task-018.ps1` (API **and** Workers in Development; `_common.ps1`; strict `Assert-Step`): smoke
      Manager + Staff; smoke product threshold 5 → `LowStock` Open in `GET /api/alerts?productId=…`; import 10 → Resolved;
      mapping on `website` with external SKU `SIM-PERM-…` → task `Failed` → `StockSyncFailed` Open; **Manager sets the
      mapping Inactive → the alert is Resolved**; re-activate with a normal SKU → task `Succeeded`, no unresolved
      `StockSyncFailed` for the pair; set the mapping Inactive again (smoke mappings end Inactive); lock both smoke
      users; `Invoke-FrontendChecks`. Run it and paste the table (Part A5 format).
- [ ] Real-browser check on the **production build** (Workers running; smoke data only; Staff for read-only, Manager
      for writes): A1 as above; alerts page at 1280 px in vi and en; a reservation that makes a smoke product low shows
      a new `LowStock` row and the dashboard card changes **without reload**; Manager acknowledges it; an import resolves
      it live (row leaves the default list); Staff sees no acknowledge/import actions; all smoke mappings left Inactive.
- [ ] README "Frontend": alerts page, the alert tags of the event → tag map; README "Alerts": A6.
- [ ] Manual check for the user documented (short: make a smoke product low, watch the alert appear and resolve).

## Out of scope
Logs/Audit page and its endpoint (018b), alert detail route, header badge / toast / sound for new alerts, manual resolve
or reopen, alert e-mails (024), purge of resolved alerts (026), any change of the alert lifecycle except A6, a search
box on the alert list (the API has none — product filter instead), channel deactivation resolving alerts.

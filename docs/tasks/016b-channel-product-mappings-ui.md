# Task 016b — Frontend: channel product mappings on the Channels page

Status: Open
Written by: Cowork, 2026-10-02
Depends on: 016 (accepted)

## Context
Doc §3.2 (Channels), §7.1 `ChannelService` ("Channel và ChannelProduct mapping"), §16 "stock sync with retry and DLQ".
Task 014 delivered the mapping API (README "Channel product mappings and stock-sync tasks"; README section 7 for what a
mapping triggers); review 014 asked that channels without stock sync are visible as such (P3); task 016 delivered the
processing pages this task links to. This task is the second half of the planned 016 (roadmap change, 2026-10-02).

Read first: implementation log "Task 014 plan", "Task 016 plan", "Task 016 result" (decisions of review 016), "Frontend
decisions", "Task 012 plan/result" (cache rules, form dialogs, confirmations); reviews 014 and 016; frontend
`features/channels/*`, `features/products/components/ProductSelect.tsx`, `features/products/productsApi.ts`,
`features/processing/paths.ts`, `features/processing/ProcessingTaskPage.tsx` (detail route pattern, not-found rules),
`features/realtime/hintTags.ts`, `features/orders/components/OrderOutcome.tsx`, `shared/hooks/useListQueryState.ts`,
`useFormSubmission.ts`, `useResetFormOnOpen.ts`; backend `ChannelService`, `ChannelContracts.cs`,
`ChannelProductContracts.cs`, `RabbitMqTopology.StockSyncQueueOf`, `DashboardService` (how it already uses that map).

If the implementation needs a decision that is not settled there (a new endpoint beyond the `stockSyncEnabled` field, a
status filter on the mapping list, a bulk mapping import, a delete of mappings, a global state change), stop and report
(`Blocked`).

## Part A — Follow-ups from review 016 (separate commits)
1. **`127.0.0.1` for local connections.** Replace `Host=localhost` by `Host=127.0.0.1` in the README (user-secrets of the
   API and of the Workers, `OMNICHANNEL_MIGRATOR_CONNECTION`) and in the fallback of
   `OmnichannelDbContextDesignTimeFactory`; `RabbitMq:HostName` in `Omnichannel.Workers/appsettings.json` and the
   default in `RabbitMqOptions` → `127.0.0.1`. One sentence in the README (section 1 or 3) on why (Windows tries `::1`
   first; the ports are published on `127.0.0.1` only; ~2 s per new connection otherwise). Nothing in `docker/`
   changes (containers use service names). Do **not** touch user-secrets or environment variables: list the commands
   for the user under "Manual steps" (placeholders only, never values). Tests that pin the old default are updated and
   listed.
2. **Hint flush as a throttle** (`createTagCoalescer`, `hintTags.ts`): when no flush happened within the last
   `FLUSH_WINDOW_MS`, tags are flushed at once; otherwise they are collected and flushed when that window ends, as one
   union (each tag once; a bare type supersedes its ids — unchanged). At most one flush per window, `cancel()` drops what
   is pending, the connect resync goes through the same path. Update the doc comments ("at most once per second",
   "trailing") and README "Frontend" if it describes the window. Tests (fake timers): idle → immediate flush; a second
   batch within the window → one flush at the window's end with the union; a burst over several windows → one flush
   per window; `cancel()`; resync on (re)connect flushed at once when idle.
3. **Order outcome: list invalidation only when not live.** `OrderOutcome` dispatches its once-per-terminal-state
   invalidation (`Order` LIST, `Inventory`, `InventoryMovement`, `Product`) only when the connection is not
   `connected` at the moment the task is first seen finished; the doc comment states why (the same hint batch already
   invalidated them). Tests: live → terminal → no list invalidation from the outcome (count requests of a subscribed
   list, or spy on `invalidateTags`); not live → exactly one, as in 013; connection lost while waiting → finished by
   polling → one. Existing 013/016 outcome tests unchanged or listed as a deviation with the reason.

## Part B — Backend: `stockSyncEnabled` on channels (one small commit)
- `ChannelResponse` gets `stockSyncEnabled` (bool): true when the channel's slug has a stock-sync queue
  (`RabbitMqTopology.StockSyncQueueOf(slug) is not null` — the one map; no second copy). It describes the capability,
  not the status: an Inactive `website` is still `true`. The map is code, not data, so the value is set after the query
  (the projection `ToResponse` is translated to SQL and cannot call it); list, get, create and update all carry it.
- Integration tests: the three seeded channels `true`; a channel created through the API `false` (create and list);
  update keeps the value. Existing tests green without modification of assertions.
- README "API conventions": one line on the field.

## Part C — Channels list
- Column **Stock sync**: "Synced" / "Not synced" (tag with icon + text, tooltip on "Not synced": "This channel has no
  stock-sync queue: products can be mapped, but stock is never sent to it"). From `stockSyncEnabled` only.
- The channel name links to the channel detail route; the Admin edit action stays as it is.
- Fits 1280 px without horizontal scrolling (widths adjusted; long values truncated with `TruncatedText`).

## Part D — Channel detail with mappings (`/channels/:channelId`, every role; writes Manager/Admin)
- **Route** outside the menu, the menu highlights *Channels* (same pattern as `/processing/tasks/:taskId`). Non-GUID id →
  not found without a request; 404 from `GET /api/channels/{id}` → not found. "Back" returns to the channels list.
  `getChannel` query (`GET /api/channels/{id}`) providing `{ type: 'Channel', id }`; the existing channel mutations also
  invalidate that id.
- **Header**: name, slug, type, status, stock sync. When `stockSyncEnabled` is false: an info alert with the sentence of
  Part C. When the channel is Inactive: an info alert "Inactive channel: no stock is sent to it until it is active
  again" (README section 7). When stock sync is enabled: a link "Stock-sync tasks of this channel" →
  `processingListPath({ taskType: 'StockSync', channelId })`.
- **Mapping list** `GET /api/channels/{channelId}/products`: search, sort `sku` (default) / `createdAt`, paging — all
  through `useListQueryState` (address; reload keeps them; search box follows the address). Columns: SKU, product
  name, external product id, external SKU, status (tag), created, updated, action (Manager/Admin). Empty values shown
  as "—". Fits 1280 px.
- **Cache**: new tag type `ChannelProduct`; the list provides `{ type: 'ChannelProduct', id: 'LIST' }`. Create and update
  invalidate it on success (not on 4xx). A product update invalidates it too (SKU and name are shown; rule "a mutation
  invalidates every list that displays fields it changed", log task 012). No realtime tag for mappings (only users
  change them); the list keeps `refetchOnMountOrArgChange`.
- **Create** (Manager/Admin; button hidden for Staff): dialog with `ProductSelect` (reused, unchanged), external product
  id, external SKU. Field rules only as the existing forms do them (max length 128 as an input limit, trimming as
  the API does); the rule "one of the two external ids" is the server's — its 400 on `externalProductId` is shown on
  that field. 409 `channel_product_exists` on the product field, 409 `external_product_id_taken` on the external
  product id field (same mechanism as `CODE_FIELDS` of the product form); 404 `not_found` (channel gone) as a form
  error.
- **Edit** (Manager/Admin): external product id, external SKU, status (Active / Inactive). Product and channel are shown,
  not editable. Same error mapping; 404 `channel_product_not_found` as a form error and the list is fetched again.
  No separate activate/deactivate action and no confirmation: the status field is the action (reversible, nothing is
  deleted).
- **Sync result**: after a successful create or edit the dialog closes and one dismissible alert above the list says
  what the API answered — with `syncTaskId`: "Stock sync started" + the task's live status (`TaskStatusTag` from
  `getProcessingTask(syncTaskId)`; it follows the `ProcessingTask` hints while live) + a link to the task detail;
  without: "No stock sync was started" + a tooltip with the reasons the API documents (channel or mapping inactive,
  channel without stock sync, nothing changed that the channel has to learn). The next save replaces it; leaving the
  page drops it. No client-side guess which reason applied.
- Language switch: text only (route, address state, open dialog and the result alert kept).

## Tests (required)
Frontend (mocked API; fake hub where realtime matters; fake timers only where needed — say where):
1. Channels list: stock-sync column for both values (text, tooltip); name links to the detail; widths at 1280 px
   (column-width assertion like the orders-table test of 016 A4).
2. Detail: header fields; alerts for "no stock sync" and "inactive"; tasks link only when enabled and carrying both
   filters; non-GUID and 404 → not found; back keeps the list address.
3. Mapping list: search, sort and paging round-trip through the address and reset the page; empty external ids as "—";
   Staff sees no create/edit.
4. Create: 201 with `syncTaskId` → result alert with the task status, and the status changes after a
   `processing.task.changed` hint for that task (fake hub); 201 without → "no stock sync" alert; 400 on
   `externalProductId`; both 409s on their fields; 404 channel.
5. Edit: status change sent; 200 with / without `syncTaskId`; 404 `channel_product_not_found` refetches the list.
6. Cache: a product update refetches a mounted mapping list; a rejected mapping mutation does not.
7. Language switch on the channels list, the detail and an open mapping dialog keeps everything but the text
   (`expectLanguageSwitchChangesTextOnly`).
8. Part A2 and A3 as listed there; Part B backend tests.

## Acceptance criteria
- [ ] Frontend typecheck, lint, test, build pass; backend build 0 warnings, all tests pass.
- [ ] Bundle: first-load set before/after (`npm run measure`), growth above 10 kB raw explained.
- [ ] No hard-coded UI text; `vi.json` / `en.json` parity; no client-side business rules (which channels sync, which
      reason a null `syncTaskId` had, the "one external id" rule — all the server's).
- [ ] `scripts/smoke/task-016b.ps1` (API **and** Workers; local-default rules of `_common.ps1`): smoke Manager + Staff;
      `GET /api/channels` → `website`, `pos`, `digital-platform` have `stockSyncEnabled` true; smoke product, import 5;
      Manager maps it on `website` → `syncTaskId` → task `Succeeded`; Staff create → 403; Manager changes the external
      SKU → new `syncTaskId` → `Succeeded`; Manager sets `Inactive` → `syncTaskId` null; list `?search=<smoke SKU>`
      shows it `Inactive`; lock both smoke users; `npm run build`; `npm test`. Run it and paste the table. **The smoke
      mapping ends Inactive** (never touch real products' mappings).
- [ ] Real-browser check (Workers running; smoke data only; Staff for read-only, Manager for writes; Admin only via
      `ui-check-61563` if needed, locked again afterwards):
      - channels list at 1280 px in vi and en: stock-sync column, no horizontal scroll;
      - detail of `website`: tasks link opens the filtered processing list;
      - Manager creates a mapping for a smoke product → result alert shows the task going to `Succeeded` without reload;
      - external SKU `SIM-PERM-…` → task `Failed` shown in the alert, link opens its detail with the attempt;
      - duplicate product → message on the product field; deactivate → "no stock sync" alert; re-activate → new task;
      - Staff: no create/edit controls;
      - VI/EN measurements of the detail page and the open dialog;
      - all smoke mappings left Inactive.
- [ ] Manual check for the user documented (short: map a smoke product on `website`, watch the task finish).

## Out of scope
Mapping delete, bulk mapping or import, a status filter on the mapping list, last-sync time per mapping (needs an
endpoint), editing channel slugs, stock sync for API-created channels (queue per channel at runtime), alerts (017/018),
any change to the hub or the hint contract.

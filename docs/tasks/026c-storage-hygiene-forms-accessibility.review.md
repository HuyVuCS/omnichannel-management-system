# Review 026c — Follow-ups of 026b; object storage hygiene; one required-field text; accessibility pass

Reviewer: Cowork, 2026-10-14
Verdict: **Accepted.** Released to production as `9712cf1` (tag `9712cf1f7544`, migration `TaskListOrderIndex`,
≈ 20 s API downtime); the first clean-up run deleted nothing (counted beforehand: 0 of 2, both in use). No P0 / P1.
All eighteen deviations accepted. Two new P3 findings (F1, F2) and the answers to the seven questions go into
**task 027 Part A**.

## Checked
- Report 026c; commits `2f78c4e` … `bcebe73` (`git diff --stat f363c23..bcebe73`: 65 files).
- Read in full: `ProductImageService.cs`, `ProductImageCleanup.cs`, `ProductImageKey.cs`; the diffs of
  `ProcessingTaskService.cs`, `DashboardService.cs`, `Caddyfile`, `providers.tsx`, `test/accessibility.ts`,
  `test/setup.ts`.
- Checked by Cowork against the code:
  - **Confirm (B1):** staging key parsed strictly (canonical lower-case GUIDs, owner = route product); repeated
    confirmation recognised *before* any storage call; cheap `HEAD` of the upload, copy to a key new for this
    confirmation, then size / type / magic bytes **of the copy** — the copy is the only object nobody else can write,
    so deviation 1 is the right order, not a detour. Transaction: row lock, "a concurrent confirmation of the same
    upload came first" check, deadline check, `SetImage`; every storage call outside the transaction; a copy is deleted
    on every failure except an ambiguous commit (left to the sweep — correct: it may be referenced).
  - **Old keys stay valid:** final format unchanged; `IsFinalOf` compares the first 8 bytes including the version
    nibble (8 vs 7 of a stored UUIDv7) — a stored key can never be mistaken for a copy of an upload. 60 bits of digest:
    a false "already confirmed" is out of reach.
  - **Sweep (B2):** strict key parse (unknown keys left alone), age by the storage's `LastModified` (`copiedAt` is
    taken *before* the copy, so the deadline is measured conservatively), reference question after the listing, one
    statement per page, cap per run, dry run sharing the code, advisory lock on an unpooled connection (same pattern
    as 026b). The safety argument holds: an object older than `MinAge` (≥ 4 deadlines; production 24 h) has a finished
    confirmation. Precisely, the deadline bounds the *decision* to refer; `SaveChanges` + commit follow under the row
    lock — milliseconds against a margin of ≥ 90 s (development floor) or ~24 h (production). Fine as is.
  - **Task list (A2):** `AttemptNo = (SELECT max(...))` per page row — a per-row index lookup; JSON identical
    (byte-compared), `ROW_NUMBER` pinned out by a test. Index `(CreatedAt, TaskId)` ascending serves both directions
    (deviation 8 is correct; a mixed-direction index would serve neither).
  - **Summary:** `lastSucceededAt` moved into the channels statement — see F2.
  - **C1:** one `validateMessages.required` in the `ConfigProvider`, re-rendered on language change.
  - **C2:** axe after every test with `color-contrast` off in jsdom and exactly one documented exception (the AntD
    listbox, matched by role **and** container). Style sheets detached while axe runs — measured, and harmless for the
    rules that stay on.
- **Verification** (not re-run by Cowork): backend 1935 / 1935 locally, 14 / 14 mutants killed; frontend 526 / 526 ×2
  + slow; CI green on `70b1d3f`; `omni026c` smoke 025 53/53, 026 ×5, 026b 16/16, 026c 41/41 with the sweep drill;
  development migrated as the migrator; production: dry-run count, migration 4 s, live `task-026.ps1` 5/5,
  `task-025b.ps1` 28/28 in 4 of 5 (q7).

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **F1 — double-click race of a confirmation.** Two confirmations of the *same* upload where the second reads the product before the first commits, and reaches `HEAD` / copy after the first deleted the staging object: the second answers **400 `image_not_uploaded`** although the image is now exactly that upload. Only the "already confirmed" check at the start and the one under the lock exist; the path "upload gone" does not look again. | 027 A1: on "nothing uploaded" (missing `HEAD`, copy returns false) re-read the product; `IsFinalOf(current, uploadId)` → 200 with the product. Test with a storage hook that deletes the staging object between the first read and the `HEAD`. |
| P3 | **F2 — `lastSucceededAt` of a channel that never had a successful stock sync** walks `IX_ProcessingTasks_Status_UpdatedAt` through *every* succeeded task (all task types) before it can answer null. The realistic case is a channel with stock sync switched off (`stockSyncEnabled = false`) — then on every summary request, growing with the table. The report noted it; the seed had no such channel. | 027 A2: measure at the 026b seed plus one channel without successes. ≥ 2 ms → partial index `(ChannelId, UpdatedAt) WHERE TaskType = 'StockSync' AND Status = 'Succeeded'` (additive; the one migration of 027), else record. |
| P3 | Focus ring 1.9:1, sortable column header focus by colour only (q1). | 027 A3. |
| P3 | No `<h1>` on any page (q3). | 027 A4. |
| P3 | Chunked oversize body through Funnel sometimes 502 (q7). | 027 A5. |
| — | Process: the release followed the runbook; the dry-run count before G was exactly what the task asked; the proxy restart (deviation 17) was caught and documented. | — |

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | Checks run on the copy, after a cheap look at the upload | Accepted — closes the gap the task's order left open. |
| 2 | Final key new per confirmation (digest of the upload id + random) | Accepted — keeps the sweep's argument free of exceptions. |
| 3 | Confirmation gives up 30 s after its copy (503) | Accepted — the bound the sweep rests on. |
| 4 | `MinAge` floor 2 min; `Interval` / `MaxPerRun` bounds | Accepted. |
| 5 | Dry run of the clean-up | Accepted — it is what made the G count trustworthy. |
| 6 | Unknown keys under the prefixes never deleted | Accepted. |
| 7 | `max()` lookup instead of `ORDER BY … LIMIT 1` (EF translation) | Accepted — measured; pinned by a test. |
| 8 | Ascending `(CreatedAt, TaskId)` | Accepted — correct. |
| 9 | axe after every test (+17 % time) | Accepted — nothing can be forgotten. |
| 10 | Theme colours changed | Accepted (q2). |
| 11 | `axe-core` 4.13.0 | Accepted — the pinning rule. |
| 12 | `_hardening.ps1` waits out a 429 of the login limit | Accepted. |
| 13 | `task-025b.ps1` ×5 on throwaway only partly meaningful | Accepted — its hardening block was the point. |
| 14 | `task-026b.ps1` without the stale drill | Accepted — code unchanged. |
| 15 | `task-019.ps1` step 14 adapted | Accepted. |
| 16 | Worker test hosts run without the clean-up | Accepted — as the other periodic jobs. |
| 17 | `restart proxy` after `up` (bind-mounted Caddyfile) | Accepted — and now in runbook 4. |
| 18 | Two live series; `_hardening.ps1` accepts 413 or 502 above 1 MiB | Accepted — that step only asserts "refused". |

## Questions of the report
1. **Focus indicator → yes, in 027 A3.** One global rule, not a theme token: `:focus-visible` outline 2 px solid
   `#0958d9` with 2 px offset (6.2:1 on white, WCAG 1.4.11 needs 3:1), applied to the focusable elements of the app
   including sortable column headers; `colorPrimaryBorder` stays as it is (it also draws info alerts and
   "processing" tags). Checked in the browser by the e2e suite (focus screenshot or computed style), not only in jsdom.
2. **New colours → accepted as they are.** One brand blue `#0958d9` everywhere; no split between "large" and "small"
   elements — two blues for one meaning is a maintenance cost with no user benefit. The user's look at the live
   dashboard (handoff §6) is still welcome but does not block anything.
3. **`<h1>` → yes, in 027 A4.** It is not structural: every page's title comes from the one `PageHeader` component.
   Render it as `level={1}` with the current visual size (the font size / weight / line height of level 4 set on the
   element), so nothing moves. The login page and the route error page get their own `<h1>`. A test asserts exactly
   one `<h1>` per route.
4. **A staging key reusable while its link lives → accepted, nothing to remember.** After a confirmation the link can
   only write the staging key; while the image is still a copy of that upload, a confirmation of it is a no-op; once
   the image changed, confirming it again is "a Manager confirms what is there now" — the same right and the same
   checks as any upload, within the link's lifetime. Remembering upload ids would add state for no protection.
5. **`updatedAt` sort → recorded, no expression index.** 9–16 ms at 24,000 tasks; the default sort is `createdAt`.
   Revisit at ~100k tasks or when that sort becomes a default.
6. **No index on `Products.ImageKey` → recorded.** One statement per 200 keys against a table of tens of rows today.
   Revisit at ~10,000 products.
7. **Chunked oversize body through Funnel → (a) accept.** No dashboard request is chunked (a `fetch` with a string,
   `Blob` or `FormData` body always sends `Content-Length`); (b) would make the API read up to 1 MiB of every refused
   body — more work for exactly the requests it wants to refuse. `_hardening.ps1` accepts **413 or 502 for the
   chunked step only when the target is a public origin** (through the tunnel); against loopback / a throwaway stack it
   stays strict (readable 413). The announced-length step stays strict everywhere. → 027 A5.

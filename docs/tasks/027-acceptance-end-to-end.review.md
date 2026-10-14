# Review 027 — Acceptance end to end (doc §16)

Reviewer: Cowork, 2026-10-14
Verdict: **Accepted — the roadmap is complete.** Released to production as `3b27e91` (tag `3b27e91c6daa`, migration
`LastStockSyncIndex`, public `/health` down ≈ 6 s, runtime 10.0.13). All fifteen criteria of doc §16 are proven on a
throwaway stack (`Full` 106 / 106, e2e 5 × 8 / 8), in CI (job `acceptance`, green) and live (`NoDrills` 75 / 75,
e2e 8 / 8). No P0 / P1 / P2. All fifteen deviations accepted. By the rule of the roadmap (user, 2026-10-14) nothing
of this review becomes a new task: the open points are known limitations in `docs/acceptance.md`.

## Checked
- Report 027; commits `de99516`, `3a2abe0`, `3b27e91`, `33dce79` (61 files).
- Read: the diff of `ProductImageService.cs` (A1), `ProcessingTaskConfiguration.cs` + migration (A2), `index.css`
  (A3), `docker-compose.acceptance.yml`, the CI job `acceptance`, `playwright.config.ts`, `docs/acceptance.md` in full.
- Checked by Cowork against the code:
  - **A1:** both "upload gone" paths (no `HEAD`, copy returns false) re-read the product and answer 200 only when its
    image is a copy of *this* upload; otherwise the old 400. Race tested at both hold points; mutant killed.
  - **A2:** the measurement shows exactly the case of review 026c F2 (+12–14 ms with one channel without a success);
    partial index `(ChannelId, UpdatedAt) WHERE TaskType = 'StockSync' AND Status = 'Succeeded'`, index-only scan,
    0.07–0.19 ms. Additive, applied throwaway → development → production in that order.
  - **A3:** one ring, two placement refinements found in the browser (deviation 1) — same colour / width, no theme token
    touched. `!important` is justified in the comment (AntD's more specific `outline: 0`).
  - **Acceptance override:** two differences only (SKU rules, retry delays), refused for `omnichannel-prod` by the
    script, fresh-broker note — correct.
  - **CI job:** generated env file with letters / digits, every value masked, artifacts uploaded only after a
    `grep -F` of every secret value over them, stack and env file removed `always()`. No repository secret needed.
  - **Playwright:** no trace / video / storage state (the trace would hold the bearer token), passwords typed without a
    titled `fill` step, one worker, no retries, no sleeps except the login-limit wait (same rule as `_hardening.ps1`).
- **Verification** (not re-run by Cowork): backend 1942 / 1942, frontend 546 / 546 ×2 + slow, three mutants killed
  (A1, B4, a broken inventory row on `omni027` → 3 red steps); first `Full` run 86 / 94 reported as red with its causes
  (deviation 8) — the right way to report it.

## Acceptance criteria of doc §16
| # | Criterion (doc §16) | Evidence | Verdict |
| --- | --- | --- | --- |
| 1 | Tạo được order từ 3 component Website/POS/Digital Platform Online | B1, `order-entry.spec.ts` (three panels in a real browser) | ✅ |
| 2 | Order được normalize và đưa vào RabbitMQ | B1 (outbox `Published`, attempt by `OrderWorker`), webhook of the same shape | ✅ |
| 3 | Duplicate order không bị xử lý hai lần | B2 + load `duplicates` (100 → 50), invariant | ✅ |
| 4 | Nhiều order đồng thời cùng SKU không gây oversell | load `contention` (60 / stock 20 → 20), 026b baseline | ✅ |
| 5 | TotalStock/ReservedStock/AvailableStock chính xác | invariants incl. the new history invariant (mutant red) | ✅ |
| 6 | InventoryMovement và OutboxEvent được ghi đúng transaction | new test: commit fails → neither row; movement ⇔ event invariants | ✅ |
| 7 | Stock sync có retry và DLQ | `SIM-TEMP-` retry, `SIM-DOWN-` → DLQ → manual retry → resolved | ✅ |
| 8 | Dashboard nhận cập nhật gần real-time | WebSocket hints ≤ 5 s; `realtime.spec.ts` incl. API restart → resync | ✅ |
| 9 | Có thể tra một order và biết task, worker, instance, attempt, lỗi | B5, `trace.spec.ts`, Logs by correlation id | ✅ |
| 10 | Low-stock alert hoạt động theo threshold | B6, `alerts.spec.ts` | ✅ |
| 11 | Có lịch sử inventory và processing | B6 | ✅ |
| 12 | Có export Excel | B7 (worksheet contains the SKU), `export.spec.ts` | ✅ |
| 13 | Ảnh sản phẩm lưu S3 | B7 (S3-compatible, decision of 019; no binary column), `image.spec.ts` | ✅ |
| 14 | API có authentication/authorization | B8 role matrix, forged tokens; expired token by unit test only | ✅ |
| 15 | Có test cho các flow quan trọng | `docs/acceptance.md` §15: every flow of CLAUDE.md §19 has a named test | ✅ |

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | `bypassCSP: true` in the e2e suite: a regression of the production CSP would not be seen by any automated check (026 verified it by hand twice). | Known limitation. If ever wanted: one spec without `bypassCSP` that only loads the app and fails on a `securitypolicyviolation` event. |
| P3 | The live e2e run from the host machine needs `E2E_HOST_RESOLVER_RULES` with today's Funnel relay addresses (deviation 7) — they can change. | Accepted: optional, only for that one situation; runbook §17 says how to look the addresses up. |
| — | `docs/acceptance.md` quoted a paraphrase of the criteria (deviation 10). | **Fixed by Cowork in this review**: the fifteen lines are now the document's wording, verbatim. |

## Deviations
All fifteen accepted. Notes on three:
- **5 (circuit breaker during the DLQ drill)** — this is the designed behaviour of doc §13 (Polly), and the drill now
  asserts what is true (all attempts temporary, first `channel_unavailable`) instead of a simplification of the task.
- **8 (first Full run red, reported)** — exactly the reporting rule; thank you.
- **14 (CVE-2026-70159 bump)** — right call, and the user decided it. Ten packages of one servicing release, base
  images pulled, runtime checked inside the images. The development machine's runtime is the user's (q3).

## Questions of the report
1. **Focus of checkboxes, radios and switches → known limitation, no task.** Switches are buttons and already get the
   global ring; for checkboxes / radios AntD draws on a sibling of a hidden input. If the user wants it later it is a
   few lines of CSS of the same kind as A3 (`.ant-checkbox:has(input:focus-visible)` / `.ant-radio…` ring), plus the
   e2e focus check — a maintenance commit, not a task.
2. **`libgssapi_krb5.so.2` message → harmless, not new.** It was already printed before the base images were pulled:
   report 026c, Part G step 3 (the one-off Workers container, and the running Workers 14 times in 20 h). Npgsql probes
   for GSSAPI; password authentication does not need it. Recorded in the log; no action.
3. **Development runtime 10.0.12 → the user is updating it** (SDK 10.0.4xx with runtime 10.0.13, via winget or the
   installer). Until then the development API runs the unpatched SignalR server — bound to `127.0.0.1` only.
4. **Live demo data → accepted** as agreed before G (`ACC027-*` / `E2E027-*`, users locked, mappings deactivated).

## Remaining for the user
- Remove the two temporary variables `OMNICHANNEL_LIVE_ADMIN_EMAIL` / `OMNICHANNEL_LIVE_ADMIN_PASSWORD` (report,
  "Manual steps") and check with `[Environment]::GetEnvironmentVariables('User').Keys`.
- Update the local .NET runtime to 10.0.13 (q3).
- Optional only: 025c (Oracle Always Free VM) for an always-on backend.

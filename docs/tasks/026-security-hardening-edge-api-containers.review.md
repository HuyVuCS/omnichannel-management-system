# Review 026 — Security hardening: image tags per release, API and storage headers, body limits, container hardening, frontend CSP; first release

Reviewer: Cowork, 2026-10-13
Verdict: **Parts A–E accepted; Part F (the release) still open.** No P0 / P1. Two P3 findings about error answers on
the cross-origin path go into 026b Part A together with the report's question 3. All nine deviations accepted.
Questions: 1 → the user's go-ahead, release as **026b Part 0**; 2 → **pin**; 3 → **fix** (026b A); 4 → **no**.

## Checked
- Report, commit `c8f31d7` (36 files), report commit `294cb91`, handoff `4b07987`. No migration, no package.
- Read in full: `SecurityHeadersMiddleware.cs`, `RequestBodyLimit.cs` (+ middleware), `RequestLogProperties.cs`; the
  diffs of `Program.cs`, `GlobalExceptionHandler.cs`, `ValidationProblemResponse.cs`; `docker-compose.prod.yml` /
  `.tls.yml`, `Caddyfile`, `rabbitmq/20-memory.conf`; `vite/contentSecurityPolicy.ts`, `vercel.json`;
  `set-image-tag.ps1`, `task-026.ps1`.
- Checked by Cowork against the code:
  - **Headers are applied in `OnStarting`**, so answers rebuilt by the exception handler, the rate limiter's 429 and
    the 413 keep them. Swagger / OpenAPI exempt from the CSP only in Development. `no-store` on everything without its
    own `Cache-Control` (deviation 1 — correct: the login answer is anonymous and carries the token).
  - **Body limit:** one constant for Kestrel, the early `Content-Length` check and the Caddyfile (a test compares the
    file) — no drift possible. Chunked oversize → `BadHttpRequestException` 413 → the same problem document.
    128 KiB = 2 × the measured largest body (the webhook's 64 KiB is the binding one). Good.
  - **Binder errors:** `AllowInputFormatterExceptionMessages = false` plus key rewriting (`$.items[0].stock` →
    `items[0].stock`, body `$`), localised `ValueInvalid` / `BodyNotJson`; when the parser failed only its errors are
    kept. No .NET type name can reach a client any more.
  - **Log forging:** the request line's `RequestPath` is `PathBase + Path` as `ToUriComponent()` — `%0A` stays encoded.
  - **Compose:** `x-hardening` on every service; add-backs measured, narrowest kept (`DAC_READ_SEARCH` instead of
    `DAC_OVERRIDE`); RabbitMQ as `rabbitmq` with no capability and a read-only root (better than the task); Caddy
    needs `NET_BIND_SERVICE` only because of the binary's file capability — explained, not a broad capability;
    `IMAGE_TAG` with `:?` — a stack without it refuses to start instead of picking a stale image.
  - **RabbitMQ memory:** `total_memory_available_override_value = 1GiB`, watermark 614 MiB of 1024 (was 2154 of 3591).
  - **Storage site:** `header_down` `nosniff` + `default-src 'none'; sandbox` — objects can no longer run as a page
    on the storage origin; presigned `PUT` / CORS and `Content-Disposition` unchanged (smoke 025).
  - **CSP plugin:** build only, origins parsed with the same function as `apiOrigin.ts`, `VITE_STORAGE_ORIGIN`
    required when `VITE_API_BASE_URL` is set (fails the build, not the users), hub source derived `http→ws` /
    `https→wss`; `script-src 'self'` without exceptions. The negative control in the browser session (injected inline
    script blocked, foreign `fetch` and image blocked) is exactly the evidence wanted. Deviation 4 (no `data:` /
    `blob:` / `font-src`) is tighter and measured — accepted.
  - **`set-image-tag.ps1`** changes one line, keeps line endings, refuses two `IMAGE_TAG` lines, prints only tags.
- **Verification** (not re-run by Cowork): build 0 warnings; backend 1825/1825; frontend 518/518 twice + slow;
  first-load set +0.02 kB; CI green; `omni026` hardened: smoke 025 53/53, smoke 026 9/9, restart of every service,
  ownership drill on a volume with data, backup + restore with identical counts, A3 proof through a real Caddy with an
  echo upstream, two browser sessions with zero violations; throwaway stacks and their images removed by tag;
  production untouched.

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `no-store` on every answer without its own `Cache-Control` | Accepted (login is anonymous and carries a token). |
| 2 | Binder keys / dropped MVC parameter error; three tests rewritten | Accepted — the old tests pinned the leaking shape. |
| 3 | Encoded path in the request line for every environment | Accepted — one value everywhere; framework request lines are off in every configuration. |
| 4 | CSP tighter than the draft | Accepted — measured; the plugin's comment says to add a source with the feature that needs it. |
| 5 | RabbitMQ as `user: rabbitmq` | Accepted — no capability at all beats root with three. |
| 6 | Caddy `/data`, `/config` on tmpfs; volumes moved to the TLS override | Accepted. The two production volumes become unused after F; removing them is the user's (runbook 15). |
| 7 | `set-image-tag.ps1` | Accepted. |
| 8 | `vite preview` against development needs `VITE_STORAGE_ORIGIN` | Accepted (README). |
| 9 | `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp`; three Data Protection warnings | Accepted; runbook says three. |

## Questions of the report
1. **Part F → release with the user's go-ahead, as 026b Part 0.** The permission layer refusing even reads of
   `omnichannel-prod` is correct behaviour, not a defect: the user says "yes" in the Claude Code chat (or allows the
   commands when prompted), sets `VITE_STORAGE_ORIGIN` in Vercel first, and does the live console check after. 026b
   starts with exactly 026's Part F (report results in **026's** report, "Production steps (applied)", and set its
   status); 026b's own changes are released at its end (it has a migration).
2. **Digest pinning → yes**, `image: postgres:17@sha256:…` (tag kept for the reader), the four digests the local image
   store has now (production uses the same store). Reason: production has no backup, so an accidental `pull` or a
   Docker reset that fetches a newer `rabbitmq:4-management` (a minor broker upgrade with feature flags) must not happen
   silently; drills and production then run byte-identical third-party images. Updating = a deliberate release that
   changes the digest in the compose file (runbook "Updating a third-party image"). 026b Part A.
3. **`X-Correlation-Id` on 500 → fix** (`OnStarting` in the correlation middleware, as the security headers do) —
   and see finding 1: on the live cross-origin path the frontend cannot read error answers that lack CORS headers either.
4. **`user: postgres` → no.** Five measured capabilities on one internal service with no published port, against a
   change of every operating script's `exec`; not worth a drill. Recorded; revisit only with a reason.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **The 413 of `RequestBodyLimitMiddleware` is written before `UseCors`**: on the live path (frontend on `*.vercel.app`) it has no `Access-Control-Allow-Origin`, so the browser hides it and the UI shows a network error instead of the problem document. Not reachable from the UI today (forms are far below 128 KiB), but the answer was built for clients. | **026b A1**: CORS evaluated before the limit (or the limit after `UseCors`); integration test: cross-origin 413, 500 and 429 carry `Access-Control-Allow-Origin`, `Access-Control-Expose-Headers` and `X-Correlation-Id`. |
| P3 | `X-Correlation-Id` missing on 500 (question 3). | **026b A1**. |
| P3 | Rollback of this release in the report: "check out `8fc1e5e` and `up -d --wait`" — that compose file has no hardening and names `:local`; fine as a one-time rollback, but after F the runbook's tag rollback is the only procedure. | Runbook already says so (section 4); no action. |
| — | The production volumes `proxy-data` / `proxy-config` are unused after F. | User's choice (runbook 15). |

# Task 026 — Security hardening: image tags per release, API and storage headers, body limits, container hardening, frontend CSP; first release

Status: Open
Written by: Cowork, 2026-10-12
Depends on: 025d (accepted — `docs/tasks/025d-production-backups-and-drills.review.md`)

## Context
Doc §6 (security: HTTPS, CORS, validation, secrets), §14 (deployment); CLAUDE.md; OWASP ASVS-style hardening of what
is now on the internet. Roadmap row 026 was split by Cowork (log "Task 026 plan"): **026** = the security items of the
edge, the API, the containers and the frontend, released to production at the end; **026b** = database and
performance; **026c** = object-storage and frontend follow-ups. Items of the old row not listed here are in 026b / 026c.

Read first: reviews 025 (q4, q5), 025b (q2), 025d (q3, findings); log "Task 025 result", "Task 025b result",
"Task 025d result", "Task 026 plan"; `docs/runbook.md` §4, §9, §10, §13; `docker/docker-compose.prod.yml`,
`docker/proxy/Caddyfile`, `docker/prod.env.example`, `scripts/deploy/new-prod-env.ps1`, `frontend/vercel.json`,
`frontend/src/shared/api/apiOrigin.ts`, `Program.cs`, `Middleware/ReverseProxy.cs`, `Filters/ValidationProblemResponse.cs`,
`scripts/smoke/task-025.ps1`, `task-025b.ps1`; `docs/tasks/README.md` (Production steps — **no backups**, user
2026-10-12).

### Production in this task
Only Part F, by the runbook's release procedure (as amended in A2), after everything else is green locally. No backup
(user's decision). Guardrails unchanged: no `down -v`, no volume removal, no `-Force` over `docker/.env.prod`;
`docker/.env.prod` is edited only by adding / changing the non-secret lines named here, never opened for anything else.

### Stop and report (`Blocked`) if
- a hardening setting makes a third-party service fail and the only fix is to run it as root or with a broad
  capability (`SYS_ADMIN`, `NET_ADMIN`, `ALL`) — report the service, the setting and the error;
- changing the ownership of an existing volume would need anything but a one-shot `chown` of that volume's contents;
- the CSP cannot be made to work without `'unsafe-inline'` / `'unsafe-eval'` in `script-src`;
- a migration appears (026 has none).

## Part A — Follow-ups

### A1. Image tag per release (review 025d q3, P2)
- Compose: `image: omnichannel-api:${IMAGE_TAG:?IMAGE_TAG is required}` (and `-workers`, `-migrate`); `IMAGE_TAG` is
  a line of the **stack's env file** — never a shell variable, so `up` cannot silently switch images. The revision
  label stays (`GIT_COMMIT`).
- Production: `IMAGE_TAG` = the 12-character short commit of the release. Throwaway / drill stacks: their own value
  (`drill-<project>`); `new-prod-env.ps1` writes a default (`local-<project>`?) — choose and document; the generator's
  refusal to overwrite stays.
- Release (runbook §4): set `IMAGE_TAG` and `GIT_COMMIT` from the same commit, `build`, `run --rm migrate`,
  `up -d --wait`. Rollback without a migration since: put the previous `IMAGE_TAG` back, `up -d --wait` (no rebuild);
  the runbook says how to list the tags present (`docker image ls omnichannel-*`) and how to remove old ones
  (`docker image rm` of named tags only; never `prune -a` while a rollback might be needed).
- `prod.env.example`, README, runbook §1 / §4 / §7 / §14 updated. Smoke / backup scripts unaffected (they find
  containers by labels) — check.
- The existing `:local` images stay until production runs the new tag (F); then they may be removed by name.

### A2. Runbook without backups (user, 2026-10-12)
- §4 step 1, §5, §9 (header "Backup first"), "Updating a third-party image", rollback "a migration ran": no backup
  step; state once, near the top, that production has **no backup by the user's decision**, what that means (a lost
  Docker Desktop, a failed release or a wrong command loses the data; after a migration only fix-forward) and that
  `backup-prod.ps1` remains available on request. §6 / §7: "available, not in use; the scheduled start was never
  exercised". §13: "Backups: none (user decision 2026-10-12)".
- `rotate-secret.ps1` header comment: "Backup first" removed. No behaviour change.

### A3. `Tailscale-User-*` headers (review 025b q2)
- Caddyfile, both sites: remove `Tailscale-User-Login`, `Tailscale-User-Name`, `Tailscale-User-Profile-Pic` before
  proxying (`request_header` / `header_up` deletion; a `Tailscale-User-*` wildcard only if the pinned Caddy 2.11
  documents it — cite the page) and delete them in the access-log filter.
- Local proof on `omni026` (Part E): a request carrying the three headers → not forwarded upstream and absent from
  the proxy log. Method of your choice (e.g. `caddy adapt` output plus a temporary upstream inside the throwaway
  project), but **no** new endpoint in the product and no new third-party image.

## Part B — API

### B1. Security headers on every API response (middleware in the API, so development has them too)
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none';
frame-ancestors 'none'` (JSON and ProblemDetails only — no HTML is served; skip the OpenAPI / Swagger UI paths in
Development), `Cache-Control: no-store` on responses to authenticated requests unless the endpoint sets its own
(check the export download link and presign answers — they must not be cached), `Cross-Origin-Resource-Policy:
same-site` **only if** it does not break the cross-origin frontend (it would — Vercel and Funnel are different sites;
then omit it and say why). HSTS stays as it is. Integration tests: headers present on 200, 401, 404, 429, 500 and on
the hub negotiate response; CORS still exposes `X-Correlation-Id`.

### B2. Request body limits (review 025 q4)
- Measure the largest legitimate body: stock import (`InventoryValidators.MaxItems` = 200), order intake (its item
  limit), webhooks (64 KiB, unchanged), every other JSON endpoint. Set Kestrel's `MaxRequestBodySize` application-wide
  to that size rounded up with a stated margin (expected well under 1 MB); per-endpoint exceptions only if one is
  larger (none expected: images and exports never pass through the API).
- A too-large body → **413 ProblemDetails** with the correlation id (not a dropped connection), tested for a JSON
  endpoint and the webhook endpoint.
- Caddy API site `request_body max_size` = the same value (one source: an env variable with a default, used by both
  compose services, or a stated constant in both — choose, document). Storage site: no limit (presigned uploads are
  bounded by the API's size check at confirm and by the signature's content length where the provider enforces it —
  026c).

### B3. Binder errors without CLR type names, localised (review 024b)
- A malformed JSON body or a wrong JSON type (`"quantity": "abc"`, `"items": {}`) answers the project's 400 shape
  with a generic, localised message per field (vi / en) and the JSON path as key — never `System.Int32`,
  `Omnichannel.Application.…` or a .NET exception text. `AllowInputFormatterExceptionMessages = false` (or the
  equivalent for System.Text.Json) plus the mapping of `$`, `$.items[0].quantity` keys in `ValidationProblemResponse`
  to the camelCase keys the frontend already uses.
- Tests for both languages, for a body that is not JSON at all, for an enum value that does not exist, for a number
  out of range of `int`.

### B4. Development log forging (review 023)
The Development text log renders request paths decoded: a path with `%0A` can forge lines. Render the raw (encoded)
path in the Development template, or escape control characters. Production (compact JSON) is not affected — state it.
One test.

### B5. Login throttling — decision only (log "Task 026 plan")
Identity lockout (5 failures → 15 min per account) plus the per-address limit (10/min) stay; nothing is added. The
runbook §10 gets the trade-off: anyone who knows an address can lock that account for 15 minutes at a time; the Admin
unlocks under Users; a second Admin exists for that case (§9 "forgotten password" procedure).

## Part C — Edge and containers

### C1. Storage site headers
Caddy storage site, on responses: `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none';
sandbox` (an object is never run as a page on the storage origin, whatever its stored type). Check: image display in
the frontend, export download (`Content-Disposition` kept), presigned `PUT` with CORS preflight — all unchanged.

### C2. Container hardening (review 025 q5)
For every service of `docker-compose.prod.yml`: `security_opt: [no-new-privileges:true]`; `cap_drop: [ALL]` with the
minimum added back (expected: PostgreSQL and RabbitMQ entrypoints need `CHOWN`, `SETUID`, `SETGID`, `FOWNER`,
`DAC_OVERRIDE` or a subset — measure, do not guess); `read_only: true` with `tmpfs` for API, Workers, migrate and
Caddy (where they write: `/tmp`, Caddy's data / config dirs); PostgreSQL / RabbitMQ / SeaweedFS read-only root only if
it works without hacks, otherwise not (say which).
- **Caddy as non-root** (ports 8080 / 8081 / 8090 need no capability).
- **SeaweedFS as non-root**: a fixed UID/GID; the existing production volume needs its contents owned by it — a
  one-shot `chown -R` container on that volume (the stack's tool image, `--network none`), run in the release (F) with
  `objectstorage` stopped. Exercise exactly that on `omni026` with data in it first.
- Pin the third-party images to the digests now running in production (`postgres:17`, `rabbitmq:4-management`,
  `caddy:2.11`, SeaweedFS) **only if** the user agrees in the report's questions — not now (a floating tag is
  accepted in review 025).
- **RabbitMQ memory** (review 025): check what the broker believes its memory is (`rabbitmq-diagnostics status`,
  "Total memory available" or similar) against `mem_limit`; if it sees the host's memory, set it from the container
  limit (`total_memory_available_override_value`, or an absolute watermark) through a config file mounted read-only.
  Report before / after.

## Part D — Frontend

### D1. Content Security Policy for the static frontend
- A small Vite build plugin writes a `<meta http-equiv="Content-Security-Policy">` into `index.html` **at build only**
  (dev server untouched), from `VITE_API_BASE_URL` and a new build variable **`VITE_STORAGE_ORIGIN`** (the public
  storage origin; validated like `apiOrigin.ts`; required when `VITE_API_BASE_URL` is set, empty = same origin):
  `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: <storage>;
  connect-src 'self' <api> <wss-of-api> <storage>; font-src 'self' data:; object-src 'none'; base-uri 'self';
  form-action 'self'` — adjust to what the application really needs, measured, and justify each source.
  `style-src 'unsafe-inline'` is accepted (Ant Design's CSS-in-JS injects `<style>` elements; a static host cannot
  issue per-request nonces) — write it in the log row. `script-src` has **no** `'unsafe-inline'`, `'unsafe-eval'`.
- `vercel.json` headers for every path (in addition to the asset cache rule): `Content-Security-Policy:
  frame-ancestors 'none'` (a meta CSP cannot set it; two policies combine), `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` that disables camera, microphone,
  geolocation, payment. Vercel's HSTS on `*.vercel.app` is not duplicated — check and cite.
- A unit test of the plugin (sources from the variables, invalid origin → build error) and a `vite preview` session
  with the meta: zero CSP violations in the console for login, dashboard live (WebSocket), products with image upload
  and display, orders + payment, reports + export download, Logs page, a deep-link refresh — on `omni026` through its
  proxy (Part E).
- **USER (Part F):** `VITE_STORAGE_ORIGIN` = `https://omni.tail70815b.ts.net:8443` in Vercel → Project → Settings →
  Environment Variables → Production. Claude Code gives the exact steps and waits.

### D2. Token storage — decision only (log "Task 026 plan")
The roadmap's "revisit httpOnly-cookie auth" is closed: the frontend (`*.vercel.app`) and the API (`*.ts.net`) are
different sites, so an auth cookie would be a third-party cookie (`SameSite=None`), blocked by Safari and restricted by
other browsers. The bearer token stays in `sessionStorage`; the CSP (D1) is the mitigation against script injection.
Data Protection stays unused (review 025 q3 unchanged).

## Part E — Local verification (`omni026`, throwaway)
- Backend: build 0 warnings, full `dotnet test`; frontend typecheck / lint / test (two full runs) / `test:slow` /
  build / `measure` (report the first-load set: the plugin must not change it beyond the meta tag).
- `omni026` (own project, ports, edge subnet, env file with its own `IMAGE_TAG`, `WORKERS_REPLICAS=2`) built from
  this commit: `task-025.ps1` 51/51 (+ new steps for B1 / B2 / C1 headers and 413 if they fit there — or in
  `task-026.ps1`), the A3 header proof, the C2 SeaweedFS ownership change on a volume that already holds data
  (upload before, change, image still served after), a restart of every service (`restart` + health), the D1 browser
  session against `omni026` with a frontend built with its origins. `docker inspect` per service: user, caps,
  `no-new-privileges`, read-only root — table in the report.
- `--profile migrate down -v` of `omni026` only; its images removed **by their tag** (`drill-…`), production's untouched.

## Part F — First release to production (runbook §4, as amended)
1. **USER:** set `VITE_STORAGE_ORIGIN` in Vercel (D1); confirm.
2. `docker/.env.prod`: add `IMAGE_TAG=<short commit>` (and the body-limit variable if B2 chose one) — only these lines.
3. `GIT_COMMIT`, `--profile migrate build`, `migrate` (expected: nothing pending), stop `objectstorage`, the C2
   one-shot `chown` of `omnichannel-prod_objectstorage-data`, `up -d --wait`, `ps`, health.
4. Push the commit to `production`; wait for Vercel's deployment.
5. `task-025b.ps1` live (extended with the new header checks: API B1, storage C1, Vercel D1 headers and the meta CSP
   in the HTML) → all green. `task-025.ps1` is **not** run on production.
6. **USER:** on the live site with the browser's console open: login, dashboard, upload a product image and see it,
   download an export, open an order — report any CSP message (Claude Code reads the proxy log for the same requests).
7. Remove the old `omnichannel-*:local` images by name once production runs the new tag.
Report the downtime of step 3 (from `stop` to healthy).

## Acceptance criteria
- [ ] A1 `IMAGE_TAG` in compose, env template, generator, runbook (release and tag rollback); production on a commit tag.
- [ ] A2 runbook and script comment without backup steps; the no-backup consequences stated once.
- [ ] A3 `Tailscale-User-*` removed before the API and from the access log (shown locally).
- [ ] B1 headers with integration tests; B2 measured limit, 413 ProblemDetails, proxy limit equal; B3 binder errors
      without type names in vi / en; B4 log forging closed in Development; B5 recorded.
- [ ] C1 storage headers with uploads / downloads unchanged; C2 every service hardened as far as it works (table),
      Caddy and SeaweedFS non-root, RabbitMQ memory limit seen by the broker.
- [ ] D1 meta CSP + `vercel.json` headers, plugin tests, zero CSP violations in the local browser session and in the
      user's live check; D2 recorded.
- [ ] Backend and frontend suites green; CI green on GitHub for the release commit.
- [ ] Release done by the runbook; `task-025b.ps1` (extended) green live; old `:local` images removed by name.
- [ ] Report: "Development steps (applied)" — none expected; "Production steps (applied)" — each step of F with its
      command (no secret) and result; questions: digest pinning of third-party images (C2).

## Out of scope (026b / 026c / later)
Indexes, load test of the reserve path, `createdByEmail` / dashboard-summary indexes, purge of old `ReportExports`,
stale non-terminal task detection, product filter on processing tasks (026b); image orphan sweep, confirmed image
copied to a final key, signed `Content-Length`, application-wide `validateMessages`, accessibility pass (026c);
backups; uptime monitoring; Oracle VM (025c); a custom domain.

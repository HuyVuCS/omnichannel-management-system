# Report 026 — Security hardening: image tags per release, API and storage headers, body limits, container hardening, frontend CSP; first release

Status: Done — Parts A to E as below; **Part F (the release to production) done on 2026-10-13** as task 026b Part 0
("Production steps (applied)"). One step is the user's: the live browser check F6 ("Manual steps for the user").
Commit: `c8f31d7` (task), `0fc116b` (Cowork's review 025d / task 026 / log, committed first). Pushed to `master`;
CI run 38278736237 green (backend, frontend, three images).

## Summary
Images are `omnichannel-*:${IMAGE_TAG}` with the tag in the stack's env file (release = short commit, rollback = the
previous tag, no rebuild). The API answers every request with `nosniff`, `Referrer-Policy`, a `default-src 'none'`
policy and `no-store`; bodies are limited to 128 KiB with a 413 problem document; binder errors carry the field's key
and a localised text without .NET names; the request log holds the encoded path. The proxy strips `Tailscale-User-*`
and adds `nosniff` + a sandbox policy on the storage site. Every container runs with `no-new-privileges` and
`cap_drop: ALL` plus measured add-backs; Caddy, SeaweedFS and RabbitMQ run as non-root on read-only roots; RabbitMQ
sees its 1 GiB limit. The frontend build writes a CSP `<meta>` from `VITE_API_BASE_URL` / `VITE_STORAGE_ORIGIN`, and
`vercel.json` sends the header-only parts. Released to production on 2026-10-13 (commit `0d7abc5`, image tag
`0d7abc5f5f16`).

## Files changed
- `docker/docker-compose.prod.yml` — `IMAGE_TAG`; `x-hardening` on every service; users, capabilities, read-only roots, tmpfs; RabbitMQ memory file; Caddy's state in memory
- `docker/docker-compose.prod.tls.yml` — the two certificate volumes live here now (TLS mode only)
- `docker/proxy/Caddyfile` — `Tailscale-User-*` removed (request and log), `max_size 128KiB`, storage `header_down`
- `docker/rabbitmq/20-memory.conf`, `.gitattributes` — the broker's memory = the container limit; LF for that file
- `docker/prod.env.example`, `scripts/deploy/new-prod-env.ps1` — `IMAGE_TAG` (`-ImageTag`, default `local-<8 random>`)
- `scripts/deploy/set-image-tag.ps1` (new) — sets the one line, prints previous → new
- `scripts/deploy/rotate-secret.ps1` — header comment without "Backup first"
- `backend/.../Middleware/SecurityHeadersMiddleware.cs`, `RequestBodyLimit.cs`, `RequestLogProperties.cs` (new); `GlobalExceptionHandler.cs`, `Program.cs`, `Filters/ValidationProblemResponse.cs`
- `backend/.../Common/Validation/ValidationMessages.cs`, `Resources/ValidationMessages(.vi).resx` — `ValueInvalid`, `BodyNotJson`
- `backend/.../Tests/Integration/SecurityHardeningTests.cs`, `Tests/Middleware/SecurityHardeningUnitTests.cs` (new); three older tests updated to the new binder contract
- `frontend/vite/contentSecurityPolicy.ts` (+ test), `src/shared/api/origin.ts`, `apiOrigin.ts`, `vite.config.ts`, `tsconfig.node.json`, `vercel.json`
- `scripts/smoke/_hardening.ps1`, `task-026.ps1` (new); `task-025.ps1` (+2 steps), `task-025b.ps1` (+14 steps, needs `SMOKE_STORAGE_ORIGIN`)
- `docs/runbook.md` (no-backup statement, release / rollback by tag, login throttling, section 15 "Containers, limits and headers"), `README.md`

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` | 0 errors, 0 warnings |
| `dotnet test` | 1825 passed, 0 failed (3 m 8 s) |
| `npm run typecheck`, `npm run lint` | clean |
| `npx vitest run` (twice) | 518 / 518, 518 / 518 |
| `npm run test:slow` | 518 / 518 |
| `npm run build` + `npm run measure` | first-load set 970.27 → 970.29 kB raw, 318.20 → 318.22 kB gzip (9 chunks; the meta tag is in `index.html`, outside the set) |
| GitHub Actions for `c8f31d7` | success: Backend, Frontend, Image api / workers / migrate |
| `task-025.ps1` on `omni026` (2 Workers, hardened) | PASSED: all 53 checks (51 + `30b`, `41b`; frontend step skipped with `SMOKE_SKIP_FRONTEND=1`) |
| `task-026.ps1` on `omni026` | PASSED: all 9 checks |
| `task-025b.ps1` against `omni026` + `vite preview` | steps 16–26 (policy in the page, API / storage checks) green; 04, 06, 10, 13–15 red **by design** there (Vercel's cache rule, 404, TLS and headers do not exist in `vite preview`) — the script's first full run is F5 |

`omni026`: own project, ports 18080 / 18081, subnet 172.30.251.0/28, `IMAGE_TAG=drill-omni026`, `WORKERS_REPLICAS=2`,
built from the working tree. From empty: build, `migrate` (14 migrations), `up -d --wait` healthy in 15 s.
Removed with `--profile migrate down -v` of that project only, its three images by tag; production's `:local` images
and containers untouched (`docker ps`: all seven `omnichannel-prod-*` up, unchanged).

**C2 — `docker inspect` per service (omni026)**

| Service | User (PID 1) | Configured user | cap_add | cap_drop | no-new-privileges | Read-only root | tmpfs |
| --- | --- | --- | --- | --- | --- | --- | --- |
| api | 1654 | 1654 (image) | — | ALL | yes | yes | /tmp |
| workers (x2) | 1654 | 1654 (image) | — | ALL | yes | yes | /tmp |
| migrate | 1654 | 1654 (image) | — | ALL | yes | yes | /tmp (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`) |
| proxy | 65532 | 65532:65532 | NET_BIND_SERVICE | ALL | yes | yes | /data, /config |
| objectstorage | 1000 | 1000:1000 | — | ALL | yes | yes | /tmp |
| rabbitmq | 999 | rabbitmq | — | ALL | yes | yes | /tmp |
| postgres | 999 (entrypoint: root) | (image) | CHOWN, DAC_READ_SEARCH, FOWNER, SETGID, SETUID | ALL | yes | **no** | — |

Measured, not guessed (fresh volume each time, `--cap-drop ALL` + the listed ones):
- PostgreSQL: none → exit 1 (`failed switching to 'postgres'`); CHOWN+SETUID+SETGID → runs but `chmod ... Operation not permitted` twice; + FOWNER → exit 1 (`find: Permission denied`); + DAC_READ_SEARCH → clean (DAC_OVERRIDE also works; the narrower one was kept). Read-only root not used: `docker cp` into the container (statement and dump files of the operating scripts) refuses a read-only root.
- RabbitMQ as root: needs CHOWN, SETUID, SETGID — and then the health check (`exec` as root without DAC capabilities) cannot read the node's cookie (`.erlang.cookie: eacces`). As `user: rabbitmq`: no capability, read-only root works, health check and `rabbitmqctl` work.
- Caddy as 65532 with `cap_drop: ALL`: `exec /usr/bin/caddy: operation not permitted` (the binary carries the file capability `cap_net_bind_service=ep`); with `NET_BIND_SERVICE` it runs. Not a broad capability, so not a stop condition — but it is one more than "ports need no capability" suggested.
- SeaweedFS as 1000:1000, read-only root + `/tmp`: runs, uploads and exports work (smoke 025).
- All services `restart` → healthy again in 14 s.

**RabbitMQ memory:** before — `Memory high watermark set to 2154 MiB of 3591 MiB total` (the Docker VM, above the 1 GiB
limit of the container); after — `614 MiB of 1024 MiB total` (`rabbitmq-diagnostics status`: 0.6442 gb).

**C2 — storage volume ownership, on a volume with data (omni026, 119 files after smoke 025):** image `GET` 200 before;
`chown -R 0:0` to simulate a volume written by a root start → the service restarts in a loop (`permission denied`),
image 502; then the release step — `stop objectstorage`,
`docker run --rm --network none -v omni026_objectstorage-data:/data --entrypoint chown postgres:17 -R 1000:1000 /data`
(exit 0, 0 of 119 files not owned by 1000:1000), `up -d --wait objectstorage` — storage down 13 s, image `GET` 200
with the same 70 bytes. Note: SeaweedFS 4.48's own entrypoint already hands `/data` to `seaweed` (1000:1000) and
drops privileges, so production's volume is very probably owned by 1000:1000 already and the chown of F3 is a no-op;
it stays in the release because it costs nothing (I could not look: see "Blocked").

**A3 — `Tailscale-User-*`** (temporary network `omni026-a3`: the real Caddyfile in a hardened Caddy container, and a
second `caddy:2.11` as upstream under the names `api` / `objectstorage` that answers with the headers it received):
```
API site:     login=[] name=[] pic=[] funnel=[?1] other=[kept]
storage site: login=[] name=[] pic=[] funnel=[?1] other=[kept]
lower-case:   login=[] name=[] pic=[] funnel=[] other=[kept]
proxy log: 3 access lines; mentions of Tailscale-User / the login / the name / the picture host: 0
           mentions of Tailscale-Funnel-Request (control: a header that is logged): 2
caddy adapt: "request":{"delete":["Tailscale-User-Login"]} x2, ...-Name x2, ...-Profile-Pic x2 (both sites)
```
The three headers are listed by name; no wildcard (nothing to cite).

**B2 — the limit.** Largest bodies the validators accept, serialised indented: stock import (200 lines) about 19–20 kB,
order (100 lines) 9–10 kB; a webhook may be 65,536 bytes. Limit = 2 × the largest = **131,072 bytes (128 KiB)**, a
constant in the API (`RequestBodyLimit.MaxBytes`, also Kestrel's `MaxRequestBodySize`) and in the Caddyfile
(`max_size 128KiB`); a backend test parses the Caddyfile and compares. Through the proxy: Content-Length = limit + 1
→ 413 `application/problem+json`, `request_body_too_large`, correlation id = header; exactly the limit → read (400).
Chunked (no Content-Length) through the proxy → **Caddy's** 413 with an empty body (the proxy cuts it off first).
Chunked straight to Kestrel (a container on the stack's network): `HTTP/1.1 413`, `application/problem+json`,
`"errorCode":"request_body_too_large"`, `correlationId`, the security headers.

**D1 — browser sessions** (production build with `VITE_API_BASE_URL=http://localhost:18080`,
`VITE_STORAGE_ORIGIN=http://localhost:18081`, `vite preview` on :4173, `omni026` behind its proxy; a throwaway Admin,
locked afterwards). Session 1 (policy as in the task): login through the form, dashboard "Trực tiếp" (5 hub requests
with status 101 in the proxy log), product image upload (presign 200, `PUT` to the storage 200, confirm 200) and
display, an order paid through the dialog, an inventory export and its download link (anchor to the storage origin,
`fetch` of it 200, 8,396 bytes starting with `PK`), Logs, deep-link reload of `/orders` — **0 `securitypolicyviolation`
events, console empty**. Negative control in the same page: an injected inline `<script>` did not run
(`script-src-elem`), `fetch('https://example.com/')` and an image from there were blocked — the policy is enforced.
Session 2 (the final, tighter policy): login, all 13 menu pages by full reload, a second image upload and the edit
dialog — 0 violations. Not exercised: saving the export file to disk (the browser pane needs a confirmation; the
anchor was intercepted) — the user's check F6 covers it.

Final policy (production values in place of the origins):
`default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' <storage>; connect-src 'self' <api> <ws(s) of the api> <storage>; object-src 'none'; base-uri 'self'; form-action 'self'`
— `script-src`: files of the build only; `style-src 'unsafe-inline'`: Ant Design injected 55 `<style>` elements on the
Logs page; `img-src <storage>`: presigned image links; `connect-src`: REST, the hub's WebSocket, the presigned `PUT`.
Vercel sends `Strict-Transport-Security: max-age=63072000; includeSubDomains; preload` itself (measured with
`curl -I` on the live origin), so `vercel.json` does not repeat it.

**Backup / restore against the hardened containers** ("scripts unaffected — check"): `backup-prod.ps1 -ProjectName
omni026` (14 migrations, 135 storage entries, storage down 13.1 s), `restore-prod.ps1` into `omni026r` — all 11 row
counts and the storage entries identical, healthy after 41 s, `/health` 200; `omni026r` removed with its own `down -v`.

## Deviations from the task
1. **B1 `no-store` on every answer that sets no `Cache-Control`**, not only authenticated ones: the login answer
   carries the access token and is anonymous. No endpoint sets its own today.
2. **B3 keys:** `$.items[0].stock` → `items[0].stock`; the body itself stays `$` (there is no field to name). JSON
   that breaks off is reported under the field where the parser stopped (`{"sku":` → `sku`). MVC's second error for
   the action parameter (`request: The request field is required.`) is dropped with the parser text. Three older
   tests pinned the previous shape (`$.paymentMethod` + `request`, English parser text) and were rewritten.
3. **B4:** fixed by logging the encoded path in the request line (`RequestPath`), in every environment, instead of
   changing the Development template. Production's JSON was not affected before and gets the same encoded value now.
   The framework's own "Request starting / finished" lines still hold the decoded path; they are off in every
   configuration of the API (`Microsoft.AspNetCore: Warning`) — only the test host switches them on.
4. **D1 policy tighter than the draft:** no `data:` / `blob:` in `img-src` and no `font-src` line — the build has no
   `data:` image, no `createObjectURL`, no font file (grep of `dist`), and session 2 ran clean without them.
5. **C2 RabbitMQ runs as `user: rabbitmq`** (no capability) instead of root with add-backs — see the measurement.
   `docker compose exec rabbitmq ...` now runs as `rabbitmq` (runbook 9's `rabbitmqctl` row works unchanged).
6. **Caddy's `/data` and `/config` are tmpfs**; the volumes `proxy-data` / `proxy-config` moved to the TLS override
   (validated with `docker compose config` only). After F the two existing production volumes are unused; they are
   not removed (runbook 15 names the command for the user).
7. **`scripts/deploy/set-image-tag.ps1`** is new and not named in the task: it changes the one line without the file
   being opened by anyone, and prints previous → new. Generator default: `local-<8 random characters>`.
8. **`vite preview` against the development stack** now needs `VITE_STORAGE_ORIGIN=http://127.0.0.1:8333` at build
   time, or images are blocked (README "Frontend"). "Empty = same origin" as specified.
9. `migrate` needed `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp` (read-only root). The API now logs three DataProtection
   warnings instead of two (in-memory key ring; runbook 2).

## Blocked / questions for Cowork
1. **Part F needs the user twice.** (a) F1: `VITE_STORAGE_ORIGIN` in Vercel. (b) This session's permission layer
   refused even read-only commands against `omnichannel-prod` containers ("Production Reads"), so the production
   steps F2–F7 need the user's explicit go-ahead in the chat (or the user runs them from runbook 4 / 15).
2. **Digest pinning (C2):** pin the third-party images to what runs now? `postgres@sha256:d74eeac9…`,
   `rabbitmq@sha256:ddc75301…`, `caddy@sha256:f2a1290d…`, `chrislusf/seaweedfs@sha256:4e61d15f…` (the local image
   store, which production uses). Not done.
3. A 500 answer has no `X-Correlation-Id` **header** (the exception handler clears the headers; the body carries the
   id). Older than this task; a one-line fix (`OnStarting` in the correlation middleware) if wanted.
4. Follow-up candidate: `user: postgres` would need no capability at all, but changes the user of every
   `docker exec postgres ...` in the backup / restore / rotation scripts (file clean-up in `/tmp`) — a drill of its own.

## Development steps (applied)
None: no migration, no change of `docker/docker-compose.yml`, no storage key. The development containers were not
touched. No host of mine is running (the two `vite preview` servers and both throwaway stacks are gone).

## Production steps (applied)
Run on 2026-10-13 as task 026b Part 0, after the user's answers in the chat (F1: `VITE_STORAGE_ORIGIN` is set in
Vercel; go-ahead for F2–F7). Released commit: `0d7abc5` (`master` = `origin/master` before any 026b commit; the code
is `c8f31d7`, the later commits are documents). `$stack` as in the runbook.

**Before the release: Docker's engine was down** (not caused by this task). `docker` answered "Docker Desktop is
unable to start"; Docker Desktop's log: the WSL distribution failed to bootstrap at 15:06 while Windows was
upgrading WSL ("WSL is finishing an upgrade...", then "WSL update required"), and the engine stayed stopped — so
production was off from 14:57 local time (start of Docker Desktop) at the latest until the restart at about 15:35. With WSL
answering again (3.0.3) `docker desktop restart` brought the engine back (no reset, nothing purged): all eight
volumes present, every container of `omnichannel-prod` and of development healthy by itself.

| # | Step (command, no secret) | Result |
| --- | --- | --- |
| F1 | USER: `VITE_STORAGE_ORIGIN` in Vercel (Production) | confirmed in the chat; the deployed page's policy names the storage origin (step 16 below) |
| 0 | `git log production..origin/master`, `git diff --stat ... Migrations` | 11 commits (`431c876` … `0d7abc5`), no migration file |
| F2 | `scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod` | `IMAGE_TAG: (not set) -> 0d7abc5f5f16`; no other line (no body-limit variable exists) |
| F3 | `$env:GIT_COMMIT = git rev-parse HEAD`; `docker compose @stack --profile migrate build` | exit 0 in 8 s (layers cached from the `omni026` drill of the same code); three images `:0d7abc5f5f16`, label `org.opencontainers.image.revision` = `0d7abc5f…` |
| F3 | `Get-AppliedMigrations` against the checkout's files (runbook 5) | 14 applied = the 14 of the checkout, nothing pending |
| F3 | `docker compose @stack --profile migrate run --rm migrate` | exit 0, "No migrations were applied. The database is already up to date." — **it recreated `postgres` first** (hardened definition; started 15:35:36), so the old API lost its database about 23 s before the `stop` below |
| F3 | `docker compose @stack stop objectstorage` (15:35:59) | exit 0 |
| F3 | `docker run --rm --network none -v omnichannel-prod_objectstorage-data:/data --entrypoint chown postgres:17 -R 1000:1000 /data` | exit 0; the volume already was `1000:1000` before (`stat`), so nothing changed |
| F3 | `docker compose @stack up -d --wait` | exit 0, every service healthy at 15:36:38; `ps`: `api` / `workers` x2 on `:0d7abc5f5f16`, restart count 0 everywhere |
| F3 | `/health` on `https://omni.tail70815b.ts.net` and on `127.0.0.1:8080`; `tailscale funnel status` | 200 / 200; both Funnel ports on |
| F4 | `git push origin 0d7abc5…:refs/heads/production` | `264270c..0d7abc5`; Vercel served the new build (page with the CSP `<meta>`) within a minute |
| F5 | `scripts\smoke\task-025b.ps1` live (`SMOKE_*` of runbook 13, `SMOKE_EXPECT_HSTS=true`) | **PASSED: all 26 checks** (first full run: 13–17 static host headers and the page's policy, 18–25 API headers and the 413, 26 storage headers) |
| F7 | `docker image rm omnichannel-api:local omnichannel-workers:local omnichannel-migrate:local` | removed; left: the three `:0d7abc5f5f16` |

**Downtime:** 39 s from `stop objectstorage` to every service healthy (15:35:59 → 15:36:38); counted from the
recreation of `postgres` by the migrate step, about 62 s (15:35:36 → 15:36:38). For the next release: the first
`compose run` after a changed service definition recreates that service — runbook step 3 is where the downtime starts
in that case.

API and Workers log after the start: the three expected DataProtection warnings (runbook 2), the line
`libgssapi_krb5.so.2: cannot open shared object file` once per host (Npgsql probing for Kerberos, also printed by
`migrate`; harmless, seen on the drills), a first reconciliation run with 0 pairs; no error.

Rollback of this release is no longer the `:local` images (removed in F7): `git checkout 8fc1e5e`,
`set-image-tag.ps1`, `build`, `up -d --wait` (runbook 4, "the images of that tag were removed").

## Manual steps for the user
1. ~~`VITE_STORAGE_ORIGIN` in Vercel~~ — done by the user (2026-10-13).
2. ~~Go-ahead for Part F~~ — given in the chat (2026-10-13).
3. **Open — F6:** on the live site with the browser console open — login, dashboard, upload a product image and see
   it, download an export (the file must really be saved), open an order — and report any message that mentions
   "Content Security Policy". (Claude Code does not log in to production: the Admin password is only with the user.)
4. Optional: the volumes `omnichannel-prod_proxy-data` / `_proxy-config` are unused now (runbook 15).

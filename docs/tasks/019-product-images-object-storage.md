# Task 019 — Product images in S3-compatible object storage; Part A (review 018b)

Status: Open
Written by: Cowork, 2026-10-03
Depends on: 018b (accepted)

## Context
Doc §4.2 / §14 / §16 ("Ảnh sản phẩm lưu S3"), §11 `POST /api/products/{id}/image/presign`, CLAUDE.md §15. Images are
never stored in PostgreSQL: the database keeps the object key and metadata, the object lives in a **private** bucket,
upload and download go through presigned URLs.

**Decision (user, 2026-10-03): no AWS** (cost; deployment will be free — Vercel / Railway, decided in 025). Storage is
any **S3-compatible** service reached through the S3 API; development and the integration tests use a **local
emulator container**. MinIO no longer publishes community images and LocalStack requires an auth token since 03/2026,
so the emulator is **Garage** or **SeaweedFS**, chosen by the spike in Part B. Every decision of this task is in the
implementation log, "Task 019 plan" — read it first; doc deviations 19–21 are listed there.

Read first: implementation log "Task 019 plan", "Task 018b result", "Steps 1–3" (options, secrets, audit interceptor),
"Task 014 plan" (no external call inside a transaction; Polly); review 018b; backend `ProductsController`,
`IProductService` / `ProductContracts.cs` / `ProductValidators.cs`, `Persistence/Products/ProductService.cs`,
`Product.cs` + its configuration, `DependencyInjection.cs` (options + `ValidateOnStart`), `RabbitMqOptions` (+ its
validator), `ChannelService` (a service-level `ValidationException` on one field), `Integration/ApiFactory.cs`;
`docker/docker-compose.yml`, `docker/.env.example`; frontend `features/products/*` (`ProductsPage.tsx` column widths,
`ProductFormModal.tsx`, `productsApi.ts`), `shared/api/baseApi.ts`, `src/test/server.ts`, `src/test/pendingRequests.ts`;
`scripts/smoke/_common.ps1`, `task-018b.ps1`; for A1 `orderEntry.test.tsx` (`renderLive`, "outcome by push"); for A2
`OrderMessageHandler` / `OrderMessageParser`, `StockSyncMessageHandler` / `StockSyncMessageParser`,
`RecordRejectedMessageAsync` (both services), `SystemEventDetails`, `features/logs/eventSummary.ts`.

**Stop and report (`Blocked`)** if: neither emulator meets the spike criteria; anything needs a cloud account or a real
provider; the image would have to change the product API beyond the `image` field and the three endpoints below; a
cleanup job, lifecycle rule, thumbnail generation or image processing seems necessary; or a decision is needed that is
not in this file or the log.

## Part A — Follow-ups of review 018b (separate commits, first)

### A1. Timing-independent order-entry tests; latency switch committed (P3, review 018b question 3)
Cause found in the review: `renderLive` returns before the refetch started by the connect resync (`RESYNC_TAGS` after
every (re)connect) is answered; with slow answers `watchLists` counts it.
- Commit the diagnostic of 018b as an **opt-in**: an environment variable read by the test setup that delays every
  answer by N ms (default: off, no effect), and an npm script `test:slow` that runs the suite with 120 ms. README
  "Frontend" names it and when to use it (any task that adds tests with requests).
- `renderLive` ends when the connect resync's requests are answered (`settled()` or the equivalent); fix the three
  "outcome by push" list-count tests' baseline that way, not by changing the expected counts.
- `npm run test:slow` (full suite) **green**; any other test it turns red is fixed the same way and listed (file, test,
  what it now waits for). No `testTimeout` change.

### A2. `reasonCode` of rejected messages (P3, review 018b question 5)
- One constant per rejection path of the order and stock-sync handlers/parsers (invalid JSON, invalid envelope,
  unexpected event type, invalid payload, unknown task, … — derive the set from the code; one code per distinct
  `RejectAsync` / reject call site or parser outcome, no catch-all except for a genuinely generic path). Codes are
  snake_case, in one `…RejectionCodes` class per worker area (or one shared class if the paths overlap) in Application.
- Both writers store `reasonCode` next to the existing `reason` (unchanged, still never returned).
  `RecordRejectedMessageAsync` takes the code.
- Allowlist: `reasonCode` added to `order.message_rejected` and `stock_sync.message_rejected`; README table updated.
- Logs page: the summary of both types shows the translated label of the code; an unknown code its raw value; an old
  row without it "—". vi/en parity.
- Tests: unit — every rejection path writes its code (table in the report: path → code); integration — the endpoint
  returns `reasonCode` and never `reason`; frontend — summary with a known, an unknown and a missing code (vi and en).

## Part B — Emulator spike and infrastructure

### B1. Spike: Garage vs SeaweedFS (result table in the report)
Evaluate both against these criteria with `AWSSDK.S3` (add it through Central Package Management; current stable
version, pinned) and record the evidence (command, status code) per criterion:

| # | Criterion |
| --- | --- |
| 1 | Free, OSI open-source licence; official container image with a **pinned version tag**; a release within the last 12 months |
| 2 | Presigned **PUT** and **GET** from the SDK work; a changed signature, an expired URL and a PUT with a `Content-Type` other than the signed one are rejected (4xx) |
| 3 | Bucket is private: an unsigned GET of an object is rejected |
| 4 | Bucket CORS can be set (S3 `PutBucketCors` or the emulator's own configuration) and a browser preflight from `http://localhost:5173` for `PUT` with `Content-Type` is answered with that origin |
| 5 | `HeadObject` (length, content type), ranged `GetObject`, `DeleteObject` |
| 6 | Single node in docker compose with an **idempotent scripted init** (bucket, access key from `docker/.env`, CORS), healthcheck, data volume, port bound to `127.0.0.1` |
| 7 | Startable from the integration tests (Testcontainers generic container or an existing module — check NuGet, do not guess) with the same init |

Pick the one that meets all criteria with fewer setup steps; if both fail any, stop (`Blocked`). If the SDK's default
request checksums are rejected by the emulator, configure the SDK to send/validate checksums only when required (verify
the option names in the installed SDK version) and say so — the production provider will need the same setting.

### B2. Infrastructure
- `docker/docker-compose.yml`: the emulator service (dev only), pinned image, `127.0.0.1` port, volume, healthcheck;
  init as a one-shot service or a script run from the repository root — idempotent, never prints a secret.
- `docker/.env.example`: the new variables (access key id, secret, bucket; format notes if the emulator imposes one).
- A helper the **user** runs once (`scripts/…ps1`, PowerShell 5.1, ASCII) that generates a key pair in the emulator's
  format and writes it to `docker/.env` (only missing entries) and to the user-secrets of `Omnichannel.Api`
  (`ObjectStorage:AccessKeyId`, `ObjectStorage:SecretAccessKey`) without printing it. Claude Code does not run it on
  the developer's real files.
- Options `ObjectStorage` (Infrastructure): `ServiceUrl`, `Region`, `ForcePathStyle`, `BucketName`, `AccessKeyId`,
  `SecretAccessKey` (secret: never in `appsettings*.json`), `UploadUrlLifetime` (5 min), `DownloadUrlLifetime` (15 min),
  `MaxImageBytes` (5 MiB), request timeout. Validator + `ValidateOnStart` (presence and ranges only; no connection at
  start-up). Development defaults in `appsettings.Development.json` (endpoint, bucket — no keys).
- The Workers host does not get storage (020 will).

## Part C — Backend

### C1. Model and migration `ProductImage`
`Product`: `ImageKey` (varchar, length from the key format), `ImageContentType` (varchar 32), `ImageSizeBytes`
(bigint), `ImageUpdatedAt` (timestamptz); all four null or all four set (check constraint); domain methods to set and
clear the image (no public setters). No index. Migration generated, not applied by Claude Code to development.

### C2. `IObjectStorage` (Application abstraction) + S3 implementation (Infrastructure)
Small interface, only what this task needs: presign PUT (key, content type, lifetime), presign GET (key, lifetime),
head (length, content type, or "missing"), read the first N bytes, delete (missing = success). Presigning is local (no
network call). Storage failures surface as one exception type mapped to **503** ProblemDetails `storage_unavailable`
(no storage detail, no URL, no key id in the message or the log). Presigned URLs and keys/secrets are never logged.

### C3. Endpoints (`ProductsController`, Manager/Admin for all three)
- `POST /api/products/{id}/image/presign` — body `{ contentType, sizeBytes }`; `contentType` ∈ `image/jpeg`,
  `image/png`, `image/webp`; `1 ≤ sizeBytes ≤ MaxImageBytes` (localised messages, en/vi). 404 for an unknown product.
  Answer `{ objectKey, uploadUrl, method: "PUT", headers: { "Content-Type": … }, expiresAt }`; key
  `products/{productId}/{uuidv7}` generated by the server. Nothing is written to the database.
- `PUT /api/products/{id}/image` — body `{ objectKey }`. Checks, in this order, outside any transaction: key format and
  prefix of **this** product (400 on `objectKey`); HEAD: missing → 400 "not uploaded"; length > max → 400 and delete;
  content type not allowed → 400 and delete; first bytes are not the magic bytes of the stored content type → 400 and
  delete. The 400s are field errors on `objectKey` through the existing service-level `ValidationException`
  mechanism (as `ChannelService`), localised. Then one transaction: lock the product row, remember the previous key,
  set the image; commit; then delete the previous object (best effort, warning without the key's secret parts on
  failure). Same key as the current one → 200 without any delete (client retry). Answer: the `ProductResponse`.
- `DELETE /api/products/{id}/image` — clears the image in a transaction, deletes the object after commit; no image →
  204 as well.
- `ProductResponse` gets `image`: `{ url, expiresAt, contentType, sizeBytes, updatedAt }` or null, on list, get, create
  and update; `url` presigned GET with `DownloadUrlLifetime`. Listing products makes **no** storage call.
- `.http` samples; README section "Product images" (flow, roles, limits, configuration, CORS, how to switch provider,
  the user's setup steps, orphans).

### Backend tests
Integration (against the emulator in a container; the other integration tests keep working without it — configuration
only): presign → PUT (HTTP client, as a browser would, without `Authorization`) → confirm → GET by `image.url` returns
the same bytes; PNG, JPEG, WebP; each 400 of presign and confirm (en and vi messages) and that the object is deleted
where stated; key of another product; HEAD missing; replace deletes the previous object; same key twice; delete
(with and without an image); expired upload URL (lifetime of 1–2 s in that test); unsigned GET rejected; Staff 403 on
all three, anonymous 401; product list with images while the emulator is stopped/unreachable still answers 200 (no
storage call); storage unreachable on confirm → 503 `storage_unavailable` and the product unchanged; concurrent
confirms of two keys for one product → one image stored, the other object deleted or orphaned (say which), no 500.
Unit: magic-byte detection (each type, truncated input, mismatches), key format/prefix, options validator.
Existing tests green without modification (except those that assert the full `ProductResponse` shape — list them).

## Part D — Frontend
- `productsApi`: `presignProductImage`, `confirmProductImage`, `removeProductImage`; confirm/remove invalidate the
  product and the product list as `updateProduct` does.
- The PUT to storage uses plain `fetch` in its own small module — **not** `baseQuery`: no `Authorization`, no
  correlation header, only the headers the presign answer names. A test asserts that no `Authorization` header is sent.
- **Product dialog, edit mode, Manager/Admin:** preview (or placeholder), "choose image" (`accept` of the three
  types), client-side type/size check first (message, no request — the server stays the authority), then presign →
  PUT → confirm with one busy state; "remove image" with confirmation; errors: API ProblemDetails through the existing
  mapping (field errors of `objectKey` shown at the image block), storage PUT failure → one localised message with the
  status. Create mode: a short note that the image is added after the product is saved. Keyboard accessible.
- **Products list:** thumbnail column (40 × 40, `object-fit: cover`, `alt` = product name, lazy loading, placeholder
  when null or on load error); the table still fits 1280 px in vi and en (column-width test).
- **Stable URL:** the client reuses the URL it has for the same image (`productId` + `updatedAt`) until 60 s before its
  `expiresAt`, so list refetches do not change `src` (tests: refetch with a new URL, same version → same `src`; new
  version → new `src`; near expiry → new `src`).
- Staff: thumbnails, no upload/remove controls.
- MSW handlers for a fixed storage origin in `src/test`; the A2 guard covers these requests too. No new dependency;
  i18n parity.

### Frontend tests
Upload happy path (presign → PUT → confirm, list refreshed); client-side rejection (type, size) without requests;
presign 400, PUT 403 (expired), confirm 400 (`objectKey` field), 503; remove with confirmation; Staff view; stable URL
rules; thumbnail placeholder (null, load error); column widths at 1280 px; language switch keeps state. `npm run
test:slow` green including the new tests.

## Acceptance criteria
- [ ] A1, A2 as listed, separate commits first; the A2 path → code table in the report.
- [ ] Spike table (both emulators, every criterion, evidence) and the choice with its reason.
- [ ] Backend build 0 warnings, all tests pass; frontend typecheck, lint, test, build pass; **5 consecutive full
      `npm test` runs green** (wall times) and one full `npm run test:slow` green.
- [ ] Migration `ProductImage` generated; smoke run first on a **throwaway** environment (PostgreSQL, RabbitMQ and the
      emulator with generated credentials), as 017 did; the user applies it to development afterwards.
- [ ] No secret, key id, presigned URL or connection detail in logs, ProblemDetails, the repository or the report.
- [ ] Bundle: first-load set before/after (the upload code is part of the lazy products chunk).
- [ ] `scripts/smoke/task-019.ps1` (strict `Assert-Step`, A5 table format; API in Development; the emulator must be up —
      a clear failed step if it is not): smoke Manager and Staff; product; Staff presign → 403; presign with
      `image/svg+xml` → 400 and with a size above the limit → 400; presign → PUT a small PNG built by the script → 2xx;
      confirm → 200 with `image`; GET `image.url` → the same bytes; the object without signature → rejected; PUT a text
      file declared as PNG + confirm → 400 and its object gone; replace with a JPEG → the old download URL now 404;
      remove → `image` null, URL 404; key of another product → 400; lock smoke users; `Invoke-FrontendChecks`.
      Table in the report.
- [ ] Real-browser check on the production build (1280 px, vi and en): Manager uploads, replaces and removes an image;
      the list shows the thumbnail; the PUT request to storage carries no `Authorization` header (network panel);
      Staff sees thumbnails without controls.
- [ ] README "Product images"; `docker/.env.example`; doc deviations 19–21 confirmed or corrected in the report.
- [ ] "Manual steps for the user" in the report: the key helper, `docker compose up -d`, migration — nothing else.

## Out of scope
More than one image per product, image processing/thumbnails/resizing, a public bucket or CDN, an orphan sweep or
lifecycle rule (026), production provider and hosting (025), Excel files in storage (020), images in the order-entry
product picker, a system event or realtime hint for image changes, virus scanning.

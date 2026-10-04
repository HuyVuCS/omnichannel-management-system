# Report 019 — Product images in S3-compatible object storage; Part A (review 018b)

Status: Done
Commit: `451b8cd` (A1), `426b8d9` (A2), `281108c` (Parts B and C: API, emulator service, migration, key helper),
`35608d6` (Part D, smoke script, README), plus the commit of this report. Cowork's documents of this round are in
`1b85ce2`.

## Summary
- **A1:** `npm run test:slow` runs the suite with every answer 120 ms late (`TEST_ANSWER_DELAY_MS`, off by default).
  `renderLive` now ends on an idle cache, so the three "outcome by push" counts are the same with and without latency;
  two more tests that the delay turned red were fixed the same way. Full slow run green.
- **A2:** every way a worker rejects a message has a `reasonCode` (19 codes, one shared class); both writers store it
  next to `reason`; the Logs endpoint returns it and never `reason`; the Logs page shows its label.
- **Spike:** Garage and SeaweedFS both meet criteria 1–5 and 7. **SeaweedFS** was chosen: bucket, key and CORS are
  start-up settings of its container, so criterion 6 needs no config file, no extra secret and no init step.
- **Backend:** `IObjectStorage` + S3 implementation, `ObjectStorage` options, image columns on `Product` (migration
  `ProductImage`), presign / confirm / remove endpoints, `image` on every product response. Listing products makes no
  storage call; a storage failure is a 503 `storage_unavailable` that changes nothing.
- **Frontend:** thumbnail column for every role, image block in the edit dialog (Manager/Admin), upload by a plain
  `fetch` without the API's token, one link per image version.
- Backend build 0 warnings, **1056 / 1056** tests. Frontend typecheck, lint, build clean, **405 tests** (375 + 30) in 29 files,
  **5 of 5 consecutive full runs green** (288 / 273 / 275 / 268 / 267 s); `npm run test:slow` green (405, 285 s). First-load set 969.88 → 970.14 kB raw (317.98 → 318.10 kB gzip). Smoke script **50 of 50 on a throwaway environment**. Real-browser
  check on the production build done; it found one defect the tests could not (below).

## Files changed
Part A1
- `frontend/src/test/pendingRequests.ts` — opt-in answer delay (real time, also under a fake clock).
- `frontend/scripts/test-slow.mjs` (new), `frontend/package.json` — `npm run test:slow`.
- `frontend/src/test/interactions.ts` — `settled(store)`: also no query or mutation pending.
- `frontend/src/features/orders/orderEntry.test.tsx` — `renderLive` ends with `settled(app.store)`.
- `frontend/src/features/auth/auth.test.tsx`, `features/inventory/stockImport.test.tsx` — end settled.
- `README.md` — "Frontend" names the script and when to use it.

Part A2
- `Omnichannel.Application/Common/Messaging/MessageRejection.cs` (new) — `MessageRejection(Code, Reason)`,
  `MessageRejectionCodes`.
- `Omnichannel.Workers/OrderWorker/OrderMessageParser.cs`, `OrderMessageHandler.cs`,
  `StockSyncWorker/StockSyncMessageParser.cs`, `StockSyncMessageHandler.cs` — every rejection carries its code.
- `IOrderReservationService`, `IStockSyncService`, `OrderReservationService`, `StockSyncService` —
  `RecordRejectedMessageAsync` takes the rejection and stores `reasonCode` next to `reason`.
- `Omnichannel.Application/SystemEvents/SystemEventDetails.cs` — `reasonCode` in both allowlist entries.
- Tests: `Workers/MessageRejectionTests.cs` (new, 35), `Integration/SystemEventEndpointsTests.cs`,
  `Integration/Workers/OrderWorkerTests.cs`, `StockSyncWorkerTests.cs`, `FailureInjection.cs`,
  `Workers/OrderMessageParserTests.cs`, `StockSyncWorkerUnitTests.cs`.
- Frontend: `features/logs/eventSummary.ts`, `logs.test.tsx`, `shared/i18n/vi.json`, `en.json`, `i18n.test.ts`,
  `test/systemEvents.ts`. `README.md` — allowlist table and the code table.

Parts B and C
- `backend/Directory.Packages.props`, `Omnichannel.Infrastructure.csproj` — `AWSSDK.S3` 4.0.104.1 (pinned).
- `docker/docker-compose.yml` — service `objectstorage` (SeaweedFS 4.48), volume, healthcheck, `127.0.0.1:8333`;
  storage settings of the `api` profile. `docker/.env.example` — three variables.
- `scripts/setup-object-storage.ps1` (new) — the key helper.
- `Omnichannel.Domain/Entities/Product.cs` — image fields, `SetImage`, `ClearImage`.
- `Omnichannel.Infrastructure/Persistence/Configurations/ProductConfiguration.cs`, migration
  `20261004074312_ProductImage`, model snapshot.
- `Omnichannel.Application/Common/Abstractions/IObjectStorage.cs` (new), `Common/Exceptions/ObjectStorageException.cs`
  (new).
- `Omnichannel.Application/Products/ProductImages.cs` (new: contracts, `ProductImageRules`, `ProductImageKey`),
  `IProductImageService.cs` (new), `ProductImageValidators.cs` (new), `ProductContracts.cs` (`Image`).
- `Omnichannel.Application/Resources/ValidationMessages(.vi).resx`, `FieldNames(.vi).resx`, `ValidationMessages.cs`.
- `Omnichannel.Infrastructure/S3/` (new): `ObjectStorageOptions.cs` (+ validator), `S3ObjectStorage.cs`
  (+ `UnconfiguredObjectStorage`), `ObjectStorageServiceCollectionExtensions.cs`.
- `Omnichannel.Infrastructure/Persistence/Products/ProductImageService.cs` (new, + `ProductImageLinks`),
  `ProductService.cs`, `DependencyInjection.cs`.
- `Omnichannel.Api/Controllers/ProductsController.cs`, `Middleware/GlobalExceptionHandler.cs`, `Program.cs`,
  `appsettings.Development.json`, `Omnichannel.Api.http`.
- Tests: `Integration/ProductImageEndpointsTests.cs` (new, 44), `Integration/StorageEmulator.cs` (new),
  `Integration/ApiFactory.cs`, `Products/ProductImageUnitTests.cs` (new).

Part D
- `frontend/src/features/products/productsApi.ts` — `image`, three mutations, stable link in `transformResponse`.
- `features/products/imageUpload.ts` (new) — the `PUT` to the storage and the client-side file check;
  `imageLinks.ts` (new) — one link per image version.
- `features/products/components/ProductThumbnail.tsx` (new), `ProductImageField.tsx` (new), `ProductFormModal.tsx`,
  `ProductsPage.tsx`.
- `app/store.ts` — links are forgotten when the session ends.
- `shared/i18n/vi.json`, `en.json`.
- Tests: `features/products/productImages.test.tsx` (new, 30), `products.test.tsx`, `test/storage.ts` (new),
  `test/catalog.ts`.
- `scripts/smoke/task-019.ps1` (new), `README.md` ("Product images", setup, frontend notes).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build` (final state of the backend) | 0 errors, 0 warnings |
| `dotnet test` after A2 | 927 passed, 0 failed (1 m 42 s) |
| `dotnet test` (final state of the backend) | **1056 passed**, 0 failed (1 m 40 s) |
| `dotnet ef migrations add ProductImage` | generated (`20261004074312_ProductImage`); **not** applied to development |
| `npm run test:slow -- orderEntry.test.tsx -t "outcome by push"` with the `renderLive` fix taken out | 3 failed (counts 3 instead of 2, 2 instead of 1, 3 instead of 2) — the defect of review 018b, reproduced |
| the same with the fix | 27 passed |
| `npm run test:slow` (full), run 1 | 1 failed: `auth.test.tsx` "logs out when the access token expires…" (request in flight) — fixed |
| `npm run test:slow` (full), run 2 | 1 failed: `stockImport.test.tsx` "pre-fills the first line…" (request in flight) — fixed |
| `npm run test:slow` (full), run 3, with A2's frontend | 375 passed (28 files, 320 s) |
| `npm run typecheck`, `npm run lint` (final state) | clean |
| `npm test` × 5 consecutive (final state, nothing else running) | **5 of 5 green**, 405 passed each (29 files); 288 / 273 / 275 / 268 / 267 s |
| `npm run test:slow` (final state) | 405 passed (29 files), 285 s |
| `npm run build`, `npm run measure` (final state) | built; first-load set 970.14 kB raw / 318.10 kB gzip; main chunk 170.96 / 55.22 kB; `ProductsPage` 11.47 kB / 4.59 kB gzip |
| Spike (console program, `AWSSDK.S3` 4.0.104.1) | Garage 19 / 19 with two settings, SeaweedFS 19 / 19 with none (table below) |
| `scripts\setup-object-storage.ps1` on a throwaway env file and a throwaway user-secrets id | three runs: entries missing → added; again → nothing changes; copy of `.env.example` → placeholders replaced, other lines kept; user-secrets equal the env file; no value printed |
| `scripts\smoke\task-019.ps1` on a **throwaway** environment | **PASSED: all 50 checks** (first run), incl. `npm run build` and `npm test` |
| Log of the throwaway API (JSON, environment `Smoke`), scanned after the browser check while the smoke script was running | 96 lines (95 Information, 1 Warning), 0 with `X-Amz-`, the key id, the secret, the storage endpoint or the bucket name; the emulator's own log (251 lines) has 0 lines with a signature |
| Real-browser check, production build, 1280 px, vi and en | done (below) |

### A2 — path → code
`MessageRejectionTests` sends each case through the handler twice: as a normal delivery (`HandleAsync`) and as the last
delivery the queue allows (`RecordFinalFailureAsync` before any attempt) — both call sites write the same code.

| Rejection path | Code | Order | Stock sync |
| --- | --- | --- | --- |
| Body is not JSON / not an envelope | `invalid_json` | ✓ | ✓ |
| Body is `null` | `empty_body` | ✓ | ✓ |
| Event type the queue does not handle | `unexpected_event_type` | ✓ | ✓ |
| Envelope without `taskId` (`order.created`, `stock.sync.requested`) | `missing_task_id` | ✓ | ✓ |
| Envelope without `messageId` or `correlationId` | `missing_message_ids` | ✓ | ✓ |
| `messageId` or `correlationId` longer than 100 | `message_ids_too_long` | | ✓ |
| Payload is not a JSON object | `payload_not_object` | ✓ | ✓ |
| Payload cannot be read as its type | `invalid_payload` | ✓ | ✓ (both event types) |
| Payload deserialises to nothing | `empty_payload` | (unreachable) | (unreachable) |
| Payload and envelope name different tasks | `task_id_mismatch` | ✓ | ✓ |
| Order without channel, customer or external id | `missing_order_fields` | ✓ | |
| Order without items | `no_items` | ✓ | |
| Item without product / quantity ≤ 0 / negative price | `invalid_item` | ✓ | |
| Same product on two items | `duplicate_product` | ✓ | |
| Inventory change without product | `missing_product_id` | | ✓ |
| Stock-sync request without channel or product | `missing_sync_target` | | ✓ |
| Task does not exist (database) | `unknown_task` | ✓ | ✓ |
| Task is not a stock-sync task of that product and channel | `task_mismatch` | | ✓ (3 cases) |
| Task of another channel than the queue | `task_of_other_channel` | | ✓ |

`empty_payload`: both parsers keep the check, but a payload that passed "is a JSON object" never deserialises to
null, so no message produces it; the completeness test names it as the one exception. The two worker integration
tests assert the stored code per message (order: `invalid_json`, `unknown_task`; stock sync: `invalid_json`,
`unexpected_event_type`, `unknown_task`, `task_mismatch`, `task_of_other_channel`).

### B1 — spike (2026-10-04)
Both emulators in containers on `127.0.0.1`, credentials generated per run; a console program with `AWSSDK.S3`
4.0.104.1 for the S3 calls and a plain `HttpClient` as "the browser". Not committed.

| # | Criterion | Garage `dxflrs/garage:v2.4.1` | SeaweedFS `chrislusf/seaweedfs:4.48` |
| --- | --- | --- | --- |
| 1 | Licence, pinned image, release < 12 months | AGPL-3.0 (OSI); tag `v2.4.1`, 2026-09-08; 99 MB | Apache-2.0; tag `4.48`, 2026-09-28; 724 MB |
| 2 | Presigned PUT | 200 | 200 |
| 2 | Presigned GET, same bytes | 200, 2008 bytes, `image/png` | 200, 2008 bytes, `image/png` |
| 2 | Changed signature | 403 | 403 |
| 2 | Expired link (2 s lifetime, used after 4 s), GET and PUT | 400, 400 | 403, 403 |
| 2 | PUT with another `Content-Type` than signed | 403, object not stored | 403, object not stored |
| 3 | Unsigned GET / PUT | 403 / 403 | 403 / 403 |
| 4 | Bucket CORS | `PutBucketCors` 200 (twice) | `PutBucketCors` 200 (twice); **or** the start flag `-s3.allowedOrigins`, no call |
| 4 | Preflight from `http://localhost:5173` for `PUT` + `content-type` | **403 with the rule `AllowedHeaders: ["Content-Type"]`**; 200 with `content-type` or `*` (header names are compared case-sensitively) | 200, `Allow-Origin` = the origin (both ways) |
| 4 | Preflight from another origin | 403 | 403, no `Allow-Origin` |
| 5 | `HeadObject` | 200, length 2008, `image/png`; missing → 404 | the same |
| 5 | Ranged `GetObject` (bytes 0–15) | 206, 16 bytes | 206, 16 bytes |
| 5 | `DeleteObject` (existing, missing) | 204, then HEAD 404; 204 | the same |
| 6 | Single node, scripted idempotent init | config file mount + `GARAGE_RPC_SECRET` (a fourth secret) + `--single-node --default-bucket` (bucket and key from env) + **one S3 call for CORS** (needs a client: an extra init container or script); image has no shell (healthcheck `garage status`); key format imposed (`GK` + 24 hex, 64 hex); restart OK | `weed mini -bucket=… -s3.allowedOrigins=…` + key pair from env: **no config file, no init step**; `wget …/healthz` healthcheck; any key format; restart with the same volume OK (17 / 17 again) |
| 7 | Testcontainers | generic container (no module on NuGet) | generic container (no official module; a third-party `SeaweedFs.Testcontainers` 0.1.0 exists, not used) |
| – | SDK `PutObject` with the SDK's default request checksums | **400 InvalidRequest**; 200 with `RequestChecksumCalculation = WHEN_REQUIRED` | 200 |
| – | Memory idle | 40 MiB | 71–80 MiB |

**Choice: SeaweedFS** — it meets every criterion with fewer setup steps (three container arguments and two
environment variables against a config file, an extra secret and a CORS call), and the SDK works with its defaults.
Garage's advantages are size and memory. What Garage taught is in the README ("Switching the provider"): a provider
may need `WHEN_REQUIRED` checksums and a lower-case `content-type` in its CORS rule. The application sends no object
through the SDK (only presign, HEAD, ranged GET, DELETE), so the checksum setting is left at the SDK default.
Side finding: Garage writes the presigned URL of every refused request, signature included, to its container log;
SeaweedFS's log of the whole smoke and browser run has no line with a signature.

### Backend tests (Part C)
`ProductImageEndpointsTests` (44, against the emulator; the "browser" is an `HttpClient` without `Authorization`):
presign → PUT → confirm → `image.url` returns the same bytes, for PNG, JPEG and WebP; `image` on list, get, update
and for Staff; presign 400s (type, empty type, size 0 / negative / above the limit — en and vi), 404, nothing written;
the storage rejects another `Content-Type` and an expired upload link (lifetime 2 s); unsigned GET, tampered link and
the API's bearer token rejected; preflight answered for the frontend origin only; confirm 400s on `objectKey`
(eleven malformed keys and the key of another product; not uploaded; too large, type not allowed, text as PNG, PNG as
JPEG, truncated, empty — each with the object deleted; en and vi); exactly 5 MiB accepted; replace deletes the
previous object and its old link is 404; same key twice; remove with and without an image; Staff 403 and anonymous
401 on all three; **storage stopped**: list, get and presign answer 200, a confirmation and a replacement answer 503
`storage_unavailable` with nothing of the storage in the body and nothing changed, a repeated confirmation of the
current image still 200, a removal 204 (row cleared, object left as an orphan), and both uploads confirm after the
storage is back; nothing of the storage in the API's log.
Concurrent confirmations of three uploads for one product: every request 200, the last one is the image, **the other
two objects are deleted** (not orphaned) — the product row lock runs them one after the other.
Unit (`ProductImageUnitTests`): magic bytes of each type, shortest and truncated input, 13 mismatches; key form (23
malformed keys, another product's key); the entity's all-or-nothing image; options validator (defaults, every missing
setting, 16 out-of-range cases, the limits themselves; no message repeats a value).
**Existing backend tests:** none asserts the whole `ProductResponse`, so none changed for Part C. Changed for A2 only:
the two parser tests (the reason is now `rejection.Reason`), the detail cases of `SystemEventEndpointsTests` (stored
payloads now carry `reasonCode`; the "never `reason`" assertion looks for the JSON name `"reason"` and for the text),
the two worker poison tests (assert the stored code), and the test double `FailureInjection`.

### Frontend tests (Part D)
`productImages.test.tsx` (30): thumbnail (src, `alt`, lazy, 40 × 40, cover) and placeholder for no image and for a
load error (a new link is tried again); Staff sees thumbnails and no controls; one link per image (refetch with a new
link → same `src` and the same element; new version → new `src`; near expiry → new `src`; the rule itself as a unit
test); upload happy path — presign, `PUT` to the storage origin with only `Content-Type` and **no `Authorization`,
no `X-Correlation-Id`**, confirm, list refreshed; one busy state; client-side rejection (type ×3, size, empty) without
a request; exactly 5 MiB and each type accepted; presign 400; storage 403 (message with the status, nothing
confirmed, retry works); storage unreachable; confirm 400 on `objectKey` in the block; 503 reported globally with the
reference code; 403; remove (cancel, confirm, nothing to remove, refused); create dialog note; keyboard; the dialog
starts from the row's product at every opening; language switch changes text only.
**Existing frontend test changed:** `products.test.tsx` "fits the content area of a 1280 px window" — 9 and 8 columns
instead of 8 and 7 (the sum of the widths is still ≤ 976).

### Bundle
| | first-load set raw | gzip | `ProductsPage` chunk |
| --- | --- | --- | --- |
| before (018b report) | 969.88 kB | 317.98 kB | not recorded |
| after | 970.14 kB (+0.26) | 318.10 kB (+0.12) | 11.47 kB / 4.59 kB gzip |

The upload code (`imageUpload.ts`, `ProductImageField`, `ProductThumbnail`) is in the lazy `ProductsPage` chunk. The
first-load set grew by the link cache (`imageLinks.ts`, imported by the store to forget links at logout). The
language chunks are loaded separately and grew by the rejection labels and the image texts.

### Smoke script (throwaway environment)
PostgreSQL 17 (55432) and RabbitMQ 4 (55672) as in 017; the emulator through **the compose file's own service**
(`docker compose -p omni019 … up -d objectstorage`, env file = a copy of `.env.example` filled by the key helper):
healthy within 15 s, port `127.0.0.1:8333` only, volume created; all nine migrations applied with `dotnet ef database
update` as `omnichannel_migrator`; API as `omnichannel_app` in environment `Smoke` on port 5080. Everything was removed
afterwards (containers, volume, env file, throwaway user-secrets).

```
OK    Step                                                             Expected                           Actual
--    ----                                                             --------                           ------
True  01 create smoke manager                                          201                                201
True  02 create smoke staff                                            201                                201
True  03 create smoke product                                          201                                201
True  04 ... it has no image                                           no image                           no image
True  05 create a second smoke product                                 201                                201
True  06 staff: presign is forbidden                                   403                                403
True  07 ... confirm too                                               403                                403
True  08 ... and remove                                                403                                403
True  09 presign image/svg+xml -> 400                                  400                                400
True  10 ... on contentType                                            The image must be a JPEG, PNG or   The image must be a JPEG, PNG or
                                                                       WebP file.                         WebP file.
True  11 presign above the size limit -> 400                           400                                400
True  12 ... on sizeBytes                                              The image must not be larger than  The image must not be larger than
                                                                       5 MB.                              5 MB.
True  13 presign a PNG -> 200                                          200                                200
True  14 ... method PUT, key under products/{productId}/               True                               True
True  15 ... the only header to send is Content-Type image/png         image/png                          image/png
True  16 nothing is stored on the product by a presign                 no image                           no image
True  17 the object storage answers (is the emulator up? docker        True                               True
      compose up -d)
True  18 PUT the PNG to the storage with the link -> 2xx               True                               True
True  19 confirm -> 200                                                200                                200
True  20 ... image: PNG of the uploaded size                           image/png 70                       image/png 70
True  21 GET image.url -> 200                                          200                                200
True  22 ... the same bytes                                            True                               True
True  23 the same object without the signature is rejected             403                                403
True  24 staff reads the product with its image                        image/png                          image/png
True  25 ... and its link returns the image                            True                               True
True  26 the product list carries the image                            image/png                          image/png
True  27 PUT a text file declared as PNG -> 2xx                        True                               True
True  28 confirm it -> 400                                             400                                400
True  29 ... on objectKey: not the image it claims to be               The content of the file is not an  The content of the file is not an
                                                                       image of the type it was uploaded  image of the type it was uploaded
                                                                       as.                                as.
True  30 its object is gone: confirming again finds no upload          No file has been uploaded for      No file has been uploaded for
                                                                       this key.                          this key.
True  31 the product still has its PNG                                 2026-10-04T08:13:35.756961+00:00   2026-10-04T08:13:35.756961+00:00
True  32 upload for the second product -> 2xx                          True                               True
True  33 confirming it on the first product -> 400                     400                                400
True  34 ... on objectKey                                              This is not an upload key of this  This is not an upload key of this
                                                                       product.                           product.
True  35 ... and it is still the second product's to confirm           200                                200
True  36 upload a JPEG -> 2xx                                          True                               True
True  37 confirm -> 200, the image is the JPEG                         200 image/jpeg 212                 200 image/jpeg 212
True  38 the link of the replaced PNG -> 404                           404                                404
True  39 the new link returns the JPEG                                 True                               True
True  40 confirming the current image again -> 200, nothing changes    200                                200
                                                                       2026-10-04T08:13:36.166464+00:00   2026-10-04T08:13:36.166464+00:00
True  41 ... and its object is still there                             200                                200
True  42 remove the image -> 204                                       204                                204
True  43 ... the product has no image                                  no image                           no image
True  44 ... its link -> 404                                           404                                404
True  45 remove again (no image) -> 204                                204                                204
True  46 the smoke products end without an image                       no image                           no image
True  47 lock the smoke manager                                        200                                200
True  48 lock the smoke staff                                          200                                200
True  50 npm run build                                                 0                                  0
True  51 npm test                                                      0                                  0

PASSED: all 50 checks.
```
(Step 49 is `npm ci`, which only runs when packages are missing.)

### Real-browser check (production build + `vite preview`, 1280 × 800, throwaway environment)
Signed in by session injection (no password typed); files were created in the page (canvas → `File`) and handed to the
dialog's file input, because the pane cannot drive the operating system's file dialog. Everything else is the real
path: the real preflight and `PUT` from `http://localhost:4173` to the emulator on `http://127.0.0.1:8333`.

| Check | Result |
| --- | --- |
| Manager, vi: products table with the thumbnail column | 9 columns, table 992 / 992 px (no horizontal scroll), no header truncated |
| Edit dialog | image block first: placeholder "Chưa có ảnh", "Chọn ảnh", "Xoá ảnh" disabled, hint |
| Upload a PNG | "Đã lưu ảnh."; calls: presign (API) → `PUT` storage → confirm (API) → list; preview and row show the image (320 × 200 natural, 96 × 96 and 40 × 40 shown, `object-fit: cover`); row and dialog use the same link (one image request) |
| **The `PUT` to the storage** | headers set by the page: **only `content-type`**, `credentials: omit`; network panel: `OPTIONS` 200, `PUT` 200; no `Authorization`, no `X-Correlation-Id` (the API calls carry both) |
| Replace with a JPEG | new image in dialog and row; the old link answers 404 |
| A text file named `.png` | the API's message in the block: "The content of the file is not an image of the type it was uploaded as."; placeholder stays |
| An SVG | the client's message, no request |
| WebP | uploaded and shown |
| Remove | confirmation "Remove the image of this product?" with a red "Remove image"; no request before it; then `DELETE`, placeholder in dialog and row, old link 404 |
| Language switch with the dialog open | text only: same field and image nodes, same rectangles, buttons 136 px both ways; table 992 / 992 in en, no header truncated |
| Staff, vi | thumbnail and placeholder, 8 columns, no edit button, no create button, no file input |
| **Defect found:** the native file input was visible in the dialog ("Choose File / No file chosen") | the UI library's reset has `.ant-form input[type="file"] { display: block }`, which beats the `hidden` attribute. jsdom applies no such stylesheet, so the test `not.toBeVisible()` passed. Fixed (`style={{ display: 'none' }}`), rebuilt, checked again: `display: none`, 0 × 0, the button still opens the picker |

Not done in the browser: a pass with real key presses (focus and activation of the two buttons were checked through
the DOM; the unit test covers Tab / Enter), and choosing a file through the operating system's dialog.

## Design notes
- **Key:** `products/{productId}/{uuidv7}`, both GUIDs lower-case with dashes: 82 characters = the column length. A
  key from a client is accepted only in exactly that form (`ProductImageKey.TryParse`).
- **Confirm:** product exists (404) → key form and prefix → *same key as the current image → 200 at once, without
  asking the storage* → HEAD → size → content type → first 12 bytes → transaction (`SELECT … FOR UPDATE` on the
  product row, set the image) → after the commit delete the previous object. No storage call inside the transaction.
- **Presign size limit** is configuration, so it is checked by the service (400 on `sizeBytes`); the validator checks
  the type and `sizeBytes >= 1`.
- **Storage failures:** `S3ObjectStorage` turns SDK, HTTP, I/O and timeout exceptions into `ObjectStorageException`
  (the operation name only) and logs one warning with the operation and `HTTP <status> <S3 code>` or the exception
  type — never the exception, whose text can carry the endpoint. The handler answers 503 `storage_unavailable` and
  logs a warning, not "Unhandled exception". One SDK retry (`MaxErrorRetry = 1`), `RequestTimeout` per try.
- **Hosts without storage:** `AddInfrastructure` registers `UnconfiguredObjectStorage` (throws on use);
  `AddObjectStorage` (API only) replaces it. `ProductService` is used by a Workers-fixture test, so it must be
  resolvable there: products without an image never touch the storage.
- **Links:** `ProductImageLinks` presigns per response; the projection carries the four columns, the link is added
  after the query.
- **Client:** the link in use is kept per product and `updatedAt` in a module map applied in `transformResponse` of
  every endpoint that returns a product; cleared when the session ends.

## Deviations from the task
1. **A1: `settled(store)`** — "requests answered" alone is not enough: RTK Query starts the refetches of a delayed
   invalidation in the dispatch that ends the last pending request, so `renderLive` also waits until no query or
   mutation is pending. `settled()` without a store is unchanged.
2. **A1: the delay is real time** (the timer is taken before any fake clock), so tests that jump a fake clock still
   get slow answers. Two more tests were red under it and fixed the same way (listed above). They were red in
   different runs: the slow run is far more sensitive than the normal one, but not perfectly deterministic.
3. **A2: one shared class** (`MessageRejectionCodes`), since 10 of the 19 paths are common to both workers; the
   parsers return a `MessageRejection` instead of a string. `empty_payload` exists but no message reaches it.
4. **No init service and no init script** for the emulator: SeaweedFS takes bucket, key and CORS as start-up settings.
   CORS by the start flag allows `GET, PUT, POST, DELETE, HEAD` from the two origins (every request still needs a
   signature).
5. **`sizeBytes` upper bound in the service**, not in the FluentValidation validator (it is configuration; validators
   are created without dependencies by the existing reflection tests).
6. **404 before the key check, and "same key" before the storage checks** in confirm (a client retry works while the
   storage is down).
7. **Integration tests:** the emulator is started by the first image test, on a host port chosen before the API host
   is built (the single shared host only needs configuration); "unreachable" = the container is stopped and started
   again; the expired-link test changes `UploadUrlLifetime` on the live options object for one presign.
8. **Smoke environment `Smoke`, not Development** (as 017: no user-secrets of the developer); its frontend checks ran
   next to the API, which the script does not own there.
9. **Product table widths:** SKU 124 → 116, name 180 → 144, price 118 → 112, created 144 → 136 to make room for the
   58 px thumbnail column; checked in the browser in both languages.
10. **Upload reads the file into memory** (`file.arrayBuffer()`, at most 5 MiB) instead of streaming the `File`: the
    same code then runs in the tests, where the test environment's `File` is not a body Node's `fetch` accepts.
11. **Remove confirmation** is `modal.confirm` (short yes/no; its texts stay as opened — the known limitation).
12. **`docker compose --profile app`:** the containerised API gets the storage settings, with the internal address
    (question 1).
13. **Browser check** as described: files created in the page, no real key presses.
14. **Spike program not committed**; the `.http` upload sample refers to a local `sample.png` the user supplies.

## Doc deviations 19–21
19. **Confirmed.** S3-compatible storage through the S3 API; `AWSSDK.S3` as client library; emulator SeaweedFS 4.48
    in development and in the integration tests.
20. **Confirmed.** `PUT /api/products/{id}/image` and `DELETE /api/products/{id}/image` next to the doc's presign.
21. **Confirmed.** `ImageKey` varchar(82), `ImageContentType` varchar(32), `ImageSizeBytes` bigint, `ImageUpdatedAt`
    timestamptz on `Products`, check constraint `CK_Products_Image_AllOrNone`, no index.

## Blocked / questions for Cowork
1. **Address of the storage for browsers vs. for the API.** One `ServiceUrl` serves both today. A containerised or
   deployed API may reach the storage under another name than browsers do; then links need a second setting (a public
   endpoint used only for presigning). Decide in 025?
2. **Client-side size limit** is the constant 5 MiB; `MaxImageBytes` is configuration. If the limit is ever changed,
   the client's early message and hint are wrong until the constant follows (the server stays right). Expose the limit
   (e.g. in the presign 400 only, as today) or accept?
3. **Slow run sensitivity** (deviation 2): two tests were red in one of three slow runs each. Should a task run
   `test:slow` more than once, or is one green run the rule?
4. **Orphans from a removal while the storage is down** are accepted by the plan (026 sweep). The warning names the
   product and the object key (not secret); is the key in the log acceptable?
5. **Network panel:** the storage `PUT` shows `200 OK` and, in this pane, a "cancelled" mark afterwards — the response
   has no body and the code does not read one. Harmless as far as I can tell (the confirmation found the object every
   time); mention it if the user sees it.

## Manual steps for the user
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` (keys into `docker/.env` and the API's
   user-secrets; prints no key).
2. `docker compose -f docker/docker-compose.yml --env-file docker/.env up -d` (starts the emulator; first start pulls
   `chrislusf/seaweedfs:4.48`, 724 MB).
3. Migration `ProductImage` on development, as the migrator:
   `cd backend; dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api`.

Then, optionally, `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-019.ps1` on development.

## Out of scope / follow-ups
- 025: production provider, public endpoint of the storage (question 1), CORS for the production origin.
- 026: orphan sweep or lifecycle rule; CSP (`img-src`, `connect-src` for the storage origin).
- 020: object storage for the Workers host (Excel files).
- The integration tests now pull a third image (724 MB) on a machine that runs them for the first time.

# Review 019 — Product images in S3-compatible object storage; Part A (review 018b)

Reviewer: Cowork, 2026-10-05
Verdict: **Accepted** — no P0–P2 finding. Four P3 follow-ups: two go into 020 Part A (backend/documentation), one into
021 Part A (frontend), one into 026 (hardening).

## Checked
- Report and commits `451b8cd` (A1), `426b8d9` (A2), `281108c` (Parts B and C), `1b85ce2` (Cowork's documents),
  `35608d6` (Part D, smoke, README), `b5ebe79` (report). 80 files outside `docs/`, +5979 / −149. One migration
  (`20261004074312_ProductImage`), one new package (`AWSSDK.S3` 4.0.104.1, pinned through Central Package Management).
- Read in full: `ProductImageService.cs` (+ `ProductImageLinks`), `S3ObjectStorage.cs` (+ `UnconfiguredObjectStorage`),
  `ObjectStorageOptions.cs` (+ validator), `ObjectStorageServiceCollectionExtensions.cs`, `IObjectStorage.cs`,
  `ProductImages.cs` (`ProductImageRules`, `ProductImageKey`), `ProductImageValidators.cs`, `MessageRejection.cs`, the
  migration, `scripts/setup-object-storage.ps1`, frontend `imageUpload.ts`, `imageLinks.ts`, `ProductThumbnail.tsx`,
  `ProductImageField.tsx`, README "Product images"; diffs of `ProductService.cs`, `ProductsController.cs`,
  `GlobalExceptionHandler.cs`, `Product.cs`, `ProductConfiguration.cs`, `DependencyInjection.cs`, `Program.cs`,
  `docker-compose.yml`, `.env.example`, `SystemEventDetails.cs`, `OrderReservationService.cs`, `productsApi.ts`,
  `store.ts`.
- Checked by Cowork against the code:
  - **No storage call inside a transaction.** Confirm: product read (no lock) → key form/prefix → same key → HEAD →
    size → type → ranged GET → *then* `BEGIN` → `SELECT … FOR UPDATE` → set → `COMMIT` → delete of the previous object.
    Remove: transaction → commit → delete. The only calls in a transaction are SQL.
  - **Concurrency.** Two confirmations for one product serialise on the row lock; each deletes the key it replaced, so
    the losers' objects are deleted, not orphaned (as the report says). A repeated confirmation of the key that just
    won reads `previousKey == key` under the lock and changes nothing. A remove racing a confirm ends in a consistent
    row (last writer wins); no path deletes the object the row points to.
  - **Key handling.** `ProductImageKey.TryParse` accepts only `products/{guid}/{guid}` in canonical lower-case form,
    rejects `Guid.Empty`; the prefix check is by parsed id, not by string prefix, so `products/{id}x/...` or `../` cannot
    pass. The column length (82) equals the generated form.
  - **Magic bytes.** JPEG `FF D8 FF`, PNG 8-byte signature, WebP `RIFF????WEBP` (length guarded before slicing). A short
    object returns fewer bytes (`ReadAtLeastAsync(…, throwOnEndOfStream: false)`); an empty object's 416 is mapped to
    "no bytes" → mismatch → 400 and delete.
  - **Secrets and links.** `S3ObjectStorage.Failed` logs operation + `HTTP <status> <S3 code>` or the exception type,
    never the exception; the global handler logs a warning with the operation only. The options validator never echoes
    a value. Keys are only in user-secrets / `docker/.env` / environment; the setup script passes them to
    `dotnet user-secrets` on stdin, not on a command line, and prints none. The client sends the upload with
    `credentials: 'omit'`, `referrerPolicy: 'no-referrer'` and only the signed headers; links are forgotten at logout.
  - **Listing makes no storage call.** `ProductImageLinks.For` presigns locally; the projection carries the four
    columns and the link is added after the query.
  - **Hosts without storage.** `TryAddSingleton<IObjectStorage, UnconfiguredObjectStorage>` in `AddInfrastructure`,
    replaced by `AddObjectStorage` in the API only; the Workers host can still resolve `ProductService` for products
    without an image (020 gives the Workers host the real storage, which ends this special case).
  - **A2.** 19 constants in one shared class with an `All` list; both writers store `reasonCode` next to `reason`; the
    allowlist adds only `reasonCode`. `empty_payload` is unreachable but kept with the check — fine, named in the test.
  - **A1.** `settled(store)` waits for no pending query or mutation in addition to answered requests; the explanation
    (RTK Query starts the delayed invalidation's refetch in the dispatch that ends the last request) matches the
    library's `invalidationBehavior: 'delayed'`.
- **Verification** (not re-run by Cowork): backend build 0 warnings, 1056/1056; frontend typecheck, lint, build clean,
  405 tests, 5 of 5 full runs green (288 / 273 / 275 / 268 / 267 s), `npm run test:slow` green (285 s); the A1 defect
  reproduced with the fix taken out (3 failed) and gone with it; smoke 50/50 on a throwaway environment (compose
  service of the repository, migrator role, `omnichannel_app`); log scan (0 lines with a signature, key id, secret,
  endpoint or bucket); production-build browser check at 1280 px in vi/en (found and fixed the visible native file
  input). First-load set +0.26 kB raw.
- **Spike.** Both emulators met criteria 1–5 and 7; SeaweedFS needs no config file, no fourth secret and no init call
  (criterion 6), and works with the SDK's default checksums. Choice accepted. The Garage findings (case-sensitive CORS
  header names, `WHEN_REQUIRED` checksums, presigned URLs written to its own log) are exactly what the production
  provider must be checked for in 025 — they are in the README.
- **Doc deviations 19–21 confirmed** (S3-compatible storage instead of AWS S3; confirm and remove endpoints; image
  columns on `Products`).

## Deviations
| # | Deviation | Decision |
| --- | --- | --- |
| 1 | `settled(store)` also waits for pending queries/mutations | Accepted — measured cause, documented in the helper. |
| 2 | The answer delay is real time, also under a fake clock | Accepted; see question 3 for the rule. |
| 3 | One shared `MessageRejectionCodes` class; parsers return `MessageRejection` | Accepted (10 of 19 paths are shared). |
| 4 | No init service/script: SeaweedFS takes bucket, key and CORS as start-up settings | Accepted; CORS allows `GET, PUT, POST, DELETE, HEAD` from the dev origins — every request still needs a signature. |
| 5 | `sizeBytes` upper bound checked in the service | Accepted (configuration; validators have no dependencies). |
| 6 | 404 before the key check; "same key" before the storage checks | Accepted — a client retry works while the storage is down. |
| 7 | Emulator started by the first image test; "unreachable" = stopped container; lifetime changed on the live options for one test | Accepted. |
| 8 | Smoke in environment `Smoke`, not Development | Accepted (as 017). |
| 9 | Product table widths reduced for the thumbnail column | Accepted; checked in the browser in both languages. |
| 10 | Upload body read into memory (≤ 5 MiB) | Accepted. |
| 11 | Remove confirmation with `modal.confirm` | Accepted (known limitation: texts stay as opened). |
| 12 | `docker compose --profile app`: the containerised API signs links for `objectstorage:8333` | Accepted for development; the public endpoint is decided in 025 (question 1). |
| 13 | Browser check with files created in the page, no real key presses | Accepted; the real file dialog and keys stay an optional manual step. |
| 14 | Spike program not committed; `.http` sample needs a local `sample.png` | Accepted. |

## Questions of the report
1. **Storage address for browsers vs. for the API.** Agreed that it is a deployment concern → **025**. Design to use then:
   an optional `ObjectStorage:PublicServiceUrl`; when it is set, *presigning* uses a second S3 client built for that
   endpoint, while HEAD / GET / PUT / DELETE from the hosts keep `ServiceUrl`. A signed URL cannot be rewritten
   afterwards (SigV4 signs the host). Until then images work only with the API run from the IDE (README says so). The
   export downloads of 020 are presigned by the API with the same client, so they inherit the same rule — no extra
   work in 020.
2. **Client size limit is a constant.** **Accept.** The server stays the authority and its presign 400 names the
   configured limit; a *lower* configured limit therefore costs one request and shows the server's message. Only a
   *higher* limit would be blocked by the client — so changing `MaxImageBytes` must change `MAX_IMAGE_BYTES` too:
   the README says so (020 A1). Revisit if the limit ever differs per deployment.
3. **Slow-run sensitivity.** **One green full `npm run test:slow` run is the rule**, but a red slow run is a finding,
   never noise: every test that is red in any slow run is fixed (or shown to be an application defect) before the
   final green run, and every red run is listed in the report with the test names. The stability measure remains the
   five consecutive normal runs. Recorded in the log, "Task 019 result".
4. **Object key in the warning of a failed delete.** **Acceptable.** The key is not a credential (the bucket is
   private; without a signature it reads nothing), contains no personal data, and is exactly what an operator needs to
   find and remove the orphan. It stays out of ProblemDetails and responses; the link is never logged.
5. **"Cancelled" mark after the storage `PUT`.** **No action.** The response is a 200 without a body that the code does
   not read; the confirmation found the object every time. If the user sees `(canceled)` in a real Chrome DevTools
   network panel, it is worth one look then.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P3 | **Idle products page shows broken thumbnails after the link lifetime.** No query refetches on focus or by time (`tabVisibility.ts`), and a link is reused until 60 s before it expires. On a products page left open longer than 15 minutes without a stock change, thumbnails that load later (`loading="lazy"` rows scrolled into view, the preview of an edit dialog opened later) request an expired link and show the "image unavailable" placeholder until something refetches the list. Self-heals on any refetch or navigation; no data is wrong. | **021 Part A** (next frontend task): refetch the product list (or the one product) when a thumbnail fails while its `expiresAt` has passed, or when the earliest `expiresAt` on the visible page is reached — one refetch, not a loop. |
| P3 | **CORS of the emulator allows only `http://localhost:5173` / `:4173`.** A developer who opens the dev server as `http://127.0.0.1:5173` (the project's own rule for local connections) gets a refused preflight and cannot upload. | **020 Part A2.** |
| P3 | **README "Switching the provider" misses one permission.** On AWS-compatible services a `HEAD` of a missing object is answered **403** (not 404) when the key may not *list* the bucket; confirm would then turn "not uploaded" into a 503. | **020 Part A1** (README: the key needs list on the bucket, or the provider check in 025 must show a 404 for a missing object). |
| P3 | **A presigned upload link stays valid after its confirmation** (5 minutes): a second `PUT` with the same link replaces the confirmed object with unchecked content of any size (the signed `Content-Type` still applies, so it is served as an image type). The stored size is then stale. Manager/Admin only — the same users can already upload unconfirmed objects of any size (presigned `PUT` cannot bound the length). | **026**: copy the object to a final key at confirmation (the checked copy is never signable for `PUT`), and evaluate a signed `Content-Length` per provider. Documented in the README now (020 A1). |

## Assessment
The task does what the plan asked and keeps every architectural rule visible in the code: the database is the only
place an image "exists", the server checks the stored object before believing it, no transaction waits on the
storage, and nothing that could be a credential reaches a log. The spike is the kind of evidence a provider decision
needs, and its side findings (Garage logging signatures, header-case CORS) were turned into README guidance for 025.
The browser check caught a defect jsdom cannot see and the report explains why — that rule (check the production build
in a real browser) keeps paying off. The four findings are edge cases of time (link lifetimes) and of deployment
(provider permissions, dev origins), not of correctness.

## Manual check
Required for the user before images work on development: the three steps in the report ("Manual steps for the user").
Optional (5 minutes): `scripts\smoke\task-019.ps1` on development, and one upload through the real file dialog with
the keyboard (Tab to "Chọn ảnh", Enter, choose a file).

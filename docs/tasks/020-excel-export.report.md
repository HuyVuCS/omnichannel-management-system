# Report 020 — Excel export in the background (ClosedXML + object storage); Part A (review 019)

Status: Done
Commit: `4f20d69` (Part A), `141c33d` (Parts B and C: backend, worker, migration, tests), `4df5baa` (smoke script, key
helper, README, `.http`), plus the commit of this report. Cowork's documents of this round are in `a9c53f3`.

## Summary
- **Part A:** README "Product images" says that the provider key needs list on the bucket, that `MAX_IMAGE_BYTES`
  must follow a raised limit, and that an upload link stays valid after its confirmation. The emulator's CORS allows
  the dev server and `vite preview` as `localhost` **and** `127.0.0.1` (compose service and tests, one constant); the
  preflight test covers all four origins.
- **Export:** every export is a background task. `POST /api/reports/{orders|inventory}/export` (Manager/Admin) stores
  task + `ReportExports` row + outbox event + audit event in one transaction and answers 202; the Report Export
  Worker (queue `report.export`) reads the rows from one read-only snapshot while it builds the workbook, uploads
  `exports/{taskId}.xlsx` after that transaction and completes export, attempt and task in one transaction. List,
  detail and download link answer for the requester only. Failures, DLQ, alert and manual retry are the existing
  machinery. A retention sweep in the Workers host deletes the files after 7 days.
- **No stop condition was hit:** the emulator stores an object from the SDK and serves it with the stored
  `Content-Disposition: attachment`; ClosedXML writes `=1+1` as text; an export at `MaxRows` needs **255 MiB / 336 MiB**
  peak working set (limit 1.5 GiB); nothing needed a cloud account.
- Backend build **0 warnings**, **1278 / 1278** tests (1056 before); **22 of 22** mutants killed; smoke script **46 of
  46** on a throwaway environment; no frontend file changed. Migration `ReportExports` generated, not applied to
  development.

## Files changed
Part A (`4f20d69`)
- `README.md` — three notes in "Product images", CORS paragraph.
- `docker/docker-compose.yml` — `-s3.allowedOrigins` with the two `127.0.0.1` origins.
- `Omnichannel.Tests/Integration/StorageEmulator.cs` (`AllowedOrigins`), `ProductImageEndpointsTests.cs` (preflight for
  all four origins; refused for another host and for an allowed host on another port).

Parts B and C (`141c33d`)
- `backend/Directory.Packages.props`, `Omnichannel.Infrastructure.csproj` — `ClosedXML` 0.105.1 (pinned).
- Domain: `Entities/ReportExport.cs` (new), `Enums/ReportType.cs` (new), `Enums/ProcessingTaskType.cs` (`ReportExport`).
- Application: `Reports/` (new): `ReportContracts.cs` (requests, responses, stored parameters, file name, time zones,
  languages, error codes, `ReportEvents`), `ReportValidators.cs`, `ReportsOptions.cs` (+ `ReportsOptionsRules`),
  `ReportTexts.cs`, `IReportExportService.cs`, `IReportExportProcessingService.cs`;
  `Resources/ReportTexts.resx` / `.vi.resx` (new, 51 texts); `ValidationMessages(.vi).resx` + keys (5),
  `FieldNames(.vi).resx` (`timeZone`, `reportType`); `Common/Abstractions/IObjectStorage.cs` (`PutAsync`);
  `Common/Exceptions/RequestRejectedException.cs` (new, 400 with an error code);
  `Common/Messaging/EventTypes.cs`, `ReportExportRequestedPayload.cs` (new), `MessageEnvelope.cs` (task id of the new
  event), `MessageRejection.cs` (comments); `SystemEvents/SystemEventDetails.cs` (two entries);
  `Orders/OrderQueryContracts.cs` (`OrderFilter`), `Inventory/InventoryContracts.cs` (`InventoryFilter`).
- Infrastructure: `Excel/ReportSheet.cs`, `Excel/ReportWorkbooks.cs` (new: the two workbooks);
  `Persistence/Reports/` (new): `ReportExportService.cs` (API side), `ReportExportProcessingService.cs` (worker side),
  `ReportExportCleanup.cs`, `ReportsOptionsValidator.cs`; `Persistence/Orders/OrderFilters.cs`,
  `Persistence/Inventories/InventoryFilters.cs` (new: the list filters, moved out of the two query services, which now
  call them); `Persistence/Configurations/ReportExportConfiguration.cs`, `OmnichannelDbContext.cs`, migration
  `20261004183420_ReportExports` + snapshot; `S3/S3ObjectStorage.cs` (`PutAsync`), `RabbitMQ/RabbitMqTopology.cs`
  (queue `report.export`), `DependencyInjection.cs`.
- API: `Controllers/ReportsController.cs` (new), `Middleware/GlobalExceptionHandler.cs` (400 for
  `RequestRejectedException`).
- Workers: `ReportExportWorker/` (new): `ReportExportMessageParser.cs`, `ReportExportMessageHandler.cs`,
  `ReportExportWorkerService.cs`, `ReportExportWorkerOptions.cs`, `ReportExportCleanupService.cs`;
  `WorkerHostSetup.cs` (`AddObjectStorage`, the worker, the sweep), `Processing/ProcessingErrorClassifier.cs`
  (`storage_unavailable`), `appsettings.Development.json` (endpoint and bucket, no keys), `Program.cs` (comment).
- Tests: `Integration/ReportExportEndToEndTests.cs` + `.Worker.cs` (12), `ReportExportEndpointsTests.cs` (20),
  `ReportExportTesting.cs` (helpers, probe), `Integration/Workers/ReportExportSnapshotTests.cs` (3 + 8),
  `Reports/ReportWorkbookTests.cs` (28), `Reports/ReportContractTests.cs` (101), `Workers/ReportExportHandlerTests.cs`
  (15); changed: see "Existing tests changed".

Scripts and documentation (`4df5baa`)
- `scripts/smoke/task-020.ps1` (new), `scripts/setup-object-storage.ps1` (Workers user-secrets, byte-order-mark fix),
  `README.md`, `docker/.env.example` (comment), `Omnichannel.Api.http` (samples).

## Verification (actually executed)
| Command | Result |
| --- | --- |
| `dotnet build --no-incremental` (final state) | 0 errors, **0 warnings** |
| `dotnet test` (final state) | **1278 passed**, 0 failed (2 m 28 s); unit only: 860 passed |
| `dotnet test --filter ProductImageEndpointsTests` after Part A | 44 passed |
| `dotnet ef migrations add ReportExports` | generated (`20261004183420_ReportExports`); **not** applied to development |
| Spike (two temporary tests, removed): SDK `PutObject` with `Content-Disposition` to the emulator, presigned GET | 200, header `attachment; filename="orders_20261005-1030.xlsx"` served from the stored object; the `response-content-disposition` override works as well (not used) |
| Spike: ClosedXML `cell.Value = "=1+1"`, `"@SUM(A1)"`, `"+1-1"`, file re-opened | all `Text`, `HasFormula = false` |
| Mutation run, 22 mutants (table below) | **22 of 22 killed**; tree restored, build ok |
| Time zones and a workbook on Windows and in `mcr.microsoft.com/dotnet/aspnet:10.0` (the image of `docker/api.Dockerfile`) | same output on both (below) |
| `scripts\setup-object-storage.ps1` on a throwaway env file and two throwaway user-secrets ids | run 1: keys added, both user-secrets set; run 2: nothing to add; both user-secrets equal the env file; no key in the output |
| `scripts\smoke\task-020.ps1` on a **throwaway** environment | **PASSED: all 46 checks** (first run) |
| the same with the throwaway emulator stopped | FAILED 2 of 21: step 17 "the object storage answers …" + the explaining error; accounts locked; exit code 1 |
| Measurement at `MaxRows` on the throwaway environment | table below |
| Log scan of the throwaway API and Workers logs | 269 lines, 0 hits (below) |

### Stop conditions
| Condition | Result |
| --- | --- |
| Emulator stores an object from the SDK and serves it as an attachment | **The stored header works**: `PutObject` with `Content-Disposition`, and the presigned GET answers with exactly that header and the stored `Content-Type`. Nothing is added to the link (`response-content-disposition` is not used). For 025: the provider must store and return `Content-Disposition` of an object; if it does not, the override on the presigned GET is the fallback (works with the emulator too). |
| ClosedXML writes a value starting with `=` as a formula | No: `Value` stores a string as text, a formula only comes from `FormulaA1` (never used). One surprise: the library reads a **leading apostrophe** as Excel's text mark and drops it; the sheet writer doubles it, so the name keeps it (tested, mutant 18). |
| An export at `MaxRows` needs more than 1.5 GiB | No: 255 MiB (orders) / 336 MiB (inventory) peak working set. |
| A cloud account | Not needed. |

### Measurement (throwaway environment, data seeded by SQL there only)
Windows 10, Debug build, Workers host in environment `Smoke`, a fresh Workers process per export, one export at a time
(`ReportExportWorker:Concurrency` 1), emulator on the same machine. Peak working set = `PeakWorkingSet64` of the
Workers process after the export (the sampled maximum agreed within 1 MiB).

| Export at `MaxRows` = 100,000 | Rows | Attempt duration | File | Workers idle | **Peak working set** |
| --- | --- | --- | --- | --- | --- |
| Orders: 25,000 orders + 75,000 item lines (two data sheets) | 100,000 | 6.1 s | 4.13 MiB (4,335,806 bytes) | 95 MiB | **255 MiB** |
| Inventory: 100,000 products (one data sheet, 10 columns) | 100,000 | 5.4 s / 5.5 s (two runs) | 5.05 MiB (5,292,749 bytes) | 95 MiB | **335 MiB / 336 MiB** |

Both files were downloaded with their link (as many bytes as `sizeBytes`, starting with `PK`). The default limit stays
100,000: about 240 MiB above idle per running export. The validator allows `MaxRows` up to 1,000,000 (one sheet holds
1,048,575 data rows); by these numbers that would be roughly 2.5 GiB per export - a deployment that raises the limit
must size the Workers host for it (README).

### Smoke script (throwaway environment)
PostgreSQL 17 (55432) and RabbitMQ 4 (55672) as in 017; the emulator through **the compose service of the repository**
(`docker compose -p omni020 -f docker/docker-compose.yml -f <override> … up -d objectstorage`; the override only moves
the host port to 58333 with `ports: !override`, because 8333 is the development emulator, which was running); env file
= a copy of `.env.example` filled by the key helper. All 10 migrations applied with `dotnet ef database update` as
`omnichannel_migrator` (`omnichannel_app` on `ReportExports`: `DELETE, INSERT, SELECT, UPDATE` through the default
privileges); API and Workers as `omnichannel_app` in environment `Smoke`, API on port 5081. Everything was removed
afterwards (containers, volume, env file, throwaway user-secrets); the development containers were not touched.

```
OK    Step                                                             Expected                           Actual
--    ----                                                             --------                           ------
True  01 create smoke manager                                          201                                201
True  02 create a second smoke manager                                 201                                201
True  03 create smoke staff                                            201                                201
True  04 create smoke product                                          201                                201
True  05 import 10 units                                               200                                200
True  06 create smoke customer                                         201                                201
True  07 submit an order on website -> 202                             202                                202
True  08 ... processed by the Order Worker (is the Workers host        Succeeded                          Succeeded
      running?)
True  09 staff: orders export is forbidden                             403                                403
True  10 ... inventory export too                                      403                                403
True  11 ... and the list of exports                                   403                                403
True  12 a range that ends before it starts -> 400                     400                                400
True  13 ... on the field to                                           'To date' must not be earlier      'To date' must not be earlier
                                                                       than 'From date'.                  than 'From date'.
True  14 no range at all -> 400 on from                                'From date' must not be empty.     'From date' must not be empty.
True  15 an unknown time zone -> 400 on timeZone                       'Time zone' must be the name of a  'Time zone' must be the name of a
                                                                       time zone, for example             time zone, for example
                                                                       Asia/Ho_Chi_Minh.                  Asia/Ho_Chi_Minh.
True  16 orders export -> 202                                          202                                202
True  17 the object storage answers (is the emulator up, do the        False                              False
      Workers have its keys? README "Reports and Excel export")
True  18 the export is built within 120 s (is the Workers host         Succeeded                          Succeeded
      running?)
True  19 its detail: an orders report                                  Orders                             Orders
True  20 ... with 2 rows: the order and its line                       2                                  2
True  21 ... a file name orders_<date>-<time>.xlsx                     True                               True
True  22 ... kept for 7 days                                           True                               True
True  23 ... the time zone of the request in its parameters            Asia/Ho_Chi_Minh                   Asia/Ho_Chi_Minh
True  24 its task: type ReportExport, one attempt by the               ReportExport 1 ReportExportWorker  ReportExport 1 ReportExportWorker
      ReportExportWorker
True  25 download link -> 200                                          200                                200
True  26 ... with the file name of the export                          orders_20261005-0228.xlsx          orders_20261005-0228.xlsx
True  27 GET the link (no Authorization header) -> 200                 200                                200
True  28 ... the bytes start with PK (an xlsx file)                    True                               True
True  29 ... as many bytes as the export says                          9096                               9096
True  30 ... served as an attachment with the file name                attachment; filename="orders_20261 attachment; filename="orders_20261
                                                                       005-0228.xlsx"                     005-0228.xlsx"
True  31 the same object without the signature is rejected             403                                403
True  32 the second manager: detail -> 404                             404                                404
True  33 ... download -> 404                                           404                                404
True  34 ... and an empty list                                         0                                  0
True  35 the administrator: detail -> 404                              404                                404
True  36 inventory export -> 202                                       202                                202
True  37 ... built within 120 s                                        Succeeded                          Succeeded
True  38 ... one row: the smoke product                                1                                  1
True  39 ... its file starts with PK and is an attachment              True                               True
True  40 the requester lists both exports, newest first                01a10863-616d-7dfe-b217-a863973f3a 01a10863-616d-7dfe-b217-a863973f3a
                                                                       8b 01a10863-537b-762c-9ef1-bab6ad9 8b 01a10863-537b-762c-9ef1-bab6ad9
                                                                       4685a                              4685a
True  41 report.export_requested in the system events                  1                                  1
True  42 ... with the requester's e-mail                               smoke-020-mgr-55478@example.com    smoke-020-mgr-55478@example.com
True  43 ... and the report type                                       Orders                             Orders
True  44 lock smoke account 1                                          200                                200
True  45 lock smoke account 2                                          200                                200
True  46 lock smoke account 3                                          200                                200

PASSED: all 46 checks.
```

With the emulator stopped, the script ends after the first export request:
```
False 17 the object storage answers (is the emulator up, do the        False [Boolean]                    True [Boolean]
        Workers have its keys? README "Reports and Excel export")
False unexpected error: The Report Export Worker cannot reach the      no error [String]                  error [String]
        object storage: start it, run scripts\setup-object-storage.ps1,
        restart the Workers host and run the script again.
FAILED: 2 of 21 checks.
```
The script was **not** run on the development environment: migration `ReportExports` is not applied there (no
migrator credentials in this session) and the Workers host has no storage keys yet.

### Log scan (throwaway API and Workers, JSON logs)
Lines containing a signature parameter (`X-Amz-`), the key id, the secret key, the storage endpoint
(`127.0.0.1:58333`), the bucket name, a database / broker / admin password, the JWT key, `Password=` or an access token:

| Log | Lines | Hits |
| --- | --- | --- |
| API (smoke run, both measurements, storage-down run) | 226 (225 Information, 1 Warning: HTTPS redirect port) | 0 |
| Workers, smoke run | 14 | 0 |
| Workers, orders measurement | 11 | 0 |
| Workers, inventory measurement + storage-down run | 18 (15 Information, 3 Warning) | 0 |

The three warnings of the storage-down run are what an operator gets: `Object storage "put" failed:
"HttpRequestException"` (operation and exception type, nothing of the storage), `Attempt 1 failed
("storage_unavailable")`, and the consumer's `… scheduling a delayed retry`. A request is logged as
`Orders export requested by "<user id>" as task <id>` - ids only.

### Linux check (IANA time zones, workbook without fonts)
A console program over the repository's own classes (`ReportTimeZones`, `ReportFileName`, `OrdersWorkbook`), run on
Windows and in `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04, .NET 10.0.12, no font directory):
```
  Asia/Ho_Chi_Minh       known=True  offset now=07:00:00 id=Asia/Ho_Chi_Minh
  Europe/Berlin          known=True  offset now=02:00:00 id=Europe/Berlin
  America/New_York       known=True  offset now=-04:00:00 id=America/New_York
  UTC                    known=True  offset now=00:00:00 id=UTC
  Mars/Olympus_Mons      known=False offset now=- id=-
  SE Asia Standard Time  known=True  offset now=07:00:00 id=SE Asia Standard Time
  Berlin 2025-10-26 00:30Z -> 2025-10-26 02:30 +02:00; 01:30Z -> 2025-10-26 02:30 +01:00
  file name: orders_20261005-0138.xlsx
  workbook: 240929 bytes; sheets: Đơn hàng, Dòng hàng, Thông tin; rows: 2001
  A2 DateTime 2025-10-26 02:30:00; D2 Text formula=False [=1+1]; H2 Number #,##0; width A=33
```
Identical on Windows (file 240,891 bytes). IANA names work on both; so do Windows names on both (through ICU). The
workbook is written without any installed font (fixed widths, no `AdjustToContents`). The program is not committed;
the image (340 MB) was pulled for this check and is still on the machine.

### Mutation run
One change at a time, build, the tests that should notice, restore (`22 of 22 compiling mutants killed`):

| # | Mutant | Killed by |
| --- | --- | --- |
| 01 | read in `READ COMMITTED` instead of `REPEATABLE READ` | snapshot test |
| 02 | list/detail not restricted to the requester | endpoint tests (2) |
| 03 | `RetryScheduled` not counted as unfinished | `too_many_exports` test |
| 04 | request limit `>=` instead of `>` | `export_too_large` test (exactly the limit) |
| 05 | error code returned for an expired export | status test |
| 06 | download link for a deleted export | status test |
| 07 | no audit event for a request | request tests (2) |
| 08 | sweep also takes unfinished exports | selection test |
| 09 | sweep ignores `ExpiresAt` | selection test |
| 10 | sweep marks exports whose file was not deleted | storage-down sweep test |
| 11 | worker limit off by one | limit-at-the-worker test |
| 12 | item lines not ordered by SKU | orders end-to-end (en, vi) |
| 13 | expired export is built anyway | handler test + end-to-end |
| 14 | completion before the upload | handler tests (3) |
| 15 | storage failure not classified as `storage_unavailable` | classifier + handler tests (4) |
| 16 | task of another type accepted by the export queue | rejection tests |
| 17 | times written in UTC instead of the requested zone | workbook tests (10) |
| 18 | leading apostrophe dropped | workbook tests (3) |
| 19 | text starting with `=` written as a formula | workbook tests (3) |
| 20 | export filter ignores the channel | orders end-to-end (shared filter) |
| 21 | file name in UTC instead of the requested zone | file-name tests (5) |
| 22 | stored object without `Content-Disposition` | orders end-to-end (en, vi) |

### Backend tests (new)
- **`ReportExportEndpointsTests` (20, API only):** a request (orders, inventory) stores task, export row, outbox row
  and audit event - each checked field by field, incl. the normalised parameters, the `Location` header and the event
  seen through `GET /api/system-events` with the requester's e-mail; **a failure injected before the commit stores
  none of the four** and the same request afterwards all four; eleven 400s in en and vi (inverted range, range above
  the limit, unknown and missing time zone, missing `from` / `to`, status out of range, search too long), an unknown
  status name and an empty body; exactly `MaxRangeDays` accepted; `export_too_large` for both reports in en and vi
  with nothing written, exactly the limit accepted; `too_many_exports` (waiting + running + retry-scheduled = 3) in
  en and vi, per user, lifted when one export ends; Staff 403 and anonymous 401 on all five endpoints, an Admin may
  export; list / detail / download for the owner, **404 for another Manager and for an Admin**, same answer as for an
  unknown id; paging, direction, `reportType` filter, 400 for another sort field; `export_not_ready` (en/vi),
  `errorCode` of a failed export without its message (also not in the list), the link of a succeeded export (key,
  300 s, not in any log event of the API), `Expired` with `errorCode` null and `export_expired`.
- **`ReportExportEndToEndTests` (12, API + Workers host + RabbitMQ + emulator):** an orders export in **en and vi**
  reaches `Succeeded`; `processing.task.changed` for `Pending`, `InProgress`, `Succeeded`; the link is read by an
  `HttpClient` without `Authorization`; `Content-Disposition: attachment; filename="…"` and the xlsx content type come
  from the stored object; the unsigned object is 403; the workbook: sheet names and headers per language, bold /
  frozen / auto-filter, rows in order, numbers and money format, **date-times converted to `Europe/Berlin` across the
  end of daylight saving time** (00:30 and 02:30 local for 22:30Z and 01:30Z), `=1+1` and `@SUM(A1)` as text, the
  Info sheet (channel and customer by name, requester by e-mail, counts), no customer e-mail or address anywhere;
  orders of another channel, another customer and outside the range are not in the file. An inventory export (vi) with
  the low-stock label, a date cell, the time in `Asia/Ho_Chi_Minh`, and `lowStockOnly`. Worker: a duplicate delivery
  of a finished task → one attempt, one upload; a failure after the upload and before the commit → two attempts, two
  uploads, **one object**; storage stopped → `RetryScheduled`, then `Succeeded` after it is back; storage stopped for
  all attempts → `Failed` + `processing.failed` + `ProcessingFailed` alert + DLQ, nothing of the storage in attempts,
  event or log, then **manual retry → `Succeeded`, alert resolved**; rows above the worker's limit → `Failed`
  `export_too_large`, one attempt, no upload; expired export → `Failed` `export_expired`; three poison messages →
  `invalid_json`, `unknown_task`, `task_mismatch` in `report_export.message_rejected` and in the DLQ. Sweep: expired
  succeeded and expired failed-with-object → object gone, `DeletedAt` set, `Expired`, download 409 (en/vi); not
  expired and not finished → untouched; batch size 1 → still everything in one round; storage stopped → nothing
  marked after two rounds, done once it is back (the hosted service, interval 1 s).
- **`ReportExportSnapshotTests` (3, Workers fixture):** an order committed by another connection right before the
  item-lines query (command interceptor) is in neither sheet, and the read transaction is `REPEATABLE READ` starting
  with `SET TRANSACTION READ ONLY`; PostgreSQL refuses a write in such a transaction (25006); the sweep's selection
  (ended + past its time + not deleted, oldest first, batch size). **`ReportExportHostOptionsTests` (8):** the Workers
  host does not start with a `Reports` / `ReportExportWorker` setting out of range or without the storage keys.
- **Unit:** `ReportWorkbookTests` (28): headers per language, cell types, formats, nine time-zone cases incl. both
  changes of daylight saving time, ten formula-looking texts in all 16 text cells of both files, fixed widths, counts,
  Info sheet, status labels. `ReportContractTests` (101): file name, parameter normalisation and round trip, language,
  time zones, options (defaults, 14 out-of-range cases, limits), all 51 texts in en and vi, the entity.
  `ReportExportHandlerTests` (15): order of the steps, terminal task, expiry, permanent and temporary failures,
  shutdown before and during the work, final failure. `MessageRejectionTests` (+15): every rejection of the export
  queue through both call sites.

### Existing tests changed
- `StockSyncTopologyTests.Event_bindings_are_unchanged` — one more binding (`report.export`); the others unchanged.
- `SystemEventDetailsTests` — `ReportEvents` in the list of event classes, 14 → 16 constants, one new fact.
- `SystemEventEndpointsTests.DetailCases` — two more cases (the test requires one per allowlist entry).
- `ProcessingErrorClassifierTests` — `storage_unavailable` (transient).
- `MessageRejectionTests` — the export theory; the "covered codes" set includes it.
- `ModelConfigurationTests` — `ReportExport` (key, 1:1, check constraint, indexes, enum as string).
- `ProductImageEndpointsTests`, `StorageEmulator` — Part A2.
- Fixtures: `WorkersFixture.BuildWorkersHost` gives every test host valid storage settings (an address nothing
  listens on) and leaves the retention sweep out unless asked for; `ApiFactory` got `CreateUserClientAsync` (a user of
  its own per test: exports are per user) and `CommitFailures` (a transaction interceptor for the failure injection).
  No dashboard test changed: `processing` is built from the enum, so `ReportExport` appears by itself.

## Design notes
- **Filters:** the bodies of the two list queries moved into `OrderFilters.Apply` / `InventoryFilters.Apply`; lists
  and exports call the same code (mutant 20). The stored parameters convert to the same filter records.
- **Order of a request:** validator → range limit (configuration, checked by the service with the validator's key and
  message) → unfinished exports → `COUNT` → one `SaveChanges`. The unfinished check is check-then-insert (comment in
  the code).
- **Worker:** parse → task state → terminal: ACK → attempt (committed) → expired: `Fail` → generate (stopping token)
  → `PutAsync` (stopping token) → complete → ACK. `ObjectStorageException` is classified by the shared classifier
  (`storage_unavailable`, transient); `ReportExportRejectedException` is a permanent failure with its own code.
- **Row limit at the worker** is counted while streaming (orders, then orders + lines): no extra `COUNT`.
- **Sweep:** `ReportExportCleanup` (Infrastructure, scoped) does one round; `ReportExportCleanupService` (Workers)
  schedules it. A round stops at the first batch with a failed delete (the rest waits for the next round instead of
  being retried again and again).
- **Texts:** `ReportTexts.resx` / `.vi.resx`, always read with the culture stored with the export - the worker has no
  request culture. Labels follow the UI's wording (order statuses, inventory columns).

## Deviations from the task
1. **`Reports` options:** class and rules (`ReportsOptionsRules`) are in Application; the `IValidateOptions` adapter and
   the binding are in `AddInfrastructure` (both hosts) - Application references no options package, and none was added.
   Ranges chosen: `MaxRows` 1–1,000,000, `MaxUnfinishedPerUser` 1–100, `Retention` 1 min–366 days, `CleanupInterval`
   1 s–1 day, `CleanupBatchSize` 1–1000, `DownloadLinkLifetime` 1 s–7 days, `MaxRangeDays` 1–3660;
   `ReportExportWorker:Concurrency` 1–8.
2. **A second new event type**, `report_export.message_rejected` (allowlist `messageId`, `reasonCode`), for poison
   messages of the export queue, next to the task's `report.export_requested`. No new rejection code: `task_mismatch`
   covers "not an export task / no export row" (comment, README table).
3. **`RequestRejectedException`** (400 with an error code that belongs to no field) for `export_too_large`; the
   handler had only validation (field errors), 404 and 409.
4. **Four localised messages**, not three: `too_many_exports`, `export_too_large`, `export_not_ready`,
   `export_expired` (in the `ValidationMessages` resources, the one mechanism for texts in the request language).
5. **Worker error code `export_parameters_invalid`** (permanent): stored parameters the Workers host cannot read - a
   time zone it does not know, or broken JSON. The API validated the zone on *its* host only.
6. **Sweep details:** the first round runs after `min(CleanupInterval, 30 s)`; `DeletedAt` is set by one set-based
   statement (`ExecuteUpdate … WHERE TaskId = ANY(…) AND DeletedAt IS NULL`, with `UpdatedAt` / `UpdatedBy`), so the
   domain method `MarkDeleted` exists as asked but is used by tests only.
7. **Leading apostrophe:** ClosedXML drops it as Excel's text mark; the writer doubles it so the value is the text as
   it is (found by a test case, see "Stop conditions").
8. **Sheet names are localised too** (`Đơn hàng`, `Dòng hàng`, `Tồn kho`, `Thông tin`); an unset search leaves its
   Info cell empty, the other unset filters say "(all)".
9. **`rowCount`** = orders + item lines (the unit of `MaxRows`); the Info sheet has both counts.
10. **Time zone ids:** accepted when the host finds them, at most 64 characters, trimmed - which also admits Windows
    names (question 6).
11. **Key helper:** besides the Workers user-secrets (`-WorkersUserSecretsId` for trying it out) it now sets the
    console input encoding for the pipe: under a UTF-8 console Windows PowerShell wrote a byte-order mark in front of
    the JSON and `dotnet user-secrets` failed (the helper failed that way in this session until this was added; the
    pipe is the one of the 019 script, whose `$OutputEncoding` line alone does not prevent it).
12. **Tests:** the limit tests change `MaxRows` on the live options object of the single API host (as 019 deviation
    7); the Workers hosts of the end-to-end tests run against the API test database (as the 008 retry tests) and also
    work off exports other tests left there.
13. **Throwaway emulator on port 58333** through a compose override file (`ports: !override`); the service definition
    is otherwise the repository's.
14. **Measurement on a Debug build** and with one data shape per report (3 lines per order); `docker pull` of the
    API's runtime image for the Linux check.

## Doc deviations 22–25
22. **Confirmed.** Table `ReportExports`: `TaskId` PK + FK (1:1, restrict), `ReportType` varchar(32), `Parameters`
    jsonb, `FileName` varchar(100), `ExpiresAt`, result `ObjectKey` varchar(49) / `SizeBytes` / `RowCount` /
    `CompletedAt` (check constraint all-or-none), `DeletedAt`, audit columns; indexes `(CreatedBy, CreatedAt DESC)`
    and `(ExpiresAt) WHERE "DeletedAt" IS NULL`.
23. **Confirmed, with one addition.** Task type `ReportExport`, event type `report.export.requested`, queue
    `report.export` (+ `.dlq`); additionally the system event type `report_export.message_rejected` (deviation 2).
24. **Confirmed.** Both `POST …/export` answer 202 with the task; `GET /api/reports/exports`,
    `GET /api/reports/exports/{taskId}`, `GET /api/reports/exports/{taskId}/download`.
25. **Confirmed.** Files are deleted 7 days after the request (`Reports:Retention`), by the sweep of the Workers host;
    the rows stay (`Expired`).

## Blocked / questions for Cowork
1. **Alerts of exports that can never succeed.** `export_expired`, `export_too_large` and
   `export_parameters_invalid` end as `Failed` with a `ProcessingFailed` alert like any final failure; that alert is
   only resolved by the task succeeding, which cannot happen for an expired export. Should the sweep resolve the
   alert when it expires the export, or should these permanent outcomes open no alert?
2. **Past `expiresAt`, not swept yet:** the export is still `Succeeded` and downloadable until the next round (at most
   `CleanupInterval`). `Expired` follows `DeletedAt` as specified. Accept, or treat `expiresAt <= now` as expired in
   the API as well?
3. **`SixLabors.Fonts`** comes in at 1.0.0 (Apache-2.0); from 2.0 it has another licence and ClosedXML's range allows
   it. Pin the transitive version centrally (026)?
4. **Upload timeout:** `ObjectStorage:RequestTimeout` (10 s, at most 2 min) bounds the upload of a whole file. Fine
   with the emulator (5 MiB in well under a second); a remote provider on a slow link may need a larger value or a
   setting of its own for the Workers host (025).
5. **A user blocked while the Workers host is down:** unfinished exports count toward the limit until they end, so
   three requests during an outage block the fourth (409) until the host is back. Accept?
6. **Windows time zone names** are accepted where the host knows them (both platforms, through ICU). Restrict to IANA
   names explicitly, or leave it at "what the host finds" as the task says?
7. **Dashboard summary:** `processing` has a `ReportExport` entry now (it lists every task type). The frontend shows
   the raw name until 021, like the two pages the task names.

## Manual steps for the user
1. `powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1` - again: it now stores the storage keys
   in the user-secrets of the Workers host too (prints no key).
2. Migration `ReportExports` on development, as the migrator (README section 4):
   `cd backend; dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api`.
3. Restart the Workers host (and an API that still runs the old build). Without step 1 the Workers host stops at
   start-up with `ObjectStorage:AccessKeyId is required`.

Then, optionally, `powershell -ExecutionPolicy Bypass -File scripts\smoke\task-020.ps1` on development. The next
`docker compose … up -d` recreates the emulator container with the two additional CORS origins of Part A2 (its data
stays).

## Out of scope / follow-ups
- 021: Reports page, labels for `ReportExport`, `report.export_requested`, `report_export.message_rejected`; the
  frontend finding of review 019 (expired thumbnail links).
- 025: provider check - stored `Content-Disposition` returned on a presigned GET, `PutObject` permission, upload
  timeout (question 4), time zone data and no-font workbook in the Workers image (checked here for the API image);
  a lifecycle rule could replace the sweep.
- 026: transitive pin for `SixLabors.Fonts` (question 3); resolved `ReportExports` rows are kept without a purge.
- The `aspnet:10.0` image pulled for the Linux check can be removed (`docker rmi mcr.microsoft.com/dotnet/aspnet:10.0`)
  if it is not wanted.

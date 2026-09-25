# Report 001 — Build, test and generate the initial migration for steps 1–3

Status: Done
Commit: ac96250

## Summary
The backend restores, builds with 0 errors / 0 warnings and passes all 39 tests on .NET SDK 10.0.401.
Only two fixes were needed: package patch bumps (as instructed) and one wrong assertion in a model test;
no production code changed. `InitialCreate` was generated, `has-pending-model-changes` reports no changes,
and the reviewed SQL meets every acceptance criterion.

## Files changed
- `backend/Directory.Packages.props` — Microsoft `10.0.0` → `10.0.12`, `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.0` → `10.0.3`
  (latest same-minor patches). Side effect: removes NU1903 (high severity, GHSA-v5pm-xwqc-g5wc) on transitive
  `Microsoft.OpenApi` 2.0.0 pulled in by `Microsoft.AspNetCore.OpenApi` 10.0.0.
- `backend/.config/dotnet-tools.json` — `dotnet-ef` `10.0.0` → `10.0.12` to match `Microsoft.EntityFrameworkCore.Design` (see Deviations).
- `backend/src/Omnichannel.Tests/Persistence/ModelConfigurationTests.cs` — `Enums_are_stored_as_strings` fixed (see below).
- `backend/src/Omnichannel.Infrastructure/Persistence/Migrations/` — `20260925094920_InitialCreate.cs`, `.Designer.cs`, `OmnichannelDbContextModelSnapshot.cs` (generated).
- `docs/tasks/001-initial-create.sql` — SQL script of `InitialCreate` for review (353 lines, default non-idempotent form).

### Test fix: test was wrong, code was right
`Enums_are_stored_as_strings` failed for all 4 cases (`Expected: typeof(string)`, `Actual: null`).
`HasEnumAsString()` calls `HasConversion<string>()`, which in EF Core only sets the provider CLR type;
the actual `ValueConverter` is supplied by the type mapping, so `property.GetValueConverter()` is `null` by design.
The test now asserts `property.GetTypeMapping().Converter?.ProviderClrType == typeof(string)` — same intent
(effective storage type), not weakened. Confirmed independently by the SQL: every enum column is `character varying(32)`.

## Verification (actually executed)
Run from `backend/` on SDK `10.0.401`.

| Command | Result |
| --- | --- |
| `dotnet --version` | `10.0.401` |
| `dotnet tool restore` | Restored `dotnet-ef` (10.0.0 first, 10.0.12 after the bump) |
| `dotnet restore` | Success. Before bump: 2× NU1903 (Microsoft.OpenApi 2.0.0). After bump: no warnings |
| `dotnet build` (before any fix) | 0 errors, 0 warnings |
| `dotnet test` (before test fix) | Failed 4, Passed 35, Total 39 — all 4 = `Enums_are_stored_as_strings` |
| `dotnet ef migrations add InitialCreate ...` | `Done.` |
| `dotnet ef migrations has-pending-model-changes ...` | `No changes have been made to the model since the last migration.` |
| `dotnet ef migrations script ... -o ../docs/tasks/001-initial-create.sql` | Written, 353 lines |
| `dotnet build --no-incremental` (final) | 0 errors, 0 warnings |
| `dotnet test` (final) | Failed 0, Passed 39, Skipped 0, Total 39 |

Build warnings by ID: none.

### Acceptance criteria check (`001-initial-create.sql`)
- [x] line 171: `"AvailableStock" integer GENERATED ALWAYS AS ("TotalStock" - "ReservedStock") STORED NOT NULL`
- [x] lines 177–178: `CK_Inventories_ReservedStock_Range`, `CK_Inventories_TotalStock_NonNegative`
- [x] `InventoryId` occurs 0 times; `CONSTRAINT "PK_Inventories" PRIMARY KEY ("ProductId")`, FK to `Products ("ProductId")`
- [x] line 319: `CREATE UNIQUE INDEX "UX_Orders_ChannelId_ExternalOrderId" ON "Orders" ("ChannelId", "ExternalOrderId")`
- [x] line 327: `CREATE UNIQUE INDEX "IX_ProcessingAttempts_TaskId_AttemptNo" ON "ProcessingAttempts" ("TaskId", "AttemptNo")`
- [x] line 307: `UX_InventoryMovements_Reference_Reservation ... WHERE "ReferenceId" IS NOT NULL AND "Type" IN ('Reserve', 'Release')`
- [x] `xmin` / `RowVersion` occur 0 times (xmin is a system column, correctly not created)
- [x] seed rows: channels `digital-platform`, `website`, `pos`; roles `Admin`, `Manager`, `Staff`
- [x] `has-pending-model-changes` reports no changes (the Identity schema-version risk did not materialise)
- [x] no user / password hash is seeded

## Deviations from the task
- `dotnet-ef` tool bumped to 10.0.12. The task only named `Directory.Packages.props`, but the tool should match
  `EntityFrameworkCore.Design`; `dotnet tool restore` itself suggested 10.0.12. Revert if unwanted.

## Blocked / questions for Cowork
None.

## Out of scope / follow-ups
- Non-Microsoft packages have newer versions available but were left as-is per scope:
  FluentValidation 12.0.0 → 12.1.1 (minor), Serilog.Sinks.Console 6.0.0 → 6.1.1 (minor);
  major bumps available: Serilog.AspNetCore/Extensions.Hosting/Settings.Configuration 9 → 10, Swashbuckle.AspNetCore.SwaggerUI 9 → 10,
  Microsoft.NET.Test.Sdk 17 → 18, xunit.runner.visualstudio 3 → 4.
- Build is warning-free: `TreatWarningsAsErrors` in `backend/Directory.Build.props` (currently `false` with a TODO) can now be turned on.
- `README.md` section 4 still tells the user to run `dotnet ef migrations add InitialCreate`; the migration now exists, so that line should be removed.
- The unique index on `(TaskId, AttemptNo)` uses the EF default name `IX_...`, not the `UX_...` convention used by the other idempotency indexes. Rename only if the naming convention should apply to it.

## Manual steps for the user
These need secrets, so they were not run. PowerShell, from the repository root:

1. Infrastructure
   ```powershell
   Copy-Item docker/.env.example docker/.env   # then edit every value in docker/.env
   docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
   ```
2. API secrets
   ```powershell
   cd backend/src/Omnichannel.Api
   dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_app;Password=<OMNI_APP_PASSWORD>"
   dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random bytes, e.g. output of: openssl rand -base64 48>"
   dotnet user-secrets set "BootstrapAdmin:Email" "admin@example.com"
   dotnet user-secrets set "BootstrapAdmin:Password" "<min 12 chars, upper+lower+digit>"
   cd ../..
   ```
3. Apply the migration with the migrator role (review `docs/tasks/001-initial-create.sql` first)
   ```powershell
   $env:OMNICHANNEL_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=omnichannel;Username=omnichannel_migrator;Password=<OMNI_MIGRATOR_PASSWORD>"
   dotnet ef database update -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api
   ```
4. Run the API and smoke-test auth
   ```powershell
   dotnet run --project src/Omnichannel.Api --launch-profile http
   ```
   Then in `src/Omnichannel.Api/Omnichannel.Api.http`: `POST /api/auth/login` with the bootstrap admin, then
   `GET /api/auth/me` with the returned token. Check http://localhost:5080/health. Remove the `BootstrapAdmin` secrets afterwards.

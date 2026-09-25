# Task 001 — Build, test and generate the initial migration for steps 1–3

Status: Open
Written by: Cowork, 2026-09-25
Depends on: nothing

## Context

Steps 1–3 (solution skeleton, data model, Auth/RBAC) were written in a sandbox **without** network access to NuGet
and without a .NET SDK, so the code has **never been compiled**. Decisions are in `docs/implementation-log.md`.
Package versions in `backend/Directory.Packages.props` were pinned without being able to check they exist.

## Goal

The backend restores, builds and passes its tests on this machine, and the `InitialCreate` migration exists
and matches the intended schema.

## Scope

In scope:
- Fix compile errors, restore errors and failing tests **with the smallest change that fixes the root cause**.
- `Directory.Packages.props`: if a pinned version does not exist or conflicts, change it. Also bump every
  `10.0.0` Microsoft/Npgsql package to the latest available `10.0.x` patch (same major/minor only).
  Do not change major versions of Serilog, Swashbuckle, FluentValidation or xunit.
- Generate the `InitialCreate` migration.

Out of scope (record as follow-ups instead):
- Refactoring, renaming, new features, new endpoints, new packages.
- Changing entity design, constraints, naming conventions or the auth design.
- Anything needing secrets: creating `docker/.env`, user-secrets, `dotnet ef database update`, running the API.

## Steps

Run from `backend/`:

1. `dotnet --version` — must be `10.0.1xx` or later. If not, stop: `Status: Blocked`.
2. `dotnet tool restore`
3. `dotnet restore` → fix until it succeeds.
4. `dotnet build` → fix until **0 errors**. List warnings by ID (e.g. CS8618 ×3); do not mass-suppress them.
5. `dotnet test` → all tests must pass. If a test fails, decide whether the **test** or the **code** is wrong and
   explain which in the report. Do not delete or weaken a test to make it pass.
6. Generate the migration:
   ```
   dotnet ef migrations add InitialCreate -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api -o Persistence/Migrations
   ```
7. `dotnet ef migrations has-pending-model-changes -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api`
   → must report no pending changes. If it does (known risk: ASP.NET Core Identity schema version on .NET 10
   differing between design time and runtime), report the difference with `Status: Blocked` instead of patching it.
8. Produce the SQL for review:
   ```
   dotnet ef migrations script -p src/Omnichannel.Infrastructure -s src/Omnichannel.Api -o ../docs/tasks/001-initial-create.sql
   ```
9. Run `dotnet build` and `dotnet test` one final time after all changes.

## Acceptance criteria

- [ ] `dotnet restore`, `dotnet build` (0 errors) and `dotnet test` (all passing) succeed.
- [ ] `backend/src/Omnichannel.Infrastructure/Persistence/Migrations/` contains `InitialCreate` and the model snapshot.
- [ ] `docs/tasks/001-initial-create.sql` exists and contains:
  - [ ] `"AvailableStock" integer GENERATED ALWAYS AS ("TotalStock" - "ReservedStock") STORED`
  - [ ] `CK_Inventories_TotalStock_NonNegative` and `CK_Inventories_ReservedStock_Range`
  - [ ] no `InventoryId` column anywhere; `Inventories` primary key is `ProductId`
  - [ ] unique index `UX_Orders_ChannelId_ExternalOrderId`
  - [ ] unique index on `ProcessingAttempts ("TaskId", "AttemptNo")`
  - [ ] partial unique index `UX_InventoryMovements_Reference_Reservation`
  - [ ] no physical `xmin` / `RowVersion` column on `Inventories`
  - [ ] seed rows: channels `digital-platform`, `website`, `pos`; roles `Admin`, `Manager`, `Staff`
- [ ] `has-pending-model-changes` reports no changes.
- [ ] Every fix is listed in the report with file and reason.

## Report must also include

"Manual steps for the user" section with the commands from `README.md` sections 1, 3, 4 (second half) and 5 that
the user runs personally because they need secrets: create `docker/.env`, `docker compose up`, user-secrets,
`dotnet ef database update` with `OMNICHANNEL_MIGRATOR_CONNECTION`, run API, login + `/api/auth/me` via
`Omnichannel.Api.http`.

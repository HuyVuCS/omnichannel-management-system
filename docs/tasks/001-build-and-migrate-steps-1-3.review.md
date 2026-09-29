# Review 001 — Build, test and generate the initial migration

Reviewer: Cowork, 2026-09-29
Verdict: **Accepted**

## Checked
- Report, commit `ac96250` (stat + diff of `Directory.Packages.props`, `dotnet-tools.json`, `ModelConfigurationTests.cs`),
  `001-initial-create.sql` (tables, CHECKs, generated column, unique/partial indexes, seeds).
- Test fix is correct: `HasConversion<string>()` puts the converter on the type mapping; the new assertion checks the
  effective provider type, which is the original intent. Not a weakened test.
- Package bumps stay within 10.0.x and removed NU1903. `dotnet-ef` → 10.0.12 deviation accepted.
- Schema matches the design: `AvailableStock` STORED generated column, both inventory CHECKs, PK `ProductId`,
  no physical xmin, `UX_Orders_ChannelId_ExternalOrderId`, partial `UX_InventoryMovements_Reference_Reservation`.

## Findings
| Sev | Finding | Action |
| --- | --- | --- |
| P1 | Uncommitted change in `backend/src/Omnichannel.Api/Omnichannel.Api.http` contains the real bootstrap admin password. Committing it would put a credential in git history. | User reverts now; task 002 switches the file to `{{$dotenv ...}}`. |
| P3 | `IX_ProcessingAttempts_TaskId_AttemptNo` does not follow the `UX_` prefix used by other idempotency indexes. | Leave as is: renaming an index for style only is churn. |
| P3 | README section 4 still says to run `migrations add InitialCreate`. | Task 002 housekeeping. |
| P3 | Build is warning-free; `TreatWarningsAsErrors` can be enabled. | Task 002 housekeeping. |

## Follow-ups
- Major package upgrades (Serilog 10, Swashbuckle UI 10, Test SDK 18, xunit runner 4): not now; revisit before first deployment.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryImportIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InventoryMovements_Reference_Reservation",
                table: "InventoryMovements");

            migrationBuilder.CreateIndex(
                name: "UX_InventoryMovements_Reference",
                table: "InventoryMovements",
                columns: new[] { "ReferenceType", "ReferenceId", "ProductId", "Type" },
                unique: true,
                filter: "\"ReferenceId\" IS NOT NULL AND \"Type\" IN ('Import', 'Reserve', 'Release')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_InventoryMovements_Reference",
                table: "InventoryMovements");

            migrationBuilder.CreateIndex(
                name: "UX_InventoryMovements_Reference_Reservation",
                table: "InventoryMovements",
                columns: new[] { "ReferenceType", "ReferenceId", "ProductId", "Type" },
                unique: true,
                filter: "\"ReferenceId\" IS NOT NULL AND \"Type\" IN ('Reserve', 'Release')");
        }
    }
}

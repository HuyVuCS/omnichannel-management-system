using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProcessingTasks_Status_UpdatedAt",
                table: "ProcessingTasks",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_Failed_CreatedAt",
                table: "OutboxEvents",
                column: "CreatedAt",
                filter: "\"Status\" = 'Failed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingTasks_Status_UpdatedAt",
                table: "ProcessingTasks");

            migrationBuilder.DropIndex(
                name: "IX_OutboxEvents_Failed_CreatedAt",
                table: "OutboxEvents");
        }
    }
}

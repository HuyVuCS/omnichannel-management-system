using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LastStockSyncIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProcessingTasks_ChannelId_UpdatedAt",
                table: "ProcessingTasks",
                columns: new[] { "ChannelId", "UpdatedAt" },
                filter: "\"TaskType\" = 'StockSync' AND \"Status\" = 'Succeeded'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingTasks_ChannelId_UpdatedAt",
                table: "ProcessingTasks");
        }
    }
}

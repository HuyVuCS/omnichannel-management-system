using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskListOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProcessingTasks_CreatedAt_TaskId",
                table: "ProcessingTasks",
                columns: new[] { "CreatedAt", "TaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingTasks_CreatedAt_TaskId",
                table: "ProcessingTasks");
        }
    }
}

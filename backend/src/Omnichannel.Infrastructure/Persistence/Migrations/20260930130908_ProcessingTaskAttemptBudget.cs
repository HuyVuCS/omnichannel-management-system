using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessingTaskAttemptBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptBudgetStart",
                table: "ProcessingTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingTasks_AttemptBudgetStart_NonNegative",
                table: "ProcessingTasks",
                sql: "\"AttemptBudgetStart\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingTasks_AttemptBudgetStart_NonNegative",
                table: "ProcessingTasks");

            migrationBuilder.DropColumn(
                name: "AttemptBudgetStart",
                table: "ProcessingTasks");
        }
    }
}

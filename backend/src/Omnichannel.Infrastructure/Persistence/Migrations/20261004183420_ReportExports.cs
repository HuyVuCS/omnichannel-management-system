using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportExports",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    FileName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(49)", maxLength: 49, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportExports", x => x.TaskId);
                    table.CheckConstraint("CK_ReportExports_Result_AllOrNone", "(\"ObjectKey\" IS NULL AND \"SizeBytes\" IS NULL AND \"RowCount\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"ObjectKey\" IS NOT NULL AND \"SizeBytes\" IS NOT NULL AND \"RowCount\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ReportExports_ProcessingTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProcessingTasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExports_CreatedBy_CreatedAt",
                table: "ReportExports",
                columns: new[] { "CreatedBy", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ReportExports_ExpiresAt",
                table: "ReportExports",
                column: "ExpiresAt",
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportExports");
        }
    }
}

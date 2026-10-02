using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Omnichannel.Infrastructure.Persistence.Alerts;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Alerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirstOccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.AlertId);
                    table.CheckConstraint("CK_Alerts_Acknowledged_By", "(\"AcknowledgedAt\" IS NULL) = (\"AcknowledgedBy\" IS NULL)");
                    table.CheckConstraint("CK_Alerts_OccurrenceCount_Positive", "\"OccurrenceCount\" >= 1");
                    table.CheckConstraint("CK_Alerts_Resolved_ResolvedAt", "(\"Status\" = 'Resolved') = (\"ResolvedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_Alerts_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "ChannelId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Alerts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Alerts_ProcessingTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ProcessingTasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Alerts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ProductId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ChannelId",
                table: "Alerts",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_OrderId",
                table: "Alerts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ProductId",
                table: "Alerts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Status_LastOccurredAt",
                table: "Alerts",
                columns: new[] { "Status", "LastOccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TaskId",
                table: "Alerts",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "UX_Alerts_DedupKey_Unresolved",
                table: "Alerts",
                column: "DedupKey",
                unique: true,
                filter: "\"Status\" <> 'Resolved'");

            // Alerts for the state that exists already: low products, channel/product pairs whose last finished
            // stock sync failed, failed tasks of other types (see AlertBackfill).
            migrationBuilder.Sql(AlertBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");
        }
    }
}

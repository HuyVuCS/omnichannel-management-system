using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxPublishTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxEvents_Pending_CreatedAt",
                table: "OutboxEvents");

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "OutboxEvents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "OutboxEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishAttempts",
                table: "OutboxEvents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_Pending_CreatedAt",
                table: "OutboxEvents",
                column: "CreatedAt",
                filter: "\"Status\" = 'Pending'")
                .Annotation("Npgsql:IndexInclude", new[] { "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxEvents_Pending_CreatedAt",
                table: "OutboxEvents");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "OutboxEvents");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "OutboxEvents");

            migrationBuilder.DropColumn(
                name: "PublishAttempts",
                table: "OutboxEvents");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_Pending_CreatedAt",
                table: "OutboxEvents",
                column: "CreatedAt",
                filter: "\"Status\" = 'Pending'");
        }
    }
}

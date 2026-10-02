using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChannelId",
                table: "ProcessingTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "ProcessingTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoutingKey",
                table: "OutboxEvents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingTasks_ChannelId",
                table: "ProcessingTasks",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingTasks_ProductId",
                table: "ProcessingTasks",
                column: "ProductId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingTasks_StockSync_ChannelAndProduct",
                table: "ProcessingTasks",
                sql: "\"TaskType\" <> 'StockSync' OR (\"ChannelId\" IS NOT NULL AND \"ProductId\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessingTasks_Channels_ChannelId",
                table: "ProcessingTasks",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "ChannelId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessingTasks_Products_ProductId",
                table: "ProcessingTasks",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "ProductId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProcessingTasks_Channels_ChannelId",
                table: "ProcessingTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_ProcessingTasks_Products_ProductId",
                table: "ProcessingTasks");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingTasks_ChannelId",
                table: "ProcessingTasks");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingTasks_ProductId",
                table: "ProcessingTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingTasks_StockSync_ChannelAndProduct",
                table: "ProcessingTasks");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "ProcessingTasks");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "ProcessingTasks");

            migrationBuilder.DropColumn(
                name: "RoutingKey",
                table: "OutboxEvents");
        }
    }
}

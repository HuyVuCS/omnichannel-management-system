using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SimulatedChannelStocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SimulatedChannelStocks",
                columns: table => new
                {
                    ChannelSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    AvailableStock = table.Column<int>(type: "integer", nullable: false),
                    LastRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulatedChannelStocks", x => new { x.ChannelSlug, x.ExternalKey });
                    table.CheckConstraint("CK_SimulatedChannelStocks_AvailableStock_NonNegative", "\"AvailableStock\" >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimulatedChannelStocks");
        }
    }
}

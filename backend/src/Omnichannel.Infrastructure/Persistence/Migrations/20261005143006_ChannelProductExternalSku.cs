using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelProductExternalSku : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ChannelProducts_ChannelId_ExternalSku",
                table: "ChannelProducts",
                columns: new[] { "ChannelId", "ExternalSku" },
                unique: true,
                filter: "\"ExternalProductId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChannelProducts_ChannelId_ExternalSku",
                table: "ChannelProducts");
        }
    }
}

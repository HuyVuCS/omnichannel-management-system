using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omnichannel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageContentType",
                table: "Products",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageKey",
                table: "Products",
                type: "character varying(82)",
                maxLength: 82,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ImageSizeBytes",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ImageUpdatedAt",
                table: "Products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_Image_AllOrNone",
                table: "Products",
                sql: "(\"ImageKey\" IS NULL AND \"ImageContentType\" IS NULL AND \"ImageSizeBytes\" IS NULL AND \"ImageUpdatedAt\" IS NULL) OR (\"ImageKey\" IS NOT NULL AND \"ImageContentType\" IS NOT NULL AND \"ImageSizeBytes\" IS NOT NULL AND \"ImageUpdatedAt\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_Image_AllOrNone",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ImageContentType",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ImageKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ImageSizeBytes",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ImageUpdatedAt",
                table: "Products");
        }
    }
}

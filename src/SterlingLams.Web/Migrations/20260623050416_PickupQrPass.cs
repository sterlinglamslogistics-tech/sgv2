using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class PickupQrPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PickupReadyEmailedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupToken",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PickupToken",
                table: "Orders",
                column: "PickupToken",
                unique: true,
                filter: "\"PickupToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_PickupToken",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PickupReadyEmailedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PickupToken",
                table: "Orders");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class MpesaPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StkConsumerKey",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StkConsumerSecret",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StkEnvironment",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "sandbox");

            migrationBuilder.AddColumn<string>(
                name: "StkPasskey",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StkShortcode",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StkTill",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "MpesaRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesDocId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    CheckoutId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Receipt = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MpesaRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MpesaRequests_CheckoutId",
                table: "MpesaRequests",
                column: "CheckoutId");

            migrationBuilder.CreateIndex(
                name: "IX_MpesaRequests_SalesDocId",
                table: "MpesaRequests",
                column: "SalesDocId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MpesaRequests");

            migrationBuilder.DropColumn(
                name: "StkConsumerKey",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StkConsumerSecret",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StkEnvironment",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StkPasskey",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StkShortcode",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "StkTill",
                table: "Companies");
        }
    }
}

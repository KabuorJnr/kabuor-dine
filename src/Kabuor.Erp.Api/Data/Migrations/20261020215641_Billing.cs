using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidUntil",
                table: "Companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrialEndsAt",
                table: "Companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plan = table.Column<string>(type: "text", nullable: false),
                    Months = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CheckoutId = table.Column<string>(type: "text", nullable: false),
                    Receipt = table.Column<string>(type: "text", nullable: false),
                    Test = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPayments_CheckoutId",
                table: "BillingPayments",
                column: "CheckoutId");

            // Companies already using Kabuor get a full 30-day trial from the day plans arrive, not from sign-up.
            migrationBuilder.Sql("UPDATE \"Companies\" SET \"TrialEndsAt\" = now() + interval '30 days' WHERE \"TrialEndsAt\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingPayments");

            migrationBuilder.DropColumn(
                name: "PaidUntil",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TrialEndsAt",
                table: "Companies");
        }
    }
}

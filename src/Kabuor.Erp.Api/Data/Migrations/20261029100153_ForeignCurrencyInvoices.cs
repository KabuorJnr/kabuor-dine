using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class ForeignCurrencyInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FxNet",
                table: "SalesLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxTax",
                table: "SalesLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxTotal",
                table: "SalesLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxUnitPrice",
                table: "SalesLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SalesDocs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "FxNet",
                table: "SalesDocs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxPaid",
                table: "SalesDocs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxTax",
                table: "SalesDocs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FxTotal",
                table: "SalesDocs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "Rate",
                table: "SalesDocs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<long>(
                name: "FxAmount",
                table: "PaymentAllocations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Customers",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FxNet",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "FxTax",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "FxTotal",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "FxUnitPrice",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "FxNet",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "FxPaid",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "FxTax",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "FxTotal",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "Rate",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "FxAmount",
                table: "PaymentAllocations");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Customers");
        }
    }
}

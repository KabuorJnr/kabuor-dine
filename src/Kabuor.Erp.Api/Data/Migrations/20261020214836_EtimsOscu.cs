using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class EtimsOscu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EtimsInvcNo",
                table: "SalesDocs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "EtimsClassCode",
                table: "Items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EtimsItemCode",
                table: "Items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "EtimsDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: false),
                    BranchId = table.Column<string>(type: "text", nullable: false),
                    DeviceSerial = table.Column<string>(type: "text", nullable: false),
                    CmcKey = table.Column<string>(type: "text", nullable: false),
                    SdcId = table.Column<string>(type: "text", nullable: false),
                    MrcNo = table.Column<string>(type: "text", nullable: false),
                    TaxpayerName = table.Column<string>(type: "text", nullable: false),
                    BranchName = table.Column<string>(type: "text", nullable: false),
                    InitializedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastInvoiceNo = table.Column<long>(type: "bigint", nullable: false),
                    LastPurchaseNo = table.Column<long>(type: "bigint", nullable: false),
                    LastItemSeq = table.Column<int>(type: "integer", nullable: false),
                    DefaultClassCode = table.Column<string>(type: "text", nullable: false),
                    DefaultItemCode = table.Column<string>(type: "text", nullable: false),
                    LastPurchaseSync = table.Column<string>(type: "text", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EtimsDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EtimsPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierPin = table.Column<string>(type: "text", nullable: false),
                    SupplierName = table.Column<string>(type: "text", nullable: false),
                    SupplierBranch = table.Column<string>(type: "text", nullable: false),
                    SupplierInvoiceNo = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: false),
                    Tax = table.Column<long>(type: "bigint", nullable: false),
                    Lines = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EtimsPurchases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EtimsDevices_CompanyId",
                table: "EtimsDevices",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EtimsPurchases_CompanyId_Status",
                table: "EtimsPurchases",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EtimsPurchases_CompanyId_SupplierPin_SupplierInvoiceNo",
                table: "EtimsPurchases",
                columns: new[] { "CompanyId", "SupplierPin", "SupplierInvoiceNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EtimsDevices");

            migrationBuilder.DropTable(
                name: "EtimsPurchases");

            migrationBuilder.DropColumn(
                name: "EtimsInvcNo",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "EtimsClassCode",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "EtimsItemCode",
                table: "Items");
        }
    }
}

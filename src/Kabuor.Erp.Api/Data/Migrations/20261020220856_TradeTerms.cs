using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class TradeTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BatchNo",
                table: "StockMoves",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "SalesLines",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitFactor",
                table: "SalesLines",
                type: "numeric",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<string>(
                name: "BatchNo",
                table: "PurchaseLines",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Expiry",
                table: "PurchaseLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "PurchaseLines",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitFactor",
                table: "PurchaseLines",
                type: "numeric",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<bool>(
                name: "TrackBatches",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PriceListId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Factor = table.Column<decimal>(type: "numeric", nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchNo = table.Column<string>(type: "text", nullable: false),
                    Expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceListItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceListItems_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemUnits_ItemId_Name",
                table: "ItemUnits",
                columns: new[] { "ItemId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItems_PriceListId_ItemId",
                table: "PriceListItems",
                columns: new[] { "PriceListId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockBatches_CompanyId_ItemId_LocationId_BatchNo",
                table: "StockBatches",
                columns: new[] { "CompanyId", "ItemId", "LocationId", "BatchNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemUnits");

            migrationBuilder.DropTable(
                name: "PriceListItems");

            migrationBuilder.DropTable(
                name: "StockBatches");

            migrationBuilder.DropTable(
                name: "PriceLists");

            migrationBuilder.DropColumn(
                name: "BatchNo",
                table: "StockMoves");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "UnitFactor",
                table: "SalesLines");

            migrationBuilder.DropColumn(
                name: "BatchNo",
                table: "PurchaseLines");

            migrationBuilder.DropColumn(
                name: "Expiry",
                table: "PurchaseLines");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "PurchaseLines");

            migrationBuilder.DropColumn(
                name: "UnitFactor",
                table: "PurchaseLines");

            migrationBuilder.DropColumn(
                name: "TrackBatches",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PriceListId",
                table: "Customers");
        }
    }
}

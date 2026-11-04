using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class KabuorDine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Product",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "erp");

            migrationBuilder.CreateTable(
                name: "DineMenu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Station = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Options = table.Column<string>(type: "text", nullable: false),
                    SoldOut = table.Column<bool>(type: "boolean", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    Popular = table.Column<bool>(type: "boolean", nullable: false),
                    SuggestItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sort = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineMenu", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DineOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    GuestName = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    PlacedBy = table.Column<string>(type: "text", nullable: false),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Total = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DineSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CallWaiterAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BillRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Feedback = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DineSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayMode = table.Column<string>(type: "text", nullable: false),
                    ApproveFirstOrder = table.Column<bool>(type: "boolean", nullable: false),
                    Stations = table.Column<string>(type: "text", nullable: false),
                    Welcome = table.Column<string>(type: "text", nullable: false),
                    Open = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DineTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<string>(type: "text", nullable: false),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineTables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DineOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<long>(type: "bigint", nullable: false),
                    Options = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    Station = table.Column<string>(type: "text", nullable: false),
                    Ready = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DineOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DineOrderLines_DineOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "DineOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DineMenu_CompanyId_ItemId",
                table: "DineMenu",
                columns: new[] { "CompanyId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DineOrderLines_OrderId",
                table: "DineOrderLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DineOrders_CompanyId_SessionId",
                table: "DineOrders",
                columns: new[] { "CompanyId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DineOrders_CompanyId_Status",
                table: "DineOrders",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DineSessions_CompanyId_TableId_Status",
                table: "DineSessions",
                columns: new[] { "CompanyId", "TableId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DineSettings_CompanyId",
                table: "DineSettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DineTables_Token",
                table: "DineTables",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DineMenu");

            migrationBuilder.DropTable(
                name: "DineOrderLines");

            migrationBuilder.DropTable(
                name: "DineSessions");

            migrationBuilder.DropTable(
                name: "DineSettings");

            migrationBuilder.DropTable(
                name: "DineTables");

            migrationBuilder.DropTable(
                name: "DineOrders");

            migrationBuilder.DropColumn(
                name: "Product",
                table: "Companies");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class PayToStartFleetAndHiring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSignInAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "AccountingLockDate",
                table: "Companies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AwaitingFirstPayment",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Industry",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedAt",
                table: "BillingPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpeningId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    InterviewOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    RejectReason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StageChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DispatchTrips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripNumber = table.Column<string>(type: "text", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    VehiclePlate = table.Column<string>(type: "text", nullable: false),
                    DriverName = table.Column<string>(type: "text", nullable: false),
                    DriverPhone = table.Column<string>(type: "text", nullable: false),
                    RouteName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchTrips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobOpenings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Department = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Positions = table.Column<int>(type: "integer", nullable: false),
                    Open = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobOpenings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesDocId = table.Column<Guid>(type: "uuid", nullable: true),
                    WaybillNumber = table.Column<string>(type: "text", nullable: false),
                    CustomerName = table.Column<string>(type: "text", nullable: false),
                    DeliveryAddress = table.Column<string>(type: "text", nullable: false),
                    ContactPhone = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RecipientName = table.Column<string>(type: "text", nullable: false),
                    PodNotes = table.Column<string>(type: "text", nullable: false),
                    PodConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryItems_DispatchTrips_TripId",
                        column: x => x.TripId,
                        principalTable: "DispatchTrips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_CompanyId_OpeningId_Stage",
                table: "Candidates",
                columns: new[] { "CompanyId", "OpeningId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryItems_CompanyId_SalesDocId",
                table: "DeliveryItems",
                columns: new[] { "CompanyId", "SalesDocId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryItems_CompanyId_WaybillNumber",
                table: "DeliveryItems",
                columns: new[] { "CompanyId", "WaybillNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryItems_TripId",
                table: "DeliveryItems",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchTrips_CompanyId_TripNumber",
                table: "DispatchTrips",
                columns: new[] { "CompanyId", "TripNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispatchTrips_CompanyId_VehicleId_Status",
                table: "DispatchTrips",
                columns: new[] { "CompanyId", "VehicleId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Candidates");

            migrationBuilder.DropTable(
                name: "DeliveryItems");

            migrationBuilder.DropTable(
                name: "JobOpenings");

            migrationBuilder.DropTable(
                name: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastSignInAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AccountingLockDate",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "AwaitingFirstPayment",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Industry",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CheckedAt",
                table: "BillingPayments");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetersAdvancesTransfersPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastDoneMeter",
                table: "MaintenancePlans",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LeadMeter",
                table: "MaintenancePlans",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MeterEvery",
                table: "MaintenancePlans",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NextDueMeter",
                table: "MaintenancePlans",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MeterReadAt",
                table: "Assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MeterReading",
                table: "Assets",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "MeterUnit",
                table: "Assets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "BudgetTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    FromDepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToDepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedBy = table.Column<string>(type: "text", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashAdvances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    AccountBy = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HeadApprovedBy = table.Column<string>(type: "text", nullable: false),
                    SignedOffBy = table.Column<string>(type: "text", nullable: false),
                    SignedOffAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinanceNote = table.Column<string>(type: "text", nullable: false),
                    OverBudget = table.Column<bool>(type: "boolean", nullable: false),
                    PaidBy = table.Column<int>(type: "integer", nullable: true),
                    PaidReference = table.Column<string>(type: "text", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Spent = table.Column<long>(type: "bigint", nullable: false),
                    Returned = table.Column<long>(type: "bigint", nullable: false),
                    RecoveredFromPay = table.Column<long>(type: "bigint", nullable: false),
                    Reimbursed = table.Column<long>(type: "bigint", nullable: false),
                    AccountedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectReason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAdvances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeterReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reading = table.Column<decimal>(type: "numeric", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterReadings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlanningMeetings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Attendees = table.Column<string>(type: "text", nullable: false),
                    Decisions = table.Column<string>(type: "text", nullable: false),
                    Snapshot = table.Column<string>(type: "text", nullable: false),
                    AgreedBy = table.Column<string>(type: "text", nullable: false),
                    AgreedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanningMeetings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashAdvanceExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CashAdvanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Receipt = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAdvanceExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashAdvanceExpenses_CashAdvances_CashAdvanceId",
                        column: x => x.CashAdvanceId,
                        principalTable: "CashAdvances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetTransfers_CompanyId_Number",
                table: "BudgetTransfers",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashAdvanceExpenses_CashAdvanceId",
                table: "CashAdvanceExpenses",
                column: "CashAdvanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAdvances_CompanyId_EmployeeId",
                table: "CashAdvances",
                columns: new[] { "CompanyId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashAdvances_CompanyId_Number",
                table: "CashAdvances",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_CompanyId_AssetId_At",
                table: "MeterReadings",
                columns: new[] { "CompanyId", "AssetId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanningMeetings_CompanyId_Month",
                table: "PlanningMeetings",
                columns: new[] { "CompanyId", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BudgetTransfers");

            migrationBuilder.DropTable(
                name: "CashAdvanceExpenses");

            migrationBuilder.DropTable(
                name: "MeterReadings");

            migrationBuilder.DropTable(
                name: "PlanningMeetings");

            migrationBuilder.DropTable(
                name: "CashAdvances");

            migrationBuilder.DropColumn(
                name: "LastDoneMeter",
                table: "MaintenancePlans");

            migrationBuilder.DropColumn(
                name: "LeadMeter",
                table: "MaintenancePlans");

            migrationBuilder.DropColumn(
                name: "MeterEvery",
                table: "MaintenancePlans");

            migrationBuilder.DropColumn(
                name: "NextDueMeter",
                table: "MaintenancePlans");

            migrationBuilder.DropColumn(
                name: "MeterReadAt",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "MeterReading",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "MeterUnit",
                table: "Assets");
        }
    }
}

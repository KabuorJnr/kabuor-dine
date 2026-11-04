using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class BudgetsApprovalsStoresTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinanceNote",
                table: "Requisitions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinanceSignedOffAt",
                table: "Requisitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinanceSignedOffBy",
                table: "Requisitions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinanceSignedOffAt",
                table: "PurchaseDocs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinanceSignedOffBy",
                table: "PurchaseDocs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StoresCategory",
                table: "Items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Use",
                table: "Items",
                type: "text",
                nullable: false,
                defaultValue: "Sale");

            // Enum columns are read back by name: an empty value can't be read. Assets added before criticality
            // existed got an empty one from the previous migration; they're Medium, the default.
            migrationBuilder.Sql("UPDATE \"Assets\" SET \"Criticality\" = 'Medium' WHERE \"Criticality\" = '';");
            // The approvals register is append-only in the database too: no UPDATE, whoever runs it. (Removing a
            // record still breaks the chain of fingerprints, which the register's check reports.)
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION approval_records_no_update() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'The approvals register cannot be changed.';
END;
$$ LANGUAGE plpgsql;");

            migrationBuilder.CreateTable(
                name: "ApprovalRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    PrevHash = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Budgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Months = table.Column<List<long>>(type: "bigint[]", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: false),
                    Justification = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PreparedBy = table.Column<string>(type: "text", nullable: false),
                    PreparedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "text", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "text", nullable: false),
                    RevisesId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Budgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderTools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedOutTo = table.Column<string>(type: "text", nullable: false),
                    CheckedOutAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReturnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReturnCondition = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderTools", x => x.Id);
                });

            migrationBuilder.Sql(@"CREATE TRIGGER approval_records_append_only BEFORE UPDATE ON ""ApprovalRecords"" FOR EACH ROW EXECUTE FUNCTION approval_records_no_update();");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRecords_CompanyId_Seq",
                table: "ApprovalRecords",
                columns: new[] { "CompanyId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRecords_CompanyId_Subject_SubjectId",
                table: "ApprovalRecords",
                columns: new[] { "CompanyId", "Subject", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_CompanyId_DepartmentId_Year",
                table: "Budgets",
                columns: new[] { "CompanyId", "DepartmentId", "Year" });

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_CompanyId_Number",
                table: "Budgets",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderTools_CompanyId_AssetId",
                table: "WorkOrderTools",
                columns: new[] { "CompanyId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderTools_CompanyId_WorkOrderId",
                table: "WorkOrderTools",
                columns: new[] { "CompanyId", "WorkOrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS approval_records_append_only ON ""ApprovalRecords"";");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS approval_records_no_update();");
            migrationBuilder.DropTable(
                name: "ApprovalRecords");

            migrationBuilder.DropTable(
                name: "Budgets");

            migrationBuilder.DropTable(
                name: "WorkOrderTools");

            migrationBuilder.DropColumn(
                name: "FinanceNote",
                table: "Requisitions");

            migrationBuilder.DropColumn(
                name: "FinanceSignedOffAt",
                table: "Requisitions");

            migrationBuilder.DropColumn(
                name: "FinanceSignedOffBy",
                table: "Requisitions");

            migrationBuilder.DropColumn(
                name: "FinanceSignedOffAt",
                table: "PurchaseDocs");

            migrationBuilder.DropColumn(
                name: "FinanceSignedOffBy",
                table: "PurchaseDocs");

            migrationBuilder.DropColumn(
                name: "StoresCategory",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Use",
                table: "Items");
        }
    }
}

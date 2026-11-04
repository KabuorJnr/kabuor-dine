using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class Intelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "SalesDocs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InquiryId",
                table: "SalesDocs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkId",
                table: "SalesDocs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalespersonName",
                table: "SalesDocs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SalespersonUserId",
                table: "SalesDocs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Leads",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InquiryId",
                table: "Leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkId",
                table: "Leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AcquiredByUserId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcquisitionChannel",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FirstLinkId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Inquiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedName = table.Column<string>(type: "text", nullable: false),
                    FirstResponseAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LostReason = table.Column<string>(type: "text", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inquiries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkVisits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorId = table.Column<string>(type: "text", nullable: false),
                    Referrer = table.Column<string>(type: "text", nullable: false),
                    Mobile = table.Column<bool>(type: "boolean", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkVisits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortalCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackingLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerName = table.Column<string>(type: "text", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    Target = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InquiryMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InquiryId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "text", nullable: false),
                    Author = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InquiryMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InquiryMessages_Inquiries_InquiryId",
                        column: x => x.InquiryId,
                        principalTable: "Inquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesDocs_CompanyId_LinkId",
                table: "SalesDocs",
                columns: new[] { "CompanyId", "LinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CompanyId_LinkId",
                table: "Leads",
                columns: new[] { "CompanyId", "LinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CompanyId_FirstLinkId",
                table: "Customers",
                columns: new[] { "CompanyId", "FirstLinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_Inquiries_CompanyId_Number",
                table: "Inquiries",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inquiries_CompanyId_Phone",
                table: "Inquiries",
                columns: new[] { "CompanyId", "Phone" });

            migrationBuilder.CreateIndex(
                name: "IX_Inquiries_CompanyId_Status",
                table: "Inquiries",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InquiryMessages_InquiryId",
                table: "InquiryMessages",
                column: "InquiryId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkVisits_CompanyId_LinkId_At",
                table: "LinkVisits",
                columns: new[] { "CompanyId", "LinkId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PortalCodes_CompanyId_Phone_ExpiresAt",
                table: "PortalCodes",
                columns: new[] { "CompanyId", "Phone", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackingLinks_Code",
                table: "TrackingLinks",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackingLinks_CompanyId_Channel",
                table: "TrackingLinks",
                columns: new[] { "CompanyId", "Channel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InquiryMessages");

            migrationBuilder.DropTable(
                name: "LinkVisits");

            migrationBuilder.DropTable(
                name: "PortalCodes");

            migrationBuilder.DropTable(
                name: "TrackingLinks");

            migrationBuilder.DropTable(
                name: "Inquiries");

            migrationBuilder.DropIndex(
                name: "IX_SalesDocs_CompanyId_LinkId",
                table: "SalesDocs");

            migrationBuilder.DropIndex(
                name: "IX_Leads_CompanyId_LinkId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Customers_CompanyId_FirstLinkId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "InquiryId",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "LinkId",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "SalespersonName",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "SalespersonUserId",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "InquiryId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "LinkId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "AcquiredByUserId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AcquisitionChannel",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "FirstLinkId",
                table: "Customers");
        }
    }
}

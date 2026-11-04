using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VerifiedShareViews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IpHash = table.Column<string>(type: "text", nullable: false),
                    Agent = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerifiedShareViews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerifiedShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LenderName = table.Column<string>(type: "text", nullable: false),
                    LenderContact = table.Column<string>(type: "text", nullable: false),
                    Months = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerifiedShares", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedShareViews_CompanyId_ShareId_At",
                table: "VerifiedShareViews",
                columns: new[] { "CompanyId", "ShareId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedShares_CompanyId_ExpiresAt",
                table: "VerifiedShares",
                columns: new[] { "CompanyId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedShares_CompanyId_TokenHash",
                table: "VerifiedShares",
                columns: new[] { "CompanyId", "TokenHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerifiedShareViews");

            migrationBuilder.DropTable(
                name: "VerifiedShares");
        }
    }
}

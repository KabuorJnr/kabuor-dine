using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfflineTill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientRef",
                table: "SalesDocs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SalesDocs_CompanyId_ClientRef",
                table: "SalesDocs",
                columns: new[] { "CompanyId", "ClientRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesDocs_CompanyId_ClientRef",
                table: "SalesDocs");

            migrationBuilder.DropColumn(
                name: "ClientRef",
                table: "SalesDocs");
        }
    }
}

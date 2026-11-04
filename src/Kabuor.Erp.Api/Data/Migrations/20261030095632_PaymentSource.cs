using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallbackIp",
                table: "Payments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "FromSafaricom",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Payments",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyId_Source_ReceivedAt",
                table: "Payments",
                columns: new[] { "CompanyId", "Source", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_CompanyId_Source_ReceivedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CallbackIp",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "FromSafaricom",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Payments");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class DriverLinksAndLiveMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DriverToken",
                table: "DispatchTrips",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "LastAccuracy",
                table: "DispatchTrips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastHeading",
                table: "DispatchTrips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastLat",
                table: "DispatchTrips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastLng",
                table: "DispatchTrips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPingAt",
                table: "DispatchTrips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastSpeed",
                table: "DispatchTrips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PodLat",
                table: "DeliveryItems",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PodLng",
                table: "DeliveryItems",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriverPings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lng = table.Column<double>(type: "double precision", nullable: false),
                    Accuracy = table.Column<double>(type: "double precision", nullable: true),
                    Speed = table.Column<double>(type: "double precision", nullable: true),
                    Heading = table.Column<double>(type: "double precision", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverPings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchTrips_DriverToken",
                table: "DispatchTrips",
                column: "DriverToken");

            migrationBuilder.CreateIndex(
                name: "IX_DriverPings_CompanyId_TripId_At",
                table: "DriverPings",
                columns: new[] { "CompanyId", "TripId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriverPings");

            migrationBuilder.DropIndex(
                name: "IX_DispatchTrips_DriverToken",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "DriverToken",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastAccuracy",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastHeading",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastLat",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastLng",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastPingAt",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "LastSpeed",
                table: "DispatchTrips");

            migrationBuilder.DropColumn(
                name: "PodLat",
                table: "DeliveryItems");

            migrationBuilder.DropColumn(
                name: "PodLng",
                table: "DeliveryItems");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ByItself",
                table: "AgentTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Edited",
                table: "AgentTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KickedAt",
                table: "AgentSetups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ByItself",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "Edited",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "KickedAt",
                table: "AgentSetups");
        }
    }
}

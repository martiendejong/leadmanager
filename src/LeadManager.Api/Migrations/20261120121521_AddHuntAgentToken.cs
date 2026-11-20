using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHuntAgentToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TokenExpiresAt",
                table: "HuntJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "HuntJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenExpiresAt",
                table: "HuntJobs");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "HuntJobs");
        }
    }
}

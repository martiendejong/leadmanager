using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class HuntActiveJobIndexAndConfirm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConfirmClear",
                table: "HuntJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_HuntJobs_OneActivePerUser",
                table: "HuntJobs",
                column: "UserId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HuntJobs_OneActivePerUser",
                table: "HuntJobs");

            migrationBuilder.DropColumn(
                name: "ConfirmClear",
                table: "HuntJobs");
        }
    }
}

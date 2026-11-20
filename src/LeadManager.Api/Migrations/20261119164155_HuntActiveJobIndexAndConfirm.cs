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

            // A database that ran under the old race may hold two active jobs for one user: the unique index below would then make Migrate()
            // fail at start-up. Only the newest active job per user is kept; the older ones are marked failed as superseded.
            // (test and acc had applied this migration before this clean-up was added; they held no such rows, so nothing changes there.)
            migrationBuilder.Sql(@"
UPDATE HuntJobs
SET Status = 'Failed',
    Error = 'Superseded: this user had more than one active job (an older race). Only the newest one is kept.',
    CompletedAt = CURRENT_TIMESTAMP
WHERE Status IN ('Pending', 'Running')
  AND UserId IS NOT NULL
  AND EXISTS (SELECT 1 FROM HuntJobs j2
              WHERE j2.UserId = HuntJobs.UserId
                AND j2.Status IN ('Pending', 'Running')
                AND (j2.CreatedAt > HuntJobs.CreatedAt OR (j2.CreatedAt = HuntJobs.CreatedAt AND j2.Id > HuntJobs.Id)));");

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

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class HuntSessionTokenAndCandidateLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TokenExchanged",
                table: "HuntJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "VerifiedScore",
                table: "HuntCandidates",
                type: "INTEGER",
                nullable: true);

            // Candidate rows of a lead that no longer exists: the link would break the new foreign key, and their evidence (quotes and facts about a
            // business nobody keeps any more) goes with the lead. The job history keeps the name, the website, the score and the reason.
            migrationBuilder.Sql(
                "UPDATE HuntCandidates SET EvidenceJson = '{}', LeadId = NULL " +
                "WHERE LeadId IS NOT NULL AND LeadId NOT IN (SELECT Id FROM Leads);");

            migrationBuilder.AddForeignKey(
                name: "FK_HuntCandidates_Leads_LeadId",
                table: "HuntCandidates",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HuntCandidates_Leads_LeadId",
                table: "HuntCandidates");

            migrationBuilder.DropColumn(
                name: "TokenExchanged",
                table: "HuntJobs");

            migrationBuilder.DropColumn(
                name: "VerifiedScore",
                table: "HuntCandidates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnrichmentAttempts",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnrichmentAttempts",
                table: "Leads");
        }
    }
}

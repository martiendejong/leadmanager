using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadEnrichmentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BranchCount",
                table: "Leads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmployeeCount",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacebookUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FoundingYear",
                table: "Leads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleMapsUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "GoogleRating",
                table: "Leads",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GoogleReviewCount",
                table: "Leads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPartOfGroup",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KvkNumber",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalForm",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotableClients",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesPriorityScore",
                table: "Leads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Street",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwitterUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatNumber",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZipCode",
                table: "Leads",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchCount",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "EmployeeCount",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "FacebookUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "FoundingYear",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "GoogleMapsUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "GoogleRating",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "GoogleReviewCount",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "InstagramUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "IsPartOfGroup",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "KvkNumber",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "LegalForm",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "NotableClients",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SalesPriorityScore",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Street",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "TwitterUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "VatNumber",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ZipCode",
                table: "Leads");
        }
    }
}

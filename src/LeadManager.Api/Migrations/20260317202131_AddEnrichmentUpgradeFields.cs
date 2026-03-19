using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentUpgradeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Certifications",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InternalContactName",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InternalContactRole",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpeningHours",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerLinkedInUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerMobile",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingInfo",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesPriorityLabel",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesPriorityReasoning",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Signals",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingArea",
                table: "Leads",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Certifications",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "InternalContactName",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "InternalContactRole",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OpeningHours",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OwnerLinkedInUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OwnerMobile",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "PricingInfo",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SalesPriorityLabel",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SalesPriorityReasoning",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Signals",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "WorkingArea",
                table: "Leads");
        }
    }
}

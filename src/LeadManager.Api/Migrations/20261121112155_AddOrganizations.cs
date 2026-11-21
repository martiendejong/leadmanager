using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_Name_Website_ImportedByUserId",
                table: "Leads");

            // Every existing lead and hunt first lands in Prospergenics (the column is NOT NULL); the UPDATEs below move the Giethoorn
            // ones. The organisations are inserted before the foreign keys are added (that rebuilds the tables in SQLite).
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Leads",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("6f0a7c10-0000-4000-8000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "HuntJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("6f0a7c10-0000-4000-8000-000000000001"));

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSeedLog",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSeedLog", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationMembers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Organizations",
                columns: new[] { "Id", "CreatedAt", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("6f0a7c10-0000-4000-8000-000000000001"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Prospergenics", "prospergenics" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000002"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Martien de Jong", "martien-de-jong" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000003"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Port of Giethoorn", "port-of-giethoorn" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000004"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Karibu Mara", "karibu-mara" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000005"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Rena Farm system", "rena-farm-system" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000006"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "MaaLink Ride", "maalink-ride" },
                    { new Guid("6f0a7c10-0000-4000-8000-000000000007"), new DateTime(2026, 11, 21, 0, 0, 0, 0, DateTimeKind.Utc), "Weerzien Mara", "weerzien-mara" }
                });

            // Giethoorn leads (city Giethoorn) belong to Port of Giethoorn, all other existing leads stay with Prospergenics.
            migrationBuilder.Sql(
                "UPDATE \"Leads\" SET \"OrganizationId\" = '6F0A7C10-0000-4000-8000-000000000003' WHERE lower(trim(\"City\")) = 'giethoorn';");
            migrationBuilder.Sql(
                "UPDATE \"HuntJobs\" SET \"OrganizationId\" = '6F0A7C10-0000-4000-8000-000000000003' " +
                "WHERE lower(\"Region\") LIKE '%giethoorn%' OR lower(\"Segment\") LIKE '%giethoorn%';");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Name_Website_OrganizationId",
                table: "Leads",
                columns: new[] { "Name", "Website", "OrganizationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_OrganizationId",
                table: "Leads",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_HuntJobs_OrganizationId",
                table: "HuntJobs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMembers_OrganizationId_Email",
                table: "OrganizationMembers",
                columns: new[] { "OrganizationId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMembers_UserId",
                table: "OrganizationMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Slug",
                table: "Organizations",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HuntJobs_Organizations_OrganizationId",
                table: "HuntJobs",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Organizations_OrganizationId",
                table: "Leads",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HuntJobs_Organizations_OrganizationId",
                table: "HuntJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Organizations_OrganizationId",
                table: "Leads");

            migrationBuilder.DropTable(
                name: "OrganizationMembers");

            migrationBuilder.DropTable(
                name: "OrganizationSeedLog");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Leads_Name_Website_OrganizationId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_OrganizationId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_HuntJobs_OrganizationId",
                table: "HuntJobs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "HuntJobs");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Name_Website_ImportedByUserId",
                table: "Leads",
                columns: new[] { "Name", "Website", "ImportedByUserId" },
                unique: true);
        }
    }
}

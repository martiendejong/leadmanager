using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    WebsiteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    WhatTheyDo = table.Column<string>(type: "TEXT", nullable: false),
                    IdealCustomerProfile = table.Column<string>(type: "TEXT", nullable: false),
                    ToneOfVoice = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSectorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetRegionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    KeywordsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UspsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CrawledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyProfiles_UserId",
                table: "CompanyProfiles",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyProfiles");
        }
    }
}

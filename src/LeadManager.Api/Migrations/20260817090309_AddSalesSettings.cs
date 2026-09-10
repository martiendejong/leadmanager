using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyAddress = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyCity = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyZipCode = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyKvk = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyVat = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyIban = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyEmail = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyPhone = table.Column<string>(type: "TEXT", nullable: true),
                    CompanyWebsite = table.Column<string>(type: "TEXT", nullable: true),
                    StarterHours = table.Column<decimal>(type: "TEXT", nullable: false),
                    StarterMonthlyPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    TeamHours = table.Column<decimal>(type: "TEXT", nullable: false),
                    TeamMonthlyPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    BundleHourlyRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    LooseHourlyRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    OverageHourlyRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    QuoteNumberPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    QuoteNumberCurrent = table.Column<int>(type: "INTEGER", nullable: false),
                    QuoteValidityDays = table.Column<int>(type: "INTEGER", nullable: false),
                    QuoteIntroText = table.Column<string>(type: "TEXT", nullable: true),
                    QuoteTerms = table.Column<string>(type: "TEXT", nullable: true),
                    QuoteFooterText = table.Column<string>(type: "TEXT", nullable: true),
                    CallScriptSectionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    EmailSubjectTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    EmailBodyTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    FedhaBaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FedhaApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    FedhaDefaultProjectId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesSettings");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesPlatformIntakeAndBundle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientBundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BundleType = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    HoursUsed = table.Column<decimal>(type: "TEXT", nullable: false),
                    MonthlyPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientBundles_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientIntakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductType = table.Column<int>(type: "INTEGER", nullable: false),
                    BundleType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Requirements = table.Column<string>(type: "TEXT", nullable: false),
                    FirstTask = table.Column<string>(type: "TEXT", nullable: true),
                    AdditionalNotes = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedHours = table.Column<decimal>(type: "TEXT", nullable: true),
                    EstimatedPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    EstimationReasoning = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    WorkQueueItemId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientIntakes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientIntakes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientBundles_ClientId",
                table: "ClientBundles",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientIntakes_ClientId",
                table: "ClientIntakes",
                column: "ClientId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientBundles");

            migrationBuilder.DropTable(
                name: "ClientIntakes");
        }
    }
}

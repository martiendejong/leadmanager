using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class FedhaBillingSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OutstandingAmount",
                table: "ClientInvoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SendUnknown",
                table: "ClientInvoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAt",
                table: "ClientInvoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncedAt",
                table: "ClientInvoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FedhaContactLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    FedhaContactId = table.Column<int>(type: "INTEGER", nullable: false),
                    FedhaContactName = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FedhaContactLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FedhaContactLinks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FedhaSyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Unchanged = table.Column<int>(type: "INTEGER", nullable: false),
                    Unmatched = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UnmatchedJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FedhaSyncRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FedhaContactLinks_ClientId",
                table: "FedhaContactLinks",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_FedhaContactLinks_UserId_FedhaContactId",
                table: "FedhaContactLinks",
                columns: new[] { "UserId", "FedhaContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FedhaSyncRuns_UserId_StartedAt",
                table: "FedhaSyncRuns",
                columns: new[] { "UserId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FedhaContactLinks");

            migrationBuilder.DropTable(
                name: "FedhaSyncRuns");

            migrationBuilder.DropColumn(
                name: "OutstandingAmount",
                table: "ClientInvoices");

            migrationBuilder.DropColumn(
                name: "SendUnknown",
                table: "ClientInvoices");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "ClientInvoices");

            migrationBuilder.DropColumn(
                name: "SyncedAt",
                table: "ClientInvoices");
        }
    }
}

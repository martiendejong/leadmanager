using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class ClientServicesAndDocumentVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServiceId",
                table: "ClientDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRef",
                table: "ClientDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ClientDocuments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ClientServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MonthlyAmount = table.Column<decimal>(type: "TEXT", precision: 14, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientServices_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientServices_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocuments_ClientId_SourceRef",
                table: "ClientDocuments",
                columns: new[] { "ClientId", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocuments_ServiceId",
                table: "ClientDocuments",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientServices_ClientId_Kind",
                table: "ClientServices",
                columns: new[] { "ClientId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientServices_ClientId_SourceRef",
                table: "ClientServices",
                columns: new[] { "ClientId", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientServices_ProjectId",
                table: "ClientServices",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDocuments_ClientServices_ServiceId",
                table: "ClientDocuments",
                column: "ServiceId",
                principalTable: "ClientServices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientDocuments_ClientServices_ServiceId",
                table: "ClientDocuments");

            migrationBuilder.DropTable(
                name: "ClientServices");

            migrationBuilder.DropIndex(
                name: "IX_ClientDocuments_ClientId_SourceRef",
                table: "ClientDocuments");

            migrationBuilder.DropIndex(
                name: "IX_ClientDocuments_ServiceId",
                table: "ClientDocuments");

            migrationBuilder.DropColumn(
                name: "ServiceId",
                table: "ClientDocuments");

            migrationBuilder.DropColumn(
                name: "SourceRef",
                table: "ClientDocuments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ClientDocuments");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddClientDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PredecessorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
                    DocumentDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SignedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 14, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientDocuments_ClientDocuments_PredecessorId",
                        column: x => x.PredecessorId,
                        principalTable: "ClientDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientDocuments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ClientDocumentFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    StoragePath = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientDocumentFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientDocumentFiles_ClientDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ClientDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocumentFiles_DocumentId",
                table: "ClientDocumentFiles",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocumentFiles_StoragePath",
                table: "ClientDocumentFiles",
                column: "StoragePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocuments_ClientId_DocumentDate",
                table: "ClientDocuments",
                columns: new[] { "ClientId", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocuments_PredecessorId",
                table: "ClientDocuments",
                column: "PredecessorId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientDocuments_ProjectId",
                table: "ClientDocuments",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientDocumentFiles");

            migrationBuilder.DropTable(
                name: "ClientDocuments");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRagEnrichmentV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_Name_Website",
                table: "Leads");

            migrationBuilder.AddColumn<int>(
                name: "ChunksIndexed",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CrawledAt",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrichmentVersion",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OwnerTitle",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PagesCrawled",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedUrl",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Services",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetAudience",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebsiteStatus",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LeadPageContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    RawText = table.Column<string>(type: "TEXT", nullable: false),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadPageContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadPageContents_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeadDocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageContentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChunkIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkText = table.Column<string>(type: "TEXT", nullable: false),
                    EmbeddingJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDocumentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadDocumentChunks_LeadPageContents_PageContentId",
                        column: x => x.PageContentId,
                        principalTable: "LeadPageContents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeadDocumentChunks_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Name_Website_ImportedByUserId",
                table: "Leads",
                columns: new[] { "Name", "Website", "ImportedByUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadDocumentChunks_LeadId",
                table: "LeadDocumentChunks",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadDocumentChunks_PageContentId",
                table: "LeadDocumentChunks",
                column: "PageContentId");

            migrationBuilder.CreateIndex(
                name: "IX_LeadPageContents_LeadId",
                table: "LeadPageContents",
                column: "LeadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadDocumentChunks");

            migrationBuilder.DropTable(
                name: "LeadPageContents");

            migrationBuilder.DropIndex(
                name: "IX_Leads_Name_Website_ImportedByUserId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ChunksIndexed",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CrawledAt",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "EnrichmentVersion",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "OwnerTitle",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "PagesCrawled",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ResolvedUrl",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Services",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "TargetAudience",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "WebsiteStatus",
                table: "Leads");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Name_Website",
                table: "Leads",
                columns: new[] { "Name", "Website" },
                unique: true);
        }
    }
}

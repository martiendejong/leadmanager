using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractionProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Interactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_ProjectId_OccurredAt",
                table: "Interactions",
                columns: new[] { "ProjectId", "OccurredAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Interactions_Projects_ProjectId",
                table: "Interactions",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Interactions_Projects_ProjectId",
                table: "Interactions");

            migrationBuilder.DropIndex(
                name: "IX_Interactions_ProjectId_OccurredAt",
                table: "Interactions");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Interactions");
        }
    }
}

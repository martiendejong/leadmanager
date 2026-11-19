using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "EntityLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntityLinks_ProjectId",
                table: "EntityLinks",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_EntityLinks_Projects_ProjectId",
                table: "EntityLinks",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EntityLinks_Projects_ProjectId",
                table: "EntityLinks");

            migrationBuilder.DropIndex(
                name: "IX_EntityLinks_ProjectId",
                table: "EntityLinks");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "EntityLinks");
        }
    }
}

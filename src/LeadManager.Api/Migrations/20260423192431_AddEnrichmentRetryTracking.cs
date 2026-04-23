using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentRetryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only add genuinely new columns. Prior migrations (AddHangfireNotifications,
            // AddLeadActivityAndPipeline, AddLeadAssignmentAndDuplication) already added the
            // other columns but their designer snapshots were inconsistent, causing EF to
            // re-propose them here. Removing those to keep the DB idempotent.
            migrationBuilder.AddColumn<string>(
                name: "EnrichmentLastError",
                table: "Leads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrichmentRetryCount",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnrichmentLastError",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "EnrichmentRetryCount",
                table: "Leads");
        }
    }
}

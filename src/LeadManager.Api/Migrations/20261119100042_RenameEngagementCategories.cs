using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RenameEngagementCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Engagement categories got English values (the interface is English now).
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Active' WHERE EngagementCategory = 'Actief';");
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Maintenance' WHERE EngagementCategory = 'Beheercontract';");
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Reactive' WHERE EngagementCategory = 'Reactief';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Actief' WHERE EngagementCategory = 'Active';");
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Beheercontract' WHERE EngagementCategory = 'Maintenance';");
            migrationBuilder.Sql("UPDATE Clients SET EngagementCategory = 'Reactief' WHERE EngagementCategory = 'Reactive';");
        }
    }
}

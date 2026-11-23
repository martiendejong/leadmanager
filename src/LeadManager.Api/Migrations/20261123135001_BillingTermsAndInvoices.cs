using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class BillingTermsAndInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BillingAmount",
                table: "ClientServices",
                type: "TEXT",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingInterval",
                table: "ClientServices",
                type: "TEXT",
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<string>(
                name: "FreeReason",
                table: "ClientServices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FreeUntil",
                table: "ClientServices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "HourlyRate",
                table: "ClientServices",
                type: "TEXT",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNote",
                table: "ClientServices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidUntil",
                table: "ClientServices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatRate",
                table: "ClientServices",
                type: "TEXT",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    InvoiceDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AmountExclVat = table.Column<decimal>(type: "TEXT", precision: 14, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SentTo = table.Column<string>(type: "TEXT", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientInvoices_ClientDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ClientDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientInvoices_ClientServices_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "ClientServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClientInvoices_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInvoices_ClientId_InvoiceDate",
                table: "ClientInvoices",
                columns: new[] { "ClientId", "InvoiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInvoices_ClientId_SourceRef",
                table: "ClientInvoices",
                columns: new[] { "ClientId", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInvoices_DocumentId",
                table: "ClientInvoices",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientInvoices_ServiceId",
                table: "ClientInvoices",
                column: "ServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientInvoices");

            migrationBuilder.DropColumn(
                name: "BillingAmount",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "BillingInterval",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "FreeReason",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "FreeUntil",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "HourlyRate",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "InvoiceNote",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "PaidUntil",
                table: "ClientServices");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "ClientServices");
        }
    }
}

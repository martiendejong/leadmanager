using System.Globalization;
using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>
/// Covers ClickUp task 869ck3j4h / tracker task 834 "Lead export to CSV and Excel".
/// Verifies GET /api/leads/export?format=csv|xlsx (LeadsController.ExportLeads),
/// which was implemented in PR #29 but had zero automated test coverage.
/// </summary>
public class LeadsExportTests : IDisposable
{
    private const string UserId = "test-user-1";
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly LeadsController _controller;

    public LeadsExportTests()
    {
        // Keep-open in-memory SQLite connection so schema/data persist for the test lifetime,
        // matching the real provider used in Program.cs (UseSqlite).
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<LeadManagerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new LeadManagerDbContext(options);
        _db.Database.EnsureCreated();

        var enrichmentChannel = new EnrichmentChannel();
        var searchService = new SearchService(NullLogger<SearchService>.Instance);
        var configuration = new ConfigurationBuilder().Build();

        _controller = new LeadsController(_db, searchService, configuration, enrichmentChannel)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, UserId),
                    }, "TestAuth")),
                },
            },
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Lead> AddLeadAsync(
        string name, string email, string phone, string owner, string city, string sector,
        string? kvk = null, string? employees = null, float? googleRating = null,
        int? salesScore = null, bool isEnriched = false, DateTime? createdAt = null,
        string? otherUserId = null)
    {
        var lead = new Lead
        {
            Name = name,
            Website = $"https://{name.ToLower().Replace(" ", "")}.example.com",
            CompanyEmail = email,
            Phone = phone,
            OwnerName = owner,
            City = city,
            Sector = sector,
            KvkNumber = kvk,
            EmployeeCount = employees,
            GoogleRating = googleRating,
            SalesPriorityScore = salesScore,
            IsEnriched = isEnriched,
            Source = "manual",
            ImportedByUserId = otherUserId ?? UserId,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();
        return lead;
    }

    [Fact]
    public async Task Csv_Export_Returns_Correct_Headers_And_Rows_For_Filtered_Set()
    {
        await AddLeadAsync("Acme BV", "info@acme.example.com", "0201234567", "Jan Jansen",
            "Amsterdam", "IT", kvk: "12345678", employees: "10-50", googleRating: 4.5f,
            salesScore: 8, isEnriched: true);
        await AddLeadAsync("Beta NV", "info@beta.example.com", "0107654321", "Piet Pietersen",
            "Rotterdam", "Retail", isEnriched: false);
        // Different user's lead must never leak into export
        await AddLeadAsync("Other User Co", "x@x.com", "000", "X", "X", "X", otherUserId: "someone-else");

        var result = await _controller.ExportLeads(new LeadManager.Api.DTOs.LeadFilterParams(Enriched: true), "csv");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Contains("leads-", file.FileDownloadName);

        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.TrimEnd('\r')).ToList();

        // Header + exactly one filtered (enriched) row — Beta and the other user's lead excluded
        Assert.Equal(2, lines.Count);
        var header = lines[0].Split(',');
        Assert.Contains("naam", header);
        Assert.Contains("email", header);
        Assert.Contains("telefoon", header);
        Assert.Contains("contactpersoon", header);
        Assert.Contains("stad", header);
        Assert.Contains("sector", header);
        Assert.Contains("kvk", header);
        Assert.Contains("medewerkers", header);
        Assert.Contains("google_rating", header);
        Assert.Contains("sales_score", header);
        Assert.Contains("verrijkingsstatus", header);
        Assert.Contains("aangemaakt", header);

        Assert.Contains("Acme BV", lines[1]);
        Assert.Contains("Jan Jansen", lines[1]);
        Assert.DoesNotContain("Beta NV", text);
        Assert.DoesNotContain("Other User Co", text);
    }

    [Fact]
    public async Task Xlsx_Export_RoundTrips_Same_Data_Via_EPPlus()
    {
        var createdAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        await AddLeadAsync("Acme BV", "info@acme.example.com", "0201234567", "Jan Jansen",
            "Amsterdam", "IT", kvk: "12345678", employees: "10-50", googleRating: 4.5f,
            salesScore: 8, isEnriched: true, createdAt: createdAt);

        ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
        var result = await _controller.ExportLeads(new LeadManager.Api.DTOs.LeadFilterParams(), "xlsx");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType);

        using var package = new ExcelPackage(new MemoryStream(file.FileContents));
        var ws = package.Workbook.Worksheets["Leads"];
        Assert.NotNull(ws);
        Assert.Equal("Naam", ws!.Cells[1, 1].Text);
        Assert.Equal("Acme BV", ws.Cells[2, 1].Text);
        Assert.Equal("info@acme.example.com", ws.Cells[2, 2].Text);
        Assert.Equal("Jan Jansen", ws.Cells[2, 4].Text);
        Assert.Equal("Amsterdam", ws.Cells[2, 5].Text);
        Assert.Equal("12345678", ws.Cells[2, 7].Text);
        Assert.Equal("8", ws.Cells[2, 10].Text);
        Assert.Equal("Ja", ws.Cells[2, 11].Text);
        Assert.Equal("2026-01-15", ws.Cells[2, 12].Text);

        // Exactly 2 rows (header + 1 data row)
        Assert.Equal(2, ws.Dimension.Rows);
    }

    [Fact]
    public async Task Csv_Export_Empty_Result_Set_Does_Not_Crash()
    {
        // No leads seeded at all for this user
        var result = await _controller.ExportLeads(new LeadManager.Api.DTOs.LeadFilterParams(), "csv");

        var file = Assert.IsType<FileContentResult>(result);
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header row only, no exception thrown
        Assert.Single(lines);
        Assert.Contains("naam", lines[0]);
    }

    [Fact]
    public async Task Xlsx_Export_Empty_Result_Set_Does_Not_Crash()
    {
        ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
        var result = await _controller.ExportLeads(new LeadManager.Api.DTOs.LeadFilterParams(), "xlsx");

        var file = Assert.IsType<FileContentResult>(result);
        using var package = new ExcelPackage(new MemoryStream(file.FileContents));
        var ws = package.Workbook.Worksheets["Leads"];
        Assert.NotNull(ws);
        Assert.Equal("Naam", ws!.Cells[1, 1].Text);
        // Only header row present
        Assert.Equal(1, ws.Dimension.Rows);
    }

    [Fact]
    public async Task Export_Respects_Sort_Params_Like_The_Leads_List_Endpoint()
    {
        await AddLeadAsync("Zebra Co", "z@z.com", "1", "Z", "Zwolle", "Sector Z");
        await AddLeadAsync("Alpha Co", "a@a.com", "2", "A", "Arnhem", "Sector A");

        var result = await _controller.ExportLeads(
            new LeadManager.Api.DTOs.LeadFilterParams(SortBy: "name", SortDesc: false), "csv");

        var file = Assert.IsType<FileContentResult>(result);
        var text = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Contains("Alpha Co", lines[1]); // ascending by name -> Alpha first
        Assert.Contains("Zebra Co", lines[2]);
    }
}

using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>JengoWork task 8024: bulk import of cold-call leads with dry-run and deduplication.</summary>
public class CrmImportTests : IDisposable
{
    private const string Me = "user-me";
    private const string Other = "user-other";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public CrmImportTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private CrmImportController Controller(string user = Me) => new(_db, new CrmParentAccess(_db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user) }, "Test")),
            },
        },
    };

    private static ImportLeadDto Hogenkamp() => new(
        "Hogenkamp", "https://www.hogenkamp.nl", "Advocatuur en incasso", "Meppel", "088 244 25 00", "info@hogenkamp.nl",
        "cold-call-2026-09",
        new List<ImportContactDto> { new("Werner Hogenkamp", "Advocaat", "werner@hogenkamp.nl", "088 244 25 00", "06 52 373 595") },
        new List<ImportInteractionDto>
        {
            new("Email", "Inbound", new DateTime(2026, 9, 9, 12, 31, 0, DateTimeKind.Utc), "Vraagt scan en offerte", null, "mail:3:Sales:10"),
            new("Email", "Outbound", new DateTime(2026, 9, 10, 13, 26, 0, DateTimeKind.Utc), "Scan en offerte (EUR 600 excl btw) verstuurd", null, "mail:3:Sent:409"),
            new("Email", "Inbound", new DateTime(2026, 9, 14, 14, 10, 0, DateTimeKind.Utc), "Eerst eigen websitebouwer, nu geen telefonisch overleg", "later terugkomen", "mail:3:Sales:11"),
        },
        new List<ImportNextActionDto> { new("Follow-up: sitemap nog 404?", new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc)) });

    private static ImportReportDto Report(IActionResult result) =>
        Assert.IsType<ImportReportDto>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task DryRun_ReportsWhatWouldHappen_AndSavesNothing()
    {
        var report = Report(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Hogenkamp() }), dryRun: true));

        Assert.True(report.DryRun);
        Assert.Equal(1, report.LeadsCreated);
        var row = Assert.Single(report.Leads);
        Assert.Equal("created", row.Action);
        Assert.Equal(1, row.ContactsCreated);
        Assert.Equal(3, row.InteractionsCreated);
        Assert.Equal(1, row.NextActionsCreated);
        Assert.Null(row.LeadId);

        Assert.Empty(_db.Leads);
        Assert.Empty(_db.Contacts);
        Assert.Empty(_db.Interactions);
        Assert.Empty(_db.NextActions);
    }

    [Fact]
    public async Task RealImport_CreatesEverything_AndRerunIsIdempotent()
    {
        var request = new ImportRequestDto(new List<ImportLeadDto> { Hogenkamp() });

        var first = Report(await Controller().Import(request, dryRun: false));
        Assert.Equal(1, first.LeadsCreated);
        Assert.Single(_db.Leads);
        Assert.Single(_db.Contacts);
        Assert.Equal(3, _db.Interactions.Count());
        Assert.Single(_db.NextActions);
        Assert.Equal("cold-call-2026-09", _db.Leads.Single().Source);
        Assert.Equal(Me, _db.NextActions.Single().AssignedToUserId);

        var second = Report(await Controller().Import(request, dryRun: false));
        var row = Assert.Single(second.Leads);
        Assert.Equal("matched", row.Action);
        Assert.Equal(0, row.ContactsCreated);
        Assert.Equal(0, row.InteractionsCreated);
        Assert.Equal(3, row.InteractionsSkipped);
        Assert.Equal(0, row.NextActionsCreated);

        Assert.Single(_db.Leads);
        Assert.Single(_db.Contacts);
        Assert.Equal(3, _db.Interactions.Count());
        Assert.Single(_db.NextActions);
    }

    [Fact]
    public async Task MatchesExistingLeadOnWebsiteDomain_NotOnExactUrl()
    {
        _db.Leads.Add(new Lead { Name = "Hogenkamp Advocaten", Website = "http://hogenkamp.nl/", ImportedByUserId = Me });
        _db.SaveChanges();

        var report = Report(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Hogenkamp() }), dryRun: false));

        Assert.Equal(0, report.LeadsCreated);
        Assert.Equal(1, report.LeadsMatched);
        Assert.Single(_db.Leads);
        Assert.Equal(3, _db.Interactions.Count()); // history is attached to the existing lead
    }

    [Fact]
    public async Task DoesNotMatchAnotherUsersLead()
    {
        _db.Leads.Add(new Lead { Name = "Hogenkamp", Website = "https://hogenkamp.nl", ImportedByUserId = Other });
        _db.SaveChanges();

        var report = Report(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Hogenkamp() }), dryRun: false));

        Assert.Equal(1, report.LeadsCreated);
        Assert.Equal(2, _db.Leads.Count());
        Assert.All(_db.Interactions, i => Assert.Equal(Me, _db.Leads.Single(l => l.Id == i.LeadId).ImportedByUserId));
    }

    [Fact]
    public async Task InteractionWithoutSourceRef_IsStillIdempotent()
    {
        var lead = new ImportLeadDto("Beens", "https://notariskantoorbeens.nl", null, null, "038-3337051", null, null, null,
            new List<ImportInteractionDto> { new("Call", null, new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc), "Gebeld", "geen gehoor", null) },
            null);
        var request = new ImportRequestDto(new List<ImportLeadDto> { lead });

        await Controller().Import(request, dryRun: false);
        var again = Report(await Controller().Import(request, dryRun: false));

        Assert.Single(_db.Interactions);
        Assert.Equal(1, again.Leads[0].InteractionsSkipped);
    }

    [Fact]
    public async Task BadRows_AreReported_NotThrown()
    {
        var leads = new List<ImportLeadDto>
        {
            new("", "https://x.nl", null, null, null, null, null, null, null, null),
            new("Ok BV", "https://ok.nl", null, null, null, null, null,
                new List<ImportContactDto> { new(" ", null, null, null, null) },
                new List<ImportInteractionDto>
                {
                    new("Telepathy", null, null, "x", null, null),
                    new("Call", null, null, "", null, null),
                    new("Call", "Sideways", null, "x", null, null),
                },
                new List<ImportNextActionDto> { new("", DateTime.UtcNow) }),
        };

        var report = Report(await Controller().Import(new ImportRequestDto(leads), dryRun: false));

        Assert.Equal("invalid", report.Leads[0].Action);
        Assert.Equal(1, report.LeadsCreated);
        Assert.Equal(5, report.Leads[1].Problems.Count);
        Assert.Empty(_db.Interactions);
        Assert.Single(_db.Leads);
    }

    [Fact]
    public async Task Empty_And_Oversized_Requests_AreRejected()
    {
        Assert.IsType<BadRequestObjectResult>(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto>()), true));

        var many = Enumerable.Range(0, 501)
            .Select(i => new ImportLeadDto($"L{i}", $"https://l{i}.nl", null, null, null, null, null, null, null, null)).ToList();
        Assert.IsType<BadRequestObjectResult>(await Controller().Import(new ImportRequestDto(many), true));
    }

    [Fact]
    public async Task ImportedContact_ResolvesViaFind()
    {
        await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Hogenkamp() }), dryRun: false);
        var search = new CrmSearchController(_db, new CrmParentAccess(_db))
        {
            ControllerContext = Controller().ControllerContext,
        };

        var rows = Assert.IsAssignableFrom<IEnumerable<CrmMatchDto>>(
            Assert.IsType<OkObjectResult>(await search.Find("werner@hogenkamp.nl", null, null)).Value).ToList();

        Assert.Equal("Hogenkamp", Assert.Single(rows).Name);
    }
}

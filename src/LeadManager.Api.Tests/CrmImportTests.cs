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
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        TestOrgInterceptor.Join(_db, Me);
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

    // ---- Review findings (independent review of PR 49) ------------------------------------

    private static ImportLeadDto Minimal(string name, string? website, List<ImportContactDto>? contacts = null,
        List<ImportInteractionDto>? interactions = null, List<ImportNextActionDto>? actions = null) =>
        new(name, website, null, null, null, null, null, contacts, interactions, actions);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://example.com")]
    [InlineData("vbscript:x")]
    public async Task UnsafeWebsite_IsRefused_AndNoLeadIsCreated(string website)
    {
        var report = Report(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Minimal("Evil", website) }), dryRun: false));

        var row = Assert.Single(report.Leads);
        Assert.Equal("invalid", row.Action);
        Assert.Contains(row.Problems, p => p.Contains("http"));
        Assert.Empty(_db.Leads);
    }

    [Theory]
    [InlineData("hogenkamp.nl", "https://hogenkamp.nl")]
    [InlineData(" www.hogenkamp.nl ", "https://www.hogenkamp.nl")]
    [InlineData("http://hogenkamp.nl", "http://hogenkamp.nl")]
    public async Task WebsiteWithoutScheme_GetsHttps(string input, string expected)
    {
        await Controller().Import(new ImportRequestDto(new List<ImportLeadDto> { Minimal("H", input) }), dryRun: false);

        Assert.Equal(expected, _db.Leads.Single().Website);
    }

    [Fact]
    public async Task SameLeadTwiceInOnePayload_IsMerged_NotDuplicated_AndTheRealRunSucceeds()
    {
        // Before: the second row found no history in the database (the first was only staged), repeated
        // the contact, and the shared SourceRef broke the unique index so SaveChanges threw a 500.
        var shared = new List<ImportInteractionDto> { new("Email", "Inbound", new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc), "Reactie", null, "mail:3:Sales:10") };
        var contact = new List<ImportContactDto> { new("Werner", null, "werner@hogenkamp.nl", null, null) };
        var rows = new List<ImportLeadDto>
        {
            Minimal("Hogenkamp", "https://hogenkamp.nl", contact, shared),
            Minimal("Hogenkamp Advocaten", "https://www.hogenkamp.nl", contact, shared),
        };

        var dry = Report(await Controller().Import(new ImportRequestDto(rows), dryRun: true));
        var real = Report(await Controller().Import(new ImportRequestDto(rows), dryRun: false));

        // The dry run now predicts the real run exactly.
        Assert.Equal(dry.LeadsCreated, real.LeadsCreated);
        Assert.Equal(1, real.LeadsCreated);
        Assert.Single(_db.Leads);
        Assert.Single(_db.Contacts);
        Assert.Single(_db.Interactions);
        Assert.Equal(1, real.Leads[0].InteractionsCreated);
        Assert.Equal(0, real.Leads[1].InteractionsCreated);
        Assert.Equal(1, real.Leads[1].InteractionsSkipped);
        Assert.Contains(real.Leads[1].Problems, p => p.Contains("earlier in this import"));
    }

    [Fact]
    public async Task InteractionWithoutSourceRefAndWithoutTime_IsSkipped_SoRerunsCannotDoubleIt()
    {
        var rows = new List<ImportLeadDto>
        {
            Minimal("Beens", "https://beens.nl", interactions: new List<ImportInteractionDto> { new("Call", null, null, "Gebeld", null, null) }),
        };

        var first = Report(await Controller().Import(new ImportRequestDto(rows), dryRun: false));
        var second = Report(await Controller().Import(new ImportRequestDto(rows), dryRun: false));

        Assert.Empty(_db.Interactions);
        Assert.Contains(first.Leads[0].Problems, p => p.Contains("OccurredAt is required"));
        Assert.Contains(second.Leads[0].Problems, p => p.Contains("OccurredAt is required"));
    }

    [Fact]
    public async Task CompletedAction_IsNotResurrected_ByTheNextImport()
    {
        var request = new ImportRequestDto(new List<ImportLeadDto>
        {
            Minimal("Rein", "https://rein.nl", actions: new List<ImportNextActionDto> { new("Bellen", new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc)) }),
        });
        await Controller().Import(request, dryRun: false);

        _db.NextActions.Single().DoneAt = DateTime.UtcNow; // the user ticked it off
        _db.SaveChanges();
        var again = Report(await Controller().Import(request, dryRun: false));

        Assert.Equal(0, again.Leads[0].NextActionsCreated);
        var action = Assert.Single(_db.NextActions);
        Assert.NotNull(action.DoneAt);
    }

    [Fact]
    public async Task PerLeadLimits_AreEnforced()
    {
        var tooManyContacts = Enumerable.Range(0, 51).Select(i => new ImportContactDto($"C{i}", null, $"c{i}@x.nl", null, null)).ToList();
        var tooManyActions = Enumerable.Range(0, 51).Select(i => new ImportNextActionDto($"A{i}", DateTime.UtcNow)).ToList();

        var report = Report(await Controller().Import(new ImportRequestDto(new List<ImportLeadDto>
        {
            Minimal("Contacts", "https://c.nl", tooManyContacts),
            Minimal("Actions", "https://a.nl", actions: tooManyActions),
            Minimal(new string('n', CrmRules.MaxName + 1), "https://n.nl"),
        }), dryRun: false));

        Assert.All(report.Leads, r => Assert.Equal("invalid", r.Action));
        Assert.Empty(_db.Leads);
    }

    [Fact]
    public async Task NumericKinds_AreRefused_InImportToo()
    {
        var rows = new List<ImportLeadDto>
        {
            Minimal("Num", "https://num.nl", interactions: new List<ImportInteractionDto>
            {
                new("99", null, DateTime.UtcNow, "x", null, null),
                new("1", null, DateTime.UtcNow, "x", null, null),
                new("Call", "7", DateTime.UtcNow, "x", null, null),
            }),
        };

        var report = Report(await Controller().Import(new ImportRequestDto(rows), dryRun: false));

        Assert.Equal(3, report.Leads[0].Problems.Count);
        Assert.Empty(_db.Interactions);
    }

    [Fact]
    public void OccurredAtWithoutZone_IsReadAsUtc()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ImportInteractionDto>(
            "{\"Kind\":\"Call\",\"Summary\":\"x\",\"OccurredAt\":\"2026-09-10T15:26:14\"}")!;

        Assert.Equal(DateTimeKind.Utc, dto.OccurredAt!.Value.Kind);
        Assert.Equal(15, dto.OccurredAt.Value.Hour);
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

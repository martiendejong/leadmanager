using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The front page dashboard: figures and lists about the caller's own data only.</summary>
public class DashboardTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public DashboardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private DashboardController As(string userId) => new(_db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        },
    };

    private async Task<DashboardDto> Load(string user = "me") =>
        Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(await As(user).BuildAsync(Now)).Value);

    private Client Client(string name, string owner = "me", int? v = null, int? u = null, int? e = null, string? category = null)
    {
        var c = new Client { Name = name, CreatedByUserId = owner, PriorityValue = v, PriorityUrgency = u, PriorityEffort = e, EngagementCategory = category };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task EmptyAccount_GivesZerosAndEmptyLists()
    {
        var d = await Load();
        Assert.Equal(0, d.Counts.Clients);
        Assert.Empty(d.TopClients);
        Assert.Empty(d.RecentActivity);
    }

    [Fact]
    public async Task TopClients_AreOrderedByScore_AndUnscoredAreLeftOut()
    {
        Client("Low", v: 2, u: 2, e: 2);     // 1
        Client("High", v: 13, u: 8, e: 2);   // 26
        Client("Unscored");
        var d = await Load();
        Assert.Equal(new[] { "High", "Low" }, d.TopClients.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task NeedsAttention_ListsClientsWithoutNextAction_ExceptReactive()
    {
        var withAction = Client("Planned");
        Client("Nothing planned");
        Client("Reactive one", category: "Reactive");
        _db.NextActions.Add(new NextAction { ClientId = withAction.Id, Title = "call", DueAt = Now.AddDays(2), AssignedToUserId = "me" });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal(new[] { "Nothing planned" }, d.NoNextAction.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Silent_MeansNoContactForThirtyDays_OrNever()
    {
        var recent = Client("Recent");
        var old = Client("Old");
        Client("Never");
        _db.Interactions.Add(new Interaction { ClientId = recent.Id, Summary = "hi", OccurredAt = Now.AddDays(-3) });
        _db.Interactions.Add(new Interaction { ClientId = old.Id, Summary = "hi", OccurredAt = Now.AddDays(-45) });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal(new[] { "Never", "Old" }, d.Silent.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task ActionCounts_SeparateOverdueFromDueToday()
    {
        var c = Client("Acme");
        _db.NextActions.AddRange(
            new NextAction { ClientId = c.Id, Title = "late", DueAt = Now.AddDays(-2), AssignedToUserId = "me" },
            new NextAction { ClientId = c.Id, Title = "earlier today", DueAt = Now.AddHours(-2), AssignedToUserId = "me" },
            new NextAction { ClientId = c.Id, Title = "today", DueAt = Now.AddHours(2), AssignedToUserId = "me" },
            new NextAction { ClientId = c.Id, Title = "later", DueAt = Now.AddDays(5), AssignedToUserId = "me" },
            new NextAction { ClientId = c.Id, Title = "done", DueAt = Now.AddDays(-1), AssignedToUserId = "me", DoneAt = Now });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal(4, d.Counts.OpenActions);
        Assert.Equal(1, d.Counts.Overdue);
        Assert.Equal(2, d.Counts.DueToday);   // earlier today and later today both count as today
    }

    [Fact]
    public async Task Documents_OpenQuotesAmountAwaitingSignatureAndExpiring()
    {
        var c = Client("Acme");
        _db.ClientDocuments.AddRange(
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Quote, Status = DocumentStatus.Sent, Title = "Q1", Amount = 1000, DocumentDate = Now.AddDays(-5) },
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Quote, Status = DocumentStatus.Sent, Title = "Q2", Amount = 500, DocumentDate = Now.AddDays(-2) },
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Quote, Status = DocumentStatus.Signed, Title = "Won", Amount = 9999, DocumentDate = Now.AddDays(-20) },
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Invoice, Status = DocumentStatus.Sent, Title = "Invoice", Amount = 77, DocumentDate = Now.AddDays(-9) },
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Dpa, Status = DocumentStatus.Signed, Title = "DPA", DocumentDate = Now.AddDays(-300), ValidUntil = Now.AddDays(20) },
            new ClientDocument { ClientId = c.Id, Kind = DocumentKind.Nda, Status = DocumentStatus.Signed, Title = "Far", DocumentDate = Now.AddDays(-30), ValidUntil = Now.AddDays(400) });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal(2, d.Counts.OpenQuotes);
        Assert.Equal(1500m, d.Counts.OpenQuotesAmount);
        Assert.Equal(2, d.Counts.AwaitingSignature);
        Assert.Equal("Q1", d.AwaitingSignature[0].Title);   // oldest first
        Assert.Equal(new[] { "DPA" }, d.Expiring.Select(x => x.Title).ToArray());
        Assert.Equal("Acme", d.Expiring[0].ClientName);
    }

    [Fact]
    public async Task Projects_RunningCountExcludesConceptCompletedAndArchived_AndArchivedIsNotListed()
    {
        var c = Client("Acme");
        foreach (var s in new[] { "Concept", "Pilot", "Active", "Maintenance", "On Hold", "Completed", "Archived" })
            _db.Projects.Add(new Project { ClientId = c.Id, Name = s, Status = s });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal(3, d.Counts.ActiveProjects);
        Assert.False(d.ProjectsByStatus.ContainsKey("Archived"));
        Assert.Equal(6, d.ProjectsByStatus.Count);
    }

    [Fact]
    public async Task EverythingIsScopedToTheCaller()
    {
        var theirs = Client("Secret", owner: "other", v: 8, u: 8, e: 1);
        _db.NextActions.Add(new NextAction { ClientId = theirs.Id, Title = "x", DueAt = Now.AddDays(-1), AssignedToUserId = "other" });
        _db.Interactions.Add(new Interaction { ClientId = theirs.Id, Summary = "private", OccurredAt = Now });
        _db.ClientDocuments.Add(new ClientDocument { ClientId = theirs.Id, Kind = DocumentKind.Quote, Status = DocumentStatus.Sent, Title = "Q", Amount = 5 });
        _db.Leads.Add(new Lead { Name = "Their lead", ImportedByUserId = "other" });
        await _db.SaveChangesAsync();

        var d = await Load("me");
        Assert.Equal(0, d.Counts.Clients);
        Assert.Equal(0, d.Counts.Leads);
        Assert.Equal(0, d.Counts.OpenActions);
        Assert.Equal(0, d.Counts.OpenQuotes);
        Assert.Empty(d.TopClients);
        Assert.Empty(d.RecentActivity);
        Assert.Empty(d.LeadsByStage);
    }

    [Fact]
    public async Task RecentActivity_ShowsLatestFirst_WithNamesAndTruncatedSummaries()
    {
        var c = Client("Acme");
        var lead = new Lead { Name = "Lead BV", ImportedByUserId = "me" };
        _db.Leads.Add(lead);
        _db.Interactions.AddRange(
            new Interaction { ClientId = c.Id, Summary = new string('x', 300), OccurredAt = Now.AddHours(-1) },
            new Interaction { LeadId = lead.Id, Summary = "called", OccurredAt = Now.AddHours(-2) });
        await _db.SaveChangesAsync();

        var d = await Load();
        Assert.Equal("Acme", d.RecentActivity[0].ParentName);
        Assert.Equal(161, d.RecentActivity[0].Summary.Length);
        Assert.Equal("Lead BV", d.RecentActivity[1].ParentName);
        Assert.Equal("leads", d.RecentActivity[1].ParentType);
        Assert.Equal(1, d.LeadsByStage["New"]);
    }

    [Fact]
    public async Task ReactiveClients_AreNeverListedAsSilent()
    {
        Client("Reactive one", category: "Reactive");
        var d = await Load();
        Assert.Empty(d.Silent);
        Assert.Empty(d.NoNextAction);
    }
}

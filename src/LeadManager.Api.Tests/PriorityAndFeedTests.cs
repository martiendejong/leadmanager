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

/// <summary>Client priority score, the Acties overview and the ICS calendar feed.</summary>
public class PriorityAndFeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public PriorityAndFeedTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static T Auth<T>(T c, string userId) where T : ControllerBase
    {
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        };
        c.ControllerContext.HttpContext.Request.Scheme = "https";
        c.ControllerContext.HttpContext.Request.Host = new HostString("leads.test");
        return c;
    }

    private ClientsController Clients(string u = "me") => Auth(new ClientsController(_db), u);
    private NextActionsController Actions(string u = "me") => Auth(new NextActionsController(_db, _access), u);
    private CalendarFeedController Feed(string u = "me") => Auth(new CalendarFeedController(_db), u);

    private Client SeedClient(string name = "Acme", string owner = "me")
    {
        var c = new Client { Name = name, CreatedByUserId = owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Theory]
    [InlineData(8, 5, 2, 10.0)]    // 8*5/4
    [InlineData(13, 13, 1, 169.0)]
    [InlineData(1, 1, 144, 0.0)]   // rounds to 0.00
    public void Score_IsValueTimesUrgencyOverEffortSquared(int v, int u, int e, double expected) =>
        Assert.Equal(expected, ClientPriority.Score(v, u, e));

    [Fact]
    public void Score_IsZeroWhileASubScoreIsMissing()
    {
        Assert.Equal(0, ClientPriority.Score(null, 5, 2));
        Assert.Equal(0, ClientPriority.Score(5, null, 2));
        Assert.Equal(0, ClientPriority.Score(5, 5, null));
        Assert.Equal(0, ClientPriority.Score(5, 5, 0));
    }

    [Theory]
    [InlineData(4, 5)]     // tie between 3 and 5 goes up
    [InlineData(6, 5)]
    [InlineData(7, 8)]
    [InlineData(10, 8)]
    [InlineData(100, 89)]
    [InlineData(144, 144)]
    public void Snap_GoesToTheNearestFibonacci(int input, int expected) =>
        Assert.Equal(expected, ClientPriority.Snap(input));

    [Fact]
    public async Task UpdatePriority_SnapsStoresAndReturnsScore_OwnerOnly()
    {
        var c = SeedClient();
        var ok = Assert.IsType<OkObjectResult>(await Clients().UpdatePriority(c.Id, new UpdatePriorityDto(7, 5, 2)));
        var dto = Assert.IsType<ClientDto>(ok.Value);
        Assert.Equal(8, dto.PriorityValue);
        Assert.Equal(10.0, dto.Score);
        Assert.IsType<NotFoundResult>(await Clients("other").UpdatePriority(c.Id, new UpdatePriorityDto(1, 1, 1)));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 145, 1)]
    [InlineData(1, 1, -3)]
    public async Task UpdatePriority_RejectsOutOfRange(int v, int u, int e)
    {
        var c = SeedClient();
        Assert.IsType<BadRequestObjectResult>(await Clients().UpdatePriority(c.Id, new UpdatePriorityDto(v, u, e)));
    }

    [Fact]
    public async Task FullRecordUpdate_DoesNotWipeThePriority()
    {
        var c = SeedClient();
        await Clients().UpdatePriority(c.Id, new UpdatePriorityDto(8, 5, 2));
        await Clients().UpdateClient(c.Id, new UpdateClientDto("Acme 2", null, null, null, null, null, null, null, null, null));
        var stored = await _db.Clients.AsNoTracking().SingleAsync();
        Assert.Equal(8, stored.PriorityValue);
    }

    [Fact]
    public async Task ClientList_CarriesNextActionAndLastContact()
    {
        var c = SeedClient();
        var due = DateTime.UtcNow.AddDays(3);
        _db.NextActions.Add(new NextAction { ClientId = c.Id, Title = "bel", DueAt = due, AssignedToUserId = "me" });
        _db.NextActions.Add(new NextAction { ClientId = c.Id, Title = "later", DueAt = due.AddDays(5), AssignedToUserId = "me" });
        _db.Interactions.Add(new Interaction { ClientId = c.Id, Summary = "x", OccurredAt = DateTime.UtcNow.AddDays(-2) });
        await _db.SaveChangesAsync();

        var list = Assert.IsAssignableFrom<IEnumerable<ClientDto>>(Assert.IsType<OkObjectResult>(await Clients().GetClients()).Value).ToList();
        Assert.Equal(due, list[0].NextActionAt!.Value, TimeSpan.FromSeconds(1));
        Assert.NotNull(list[0].LastContactAt);
    }

    [Fact]
    public async Task Range_ReturnsMyOpenActionsWithParentNames_NotOthers()
    {
        var mine = SeedClient("Mijn klant");
        var theirs = SeedClient("Zijn klant", "other");
        var lead = new Lead { Name = "Mijn lead", ImportedByUserId = "me" };
        _db.Leads.Add(lead);
        var soon = DateTime.UtcNow.AddDays(2);
        _db.NextActions.AddRange(
            new NextAction { ClientId = mine.Id, Title = "a", DueAt = soon, AssignedToUserId = "me" },
            new NextAction { LeadId = lead.Id, Title = "b", DueAt = soon.AddHours(1), AssignedToUserId = "me" },
            new NextAction { ClientId = theirs.Id, Title = "c", DueAt = soon, AssignedToUserId = "other" },
            new NextAction { ClientId = mine.Id, Title = "klaar", DueAt = soon, AssignedToUserId = "me", DoneAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var rows = Assert.IsAssignableFrom<IEnumerable<ActionRowDto>>(Assert.IsType<OkObjectResult>(await Actions().Range(null, null)).Value).ToList();
        Assert.Equal(new[] { "a", "b" }, rows.Select(r => r.Title).ToArray());
        Assert.Equal("Mijn klant", rows[0].ParentName);
        Assert.Equal("leads", rows[1].ParentType);

        var withDone = Assert.IsAssignableFrom<IEnumerable<ActionRowDto>>(Assert.IsType<OkObjectResult>(await Actions().Range(null, null, true)).Value);
        Assert.Equal(3, withDone.Count());
    }

    [Fact]
    public async Task Range_RejectsAbsurdPeriod() =>
        Assert.IsType<BadRequestObjectResult>(await Actions().Range(DateTime.UtcNow, DateTime.UtcNow.AddYears(5)));

    [Fact]
    public async Task Feed_IsStablePerUser_AndRotationKillsTheOldUrl()
    {
        var first = Assert.IsType<CalendarFeedDto>(Assert.IsType<OkObjectResult>(await Feed().Get()).Value);
        var again = Assert.IsType<CalendarFeedDto>(Assert.IsType<OkObjectResult>(await Feed().Get()).Value);
        Assert.Equal(first.Token, again.Token);
        Assert.Equal(48, first.Token.Length);
        Assert.Equal($"https://leads.test/api/calendar/feed/{first.Token}.ics", first.Url);

        var rotated = Assert.IsType<CalendarFeedDto>(Assert.IsType<OkObjectResult>(await Feed().Rotate()).Value);
        Assert.NotEqual(first.Token, rotated.Token);
        Assert.IsType<NotFoundResult>(await Feed().Ics(first.Token));
        Assert.IsType<ContentResult>(await Feed().Ics(rotated.Token));
    }

    [Fact]
    public async Task Ics_ListsOnlyMyOpenActions_WithValidFormat_AndEscaping()
    {
        var mine = SeedClient("Jansen; Zn, BV");
        var theirs = SeedClient("Geheim", "other");
        var soon = DateTime.UtcNow.AddDays(2);
        _db.NextActions.AddRange(
            new NextAction { ClientId = mine.Id, Title = "Bel over offerte, graag", DueAt = soon, AssignedToUserId = "me" },
            new NextAction { ClientId = theirs.Id, Title = "NIET ZICHTBAAR", DueAt = soon, AssignedToUserId = "other" },
            new NextAction { ClientId = mine.Id, Title = "AFGEROND", DueAt = soon, AssignedToUserId = "me", DoneAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var token = Assert.IsType<CalendarFeedDto>(Assert.IsType<OkObjectResult>(await Feed().Get()).Value).Token;
        // The feed is anonymous: a controller without a user must still serve it.
        var anonymous = new CalendarFeedController(_db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        anonymous.ControllerContext.HttpContext.Request.Scheme = "https";
        anonymous.ControllerContext.HttpContext.Request.Host = new HostString("leads.test");
        var content = Assert.IsType<ContentResult>(await anonymous.Ics(token));
        var ics = content.Content!;

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(ics, "BEGIN:VEVENT").Count);
        Assert.Contains("Bel over offerte\\, graag", ics);
        Assert.Contains("Jansen\\; Zn\\, BV", ics);
        Assert.DoesNotContain("NIET ZICHTBAAR", ics);
        Assert.DoesNotContain("AFGEROND", ics);
        Assert.Equal("text/calendar; charset=utf-8", content.ContentType);
        foreach (var line in ics.Split("\r\n"))
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, "line too long: " + line);
    }

    [Theory]
    [InlineData("kort")]
    [InlineData("0123456789012345678901234567890123456789ABCDEFGH")]
    public async Task Ics_UnknownToken_IsNotFound(string token) =>
        Assert.IsType<NotFoundResult>(await Feed().Ics(token));

    [Fact]
    public async Task Range_KeepsOldOverdueOpenActionsVisible()
    {
        var c = SeedClient();
        _db.NextActions.Add(new NextAction { ClientId = c.Id, Title = "heel oud", DueAt = DateTime.UtcNow.AddDays(-200), AssignedToUserId = "me" });
        await _db.SaveChangesAsync();
        var rows = Assert.IsAssignableFrom<IEnumerable<ActionRowDto>>(Assert.IsType<OkObjectResult>(await Actions().Range(null, null)).Value);
        Assert.Contains(rows, r => r.Title == "heel oud" && r.Overdue);
    }
}

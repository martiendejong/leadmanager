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

/// <summary>Projects of a client, each with its own history (interactions filtered by ProjectId).</summary>
public class ProjectHistoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public ProjectHistoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static void Auth(ControllerBase c, string userId) =>
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        };

    private ProjectsController Projects(string user = "me") { var c = new ProjectsController(_db); Auth(c, user); return c; }
    private InteractionsController Interactions(string user = "me") { var c = new InteractionsController(_db, _access); Auth(c, user); return c; }

    private Client SeedClient(string name = "Acme", string owner = "me")
    {
        var c = new Client { Name = name, CreatedByUserId = owner };
        _db.Clients.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task CreateProject_ForOwnClient_Works_ForOthersClient_IsNotFound()
    {
        var client = SeedClient();
        var ok = Assert.IsType<CreatedResult>(await Projects().Create(client.Id, new UpsertProjectDto("Site", "omschrijving", null)));
        Assert.Equal("Active", Assert.IsType<ProjectDto>(ok.Value).Status);
        Assert.IsType<NotFoundResult>(await Projects("other").Create(client.Id, new UpsertProjectDto("Site2", null, null)));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Naam", "Bogus")]
    public async Task CreateProject_RejectsBadInput(string name, string? status)
    {
        var client = SeedClient();
        Assert.IsType<BadRequestObjectResult>(await Projects().Create(client.Id, new UpsertProjectDto(name, null, status)));
    }

    [Fact]
    public async Task UpdateAndDeleteProject_OnlyByOwner()
    {
        var client = SeedClient();
        var p = new Project { ClientId = client.Id, Name = "Site" };
        _db.Projects.Add(p);
        await _db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await Projects("other").Update(p.Id, new UpsertProjectDto("x", null, null)));
        Assert.IsType<NotFoundResult>(await Projects("other").Delete(p.Id));
        Assert.IsType<OkObjectResult>(await Projects().Update(p.Id, new UpsertProjectDto("Site v2", null, "Completed")));
        Assert.Equal("Completed", (await _db.Projects.FindAsync(p.Id))!.Status);
        Assert.IsType<NoContentResult>(await Projects().Delete(p.Id));
        Assert.Empty(await _db.Projects.ToListAsync());
    }

    [Fact]
    public async Task Interaction_WithProject_AppearsInProjectHistory_AndClientTimelineWithName()
    {
        var client = SeedClient();
        var a = new Project { ClientId = client.Id, Name = "A" };
        var b = new Project { ClientId = client.Id, Name = "B" };
        _db.Projects.AddRange(a, b);
        await _db.SaveChangesAsync();

        await Interactions().Create("clients", client.Id, new CreateInteractionDto("Note", null, null, "voor A", null, null, null, a.Id));
        await Interactions().Create("clients", client.Id, new CreateInteractionDto("Note", null, null, "voor B", null, null, null, b.Id));
        await Interactions().Create("clients", client.Id, new CreateInteractionDto("Note", null, null, "algemeen", null, null, null));

        var onlyA = Assert.IsAssignableFrom<IEnumerable<TimelineItemDto>>(Assert.IsType<OkObjectResult>(await Interactions().Timeline("clients", client.Id, 200, a.Id)).Value).ToList();
        Assert.Single(onlyA);
        Assert.Equal("voor A", onlyA[0].Summary);

        var all = Assert.IsAssignableFrom<IEnumerable<TimelineItemDto>>(Assert.IsType<OkObjectResult>(await Interactions().Timeline("clients", client.Id)).Value).ToList();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, i => i.Summary == "voor B" && i.ProjectName == "B");
        Assert.Contains(all, i => i.Summary == "algemeen" && i.ProjectId == null);
    }

    [Fact]
    public async Task Interaction_WithForeignOrLeadProject_IsRejected()
    {
        var mine = SeedClient("Mine");
        var theirs = SeedClient("Theirs", "other");
        var foreign = new Project { ClientId = theirs.Id, Name = "X" };
        _db.Projects.Add(foreign);
        var lead = new Lead { Name = "L", ImportedByUserId = "me" };
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await Interactions().Create("clients", mine.Id, new CreateInteractionDto("Note", null, null, "x", null, null, null, foreign.Id)));
        Assert.IsType<BadRequestObjectResult>(await Interactions().Create("leads", lead.Id, new CreateInteractionDto("Note", null, null, "x", null, null, null, foreign.Id)));
    }

    [Fact]
    public async Task DeletingProject_KeepsHistoryOnTheClient()
    {
        var client = SeedClient();
        var p = new Project { ClientId = client.Id, Name = "A" };
        _db.Projects.Add(p);
        await _db.SaveChangesAsync();
        await Interactions().Create("clients", client.Id, new CreateInteractionDto("Note", null, null, "blijft", null, null, null, p.Id));

        Assert.IsType<NoContentResult>(await Projects().Delete(p.Id));
        _db.ChangeTracker.Clear();
        var i = await _db.Interactions.SingleAsync();
        Assert.Null(i.ProjectId);
        Assert.Equal(client.Id, i.ClientId);
    }

    [Theory]
    [InlineData("Concept")]
    [InlineData("Pilot")]
    [InlineData("Maintenance")]
    [InlineData("On Hold")]
    [InlineData("Completed")]
    public async Task ProjectStatuses_AllAcceptedOnCreateAndUpdate(string status)
    {
        var client = SeedClient();
        var created = Assert.IsType<CreatedResult>(await Projects().Create(client.Id, new UpsertProjectDto("P", null, status)));
        var id = Assert.IsType<ProjectDto>(created.Value).Id;
        Assert.Equal(status, Assert.IsType<ProjectDto>(created.Value).Status);
        Assert.IsType<OkObjectResult>(await Projects().Update(id, new UpsertProjectDto("P", null, "Pilot")));
    }

    private EntityLinksController Links(string user = "me") { var c = new EntityLinksController(_db, _access); Auth(c, user); return c; }

    [Fact]
    public async Task ProjectLinks_AreSeparatedFromClientLinks_AndOwnedByTheClientsProject()
    {
        var client = SeedClient();
        var project = new Project { ClientId = client.Id, Name = "Platform" };
        var other = SeedClient("Other client");
        var foreign = new Project { ClientId = other.Id, Name = "Elsewhere" };
        _db.Projects.AddRange(project, foreign);
        await _db.SaveChangesAsync();

        Assert.IsType<CreatedAtActionResult>(await Links().Create("clients", client.Id, new CreateEntityLinkDto("GitHubRepo", "Backend", "https://github.com/x/y", null, project.Id)));
        Assert.IsType<CreatedAtActionResult>(await Links().Create("clients", client.Id, new CreateEntityLinkDto("JengoWorkBoard", "Client board", "https://tasks.example/board/1", null)));

        // The project of another client is refused, and a project link cannot be put on a lead.
        Assert.IsType<BadRequestObjectResult>(await Links().Create("clients", client.Id, new CreateEntityLinkDto("Web", "x", "https://x.example", null, foreign.Id)));
        var lead = new Lead { Name = "L", ImportedByUserId = "me" };
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Links().Create("leads", lead.Id, new CreateEntityLinkDto("Web", "x", "https://x.example", null, project.Id)));

        IEnumerable<EntityLinkDto> Of(IActionResult r) => Assert.IsAssignableFrom<IEnumerable<EntityLinkDto>>(Assert.IsType<OkObjectResult>(r).Value);
        Assert.Equal(new[] { "Client board" }, Of(await Links().List("clients", client.Id)).Select(l => l.Label).ToArray());
        Assert.Equal(new[] { "Backend" }, Of(await Links().List("clients", client.Id, project.Id)).Select(l => l.Label).ToArray());
        Assert.Equal(2, Of(await Links().List("clients", client.Id, null, true)).Count());
    }

    [Fact]
    public async Task ProjectLink_IsIdempotentPerProject_AndGoesWhenTheProjectGoes()
    {
        var client = SeedClient();
        var a = new Project { ClientId = client.Id, Name = "A" };
        var b = new Project { ClientId = client.Id, Name = "B" };
        _db.Projects.AddRange(a, b);
        await _db.SaveChangesAsync();
        var dto = new CreateEntityLinkDto("GitHubRepo", "Repo", "https://github.com/x/y", null, a.Id);

        await Links().Create("clients", client.Id, dto);
        Assert.IsType<OkObjectResult>(await Links().Create("clients", client.Id, dto));                       // same project: returned, not duplicated
        Assert.IsType<CreatedAtActionResult>(await Links().Create("clients", client.Id, dto with { ProjectId = b.Id })); // other project: its own link
        Assert.Equal(2, await _db.EntityLinks.CountAsync());

        Assert.IsType<NoContentResult>(await Projects().Delete(a.Id));
        Assert.Equal(1, await _db.EntityLinks.CountAsync());
    }

    [Fact]
    public async Task ArchivedStatus_IsAccepted()
    {
        var client = SeedClient();
        var created = Assert.IsType<CreatedResult>(await Projects().Create(client.Id, new UpsertProjectDto("Old", null, "Archived")));
        Assert.Equal("Archived", Assert.IsType<ProjectDto>(created.Value).Status);
    }
}

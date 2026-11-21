using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>JengoWork task 8087: a client can be added by hand, not only by converting a lead.</summary>
public class ClientCreateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public ClientCreateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private ClientsController As(string userId) => new(_db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        },
    };

    private static CreateClientDto Dto(string name = "Hogenkamp", string? website = null, string? category = null, string? email = null) =>
        new(name, null, "Jan", email, null, "Meppel", "Juridisch", website, "notitie", category);

    [Fact]
    public async Task Create_StoresClientForCaller_AndNormalisesWebsite()
    {
        var result = await As("me").CreateClient(Dto(website: "hogenkamp.nl", category: "Active"));
        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<ClientDto>(created.Value);
        Assert.Equal("https://hogenkamp.nl", dto.Website);
        Assert.Equal("me", dto.CreatedByUserId);
        Assert.Null(dto.SourceLeadId);
        Assert.Single(await _db.Clients.ToListAsync());
    }

    [Fact]
    public async Task Create_ThenListedForOwnerOnly()
    {
        await As("me").CreateClient(Dto());
        var mine = Assert.IsType<OkObjectResult>(await As("me").GetClients());
        var theirs = Assert.IsType<OkObjectResult>(await As("other").GetClients());
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ClientDto>>(mine.Value));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ClientDto>>(theirs.Value));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Create_RejectsEmptyName(string name) =>
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(Dto(name)));

    [Fact]
    public async Task Create_RejectsTooLongName() =>
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(Dto(new string('x', 201))));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x.nl")]
    public async Task Create_RejectsUnsafeWebsite(string site) =>
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(Dto(website: site)));

    [Fact]
    public async Task Create_RejectsUnknownCategory() =>
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(Dto(category: "VIP")));

    [Fact]
    public async Task Create_RejectsBadEmail() =>
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(Dto(email: "geen-email")));

    [Fact]
    public async Task Create_SameNameTwice_IsConflict_ButOtherUserMay()
    {
        Assert.IsType<CreatedAtActionResult>(await As("me").CreateClient(Dto("Acme")));
        Assert.IsType<ConflictObjectResult>(await As("me").CreateClient(Dto("ACME")));
        Assert.IsType<CreatedAtActionResult>(await As("other").CreateClient(Dto("Acme")));
        Assert.Equal(2, await _db.Clients.CountAsync());
    }

    [Fact]
    public async Task Create_TrimsAndDropsBlankOptionalFields()
    {
        var created = Assert.IsType<CreatedAtActionResult>(await As("me").CreateClient(
            new CreateClientDto("  Acme  ", "  ", " ", null, "", "", null, "  ", null, "")));
        var dto = Assert.IsType<ClientDto>(created.Value);
        Assert.Equal("Acme", dto.Name);
        Assert.Null(dto.Plan);
        Assert.Null(dto.Website);
        Assert.Null(dto.EngagementCategory);
    }

    [Fact]
    public async Task Create_WithProjects_StoresThemOnTheClient()
    {
        var dto = Dto("Acme") with { Projects = new() { new("Site", "nieuwe site", "Active"), new("Beheer", null, null) } };
        var created = Assert.IsType<CreatedAtActionResult>(await As("me").CreateClient(dto));
        var res = Assert.IsType<ClientDto>(created.Value);
        Assert.Equal(2, res.Projects.Count);
        Assert.Equal(2, await _db.Projects.CountAsync());
        Assert.Contains(res.Projects, p => p.Name == "Beheer" && p.Status == "Active");
    }

    [Fact]
    public async Task Create_WithBadProject_StoresNothing()
    {
        var dto = Dto("Acme") with { Projects = new() { new("ok", null, null), new("slecht", null, "Bogus") } };
        Assert.IsType<BadRequestObjectResult>(await As("me").CreateClient(dto));
        Assert.Empty(await _db.Clients.ToListAsync());
    }

    [Fact]
    public async Task Update_AppliesTheSameRulesAsCreate()
    {
        var created = Assert.IsType<CreatedAtActionResult>(await As("me").CreateClient(Dto("Acme")));
        var id = Assert.IsType<ClientDto>(created.Value).Id;
        Assert.IsType<BadRequestObjectResult>(await As("me").UpdateClient(id, new UpdateClientDto("  ", null, null, null, null, null, null, null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await As("me").UpdateClient(id, new UpdateClientDto("Acme", null, null, null, null, null, null, "javascript:alert(1)", null, null)));
        var ok = Assert.IsType<OkObjectResult>(await As("me").UpdateClient(id, new UpdateClientDto(" Acme 2 ", null, null, null, null, null, null, "acme.nl", null, "Active")));
        var dto = Assert.IsType<ClientDto>(ok.Value);
        Assert.Equal("Acme 2", dto.Name);
        Assert.Equal("https://acme.nl", dto.Website);
    }
}

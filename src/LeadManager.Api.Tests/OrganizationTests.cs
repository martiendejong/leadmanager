using System.Security.Claims;
using System.Text.Json;
using JengoComponents.McpConnector;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Mcp;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>
/// Leads are divided per organisation: a person sees and edits the leads of the organisations they are a member of and nothing else.
/// These tests set the organisation explicitly (no test interceptor), so they prove what the production code does.
/// </summary>
public class OrganizationTests : IDisposable
{
    // Anna: Prospergenics + Karibu Mara. Ben: Prospergenics only. Cleo: Port of Giethoorn only. Dirk: no organisation at all.
    private const string Anna = "user-anna";
    private const string Ben = "user-ben";
    private const string Cleo = "user-cleo";
    private const string Dirk = "user-dirk";

    private static readonly Guid Pg = OrganizationSeed.Prospergenics;
    private static readonly Guid Karibu = OrganizationSeed.KaribuMara;
    private static readonly Guid Pog = OrganizationSeed.PortOfGiethoorn;

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public OrganizationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
        foreach (var (id, first) in new[] { (Anna, "Anna"), (Ben, "Ben"), (Cleo, "Cleo"), (Dirk, "Dirk") })
            _db.Users.Add(new ApplicationUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), Email = $"{first.ToLower()}@example.test", NormalizedEmail = $"{first.ToUpper()}@EXAMPLE.TEST", FirstName = first });
        foreach (var id in new[] { Anna, Ben, Cleo }) _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = id, RoleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890" }); // may start hunts
        Member(Anna, Pg, OrganizationRoles.Admin);
        Member(Anna, Karibu, OrganizationRoles.Member);
        Member(Ben, Pg, OrganizationRoles.Member);
        Member(Cleo, Pog, OrganizationRoles.Admin);
        _db.SaveChanges();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private void Member(string user, Guid org, string role)
    {
        _db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = org, UserId = user, Email = Organizations.NormalizeEmail(_db.Users.Local.First(u => u.Id == user).Email), Role = role });
        _db.SaveChanges();
    }

    private T As<T>(T controller, string? user) where T : ControllerBase
    {
        var claims = user == null ? Array.Empty<System.Security.Claims.Claim>() : new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, user) };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private LeadsController Leads(string user) =>
        As(new LeadsController(_db, null!, new ConfigurationBuilder().Build(), new EnrichmentChannel()), user);

    private OrganizationsController Orgs(string user) => As(new OrganizationsController(_db), user);

    private Lead SeedLead(Guid org, string name = "Hogenkamp", string city = "Zwolle", string owner = Anna)
    {
        var lead = new Lead { Name = name, Website = $"https://{name.ToLower().Replace(' ', '-')}.example.nl", City = city, ImportedByUserId = owner, OrganizationId = org };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    private static T Value<T>(IActionResult r) where T : class => r switch
    {
        OkObjectResult ok => Assert.IsAssignableFrom<T>(ok.Value),
        CreatedResult c => Assert.IsAssignableFrom<T>(c.Value),
        CreatedAtActionResult c => Assert.IsAssignableFrom<T>(c.Value),
        ObjectResult o => Assert.IsAssignableFrom<T>(o.Value),
        _ => throw new Xunit.Sdk.XunitException($"Unexpected {r.GetType().Name}"),
    };

    private sealed record Row(Guid Id, Guid OrganizationId);

    private static List<Row> Items(IActionResult r)
    {
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value);
        return json.GetProperty("items").EnumerateArray().Select(e => new Row(e.GetProperty("Id").GetGuid(), e.GetProperty("OrganizationId").GetGuid())).ToList();
    }

    private static int Total(IActionResult r) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value).GetProperty("total").GetInt32();

    // ---- visibility ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMember_SeesTheLeadsOfTheirOrganisations_AndNoOthers()
    {
        var pg = SeedLead(Pg, "Alpha");
        var karibu = SeedLead(Karibu, "Beta");
        var pog = SeedLead(Pog, "Gamma");

        var anna = Items(await Leads(Anna).GetLeads(new LeadFilterParams()));
        Assert.Equal(new[] { pg.Id, karibu.Id }.OrderBy(x => x), anna.Select(l => l.Id).OrderBy(x => x));
        var ben = Items(await Leads(Ben).GetLeads(new LeadFilterParams()));
        Assert.Equal(pg.Id, Assert.Single(ben).Id);
        var cleo = Items(await Leads(Cleo).GetLeads(new LeadFilterParams()));
        Assert.Equal(pog.Id, Assert.Single(cleo).Id);
    }

    [Fact]
    public async Task ColleaguesSeeEachOthersLeads_NotOnlyTheirOwnImports()
    {
        var annasLead = SeedLead(Pg, "Annas", owner: Anna);

        // Ben never imported anything, but is in the same organisation.
        var lead = Value<LeadDto>(await Leads(Ben).GetLead(annasLead.Id));
        Assert.Equal(annasLead.Id, lead.Id);
        Assert.Equal(Pg, lead.OrganizationId);
    }

    [Fact]
    public async Task APersonOutsideTheOrganisation_GetsNotFoundOrEmpty_NeverTheData()
    {
        var secret = SeedLead(Pog, "Secret");

        Assert.IsType<NotFoundResult>(await Leads(Anna).GetLead(secret.Id));
        Assert.IsType<NotFoundResult>(await Leads(Dirk).GetLead(secret.Id));
        Assert.Equal(0, Total(await Leads(Anna).GetLeads(new LeadFilterParams())));

        // writes are refused the same way
        var dto = new UpdateLeadDto("Renamed", null, "", "", "", "", "", "", "", "", "", "");
        Assert.IsType<NotFoundResult>(await Leads(Anna).UpdateLead(secret.Id, dto));
        Assert.IsType<NotFoundObjectResult>(await Leads(Anna).MergeLead(SeedLead(Pg, "Mine").Id, new MergeLeadDto(secret.Id)));
        Assert.Equal("Secret", _db.Leads.Single(l => l.Id == secret.Id).Name);
    }

    [Fact]
    public async Task AUserWithoutAnyOrganisation_SeesNothing_AndCannotCreate()
    {
        SeedLead(Pg);

        Assert.Equal(0, Total(await Leads(Dirk).GetLeads(new LeadFilterParams())));
        Assert.Empty(Value<IEnumerable<OrganizationDto>>(await Orgs(Dirk).Mine()));

        var create = await Leads(Dirk).CreateLead(new CreateLeadDto("New", "https://new.example.nl", "", "", "", "", "test"));
        var bad = Assert.IsType<BadRequestObjectResult>(create);
        Assert.Contains("not a member of any organisation", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task NoUser_SeesNothing()
    {
        SeedLead(Pg);
        Assert.Equal(0, await _db.Leads.VisibleTo(_db, null).CountAsync());
        Assert.Equal(0, await _db.Leads.VisibleTo(_db, "").CountAsync());
    }

    [Fact]
    public async Task TheOrganisationFilter_NarrowsTheList_AndNeverWidensIt()
    {
        var pg = SeedLead(Pg, "Alpha");
        var karibu = SeedLead(Karibu, "Beta");
        SeedLead(Pog, "Gamma");

        Assert.Equal(pg.Id, Assert.Single(Items(await Leads(Anna).GetLeads(new LeadFilterParams(OrganizationId: Pg.ToString())))).Id);
        Assert.Equal(karibu.Id, Assert.Single(Items(await Leads(Anna).GetLeads(new LeadFilterParams(OrganizationId: "karibu-mara")))).Id);
        Assert.Equal(2, Items(await Leads(Anna).GetLeads(new LeadFilterParams(OrganizationId: $"{Pg},{Karibu}"))).Count);
        // an organisation the person is not in: nothing, not "everything"
        Assert.Empty(Items(await Leads(Anna).GetLeads(new LeadFilterParams(OrganizationId: Pog.ToString()))));
        Assert.Empty(Items(await Leads(Anna).GetLeads(new LeadFilterParams(OrganizationId: "no-such-org"))));
    }

    [Fact]
    public async Task NotesActivitiesAndMessages_FollowTheLead()
    {
        var lead = SeedLead(Pg, "Shared");

        Assert.IsType<CreatedAtActionResult>(await As(new LeadNotesController(_db), Anna).CreateNote(lead.Id, new CreateLeadNoteDto("called")));
        var notes = Value<IEnumerable<LeadNoteDto>>(await As(new LeadNotesController(_db), Ben).GetNotes(lead.Id));
        Assert.Single(notes);
        Assert.IsType<NotFoundObjectResult>(await As(new LeadNotesController(_db), Cleo).GetNotes(lead.Id));
        Assert.IsType<NotFoundObjectResult>(await As(new LeadNotesController(_db), Cleo).CreateNote(lead.Id, new CreateLeadNoteDto("intruder")));

        var messages = As(new MessagesController(_db), Anna);
        Assert.IsType<CreatedResult>(await messages.CreateDraft(lead.Id, new CreateMessageDto("Email", "Hello", "Body of the draft")));
        // a colleague sees the draft in the lead and in the overview; an outsider sees none
        Assert.Single(Value<IEnumerable<LeadMessageDto>>(await As(new MessagesController(_db), Ben).ForLead(lead.Id)));
        var overviewBen = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await As(new MessagesController(_db), Ben).List(null, null, null, null)).Value);
        Assert.Equal(1, overviewBen.GetProperty("total").GetInt32());
        var overviewCleo = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await As(new MessagesController(_db), Cleo).List(null, null, null, null)).Value);
        Assert.Equal(0, overviewCleo.GetProperty("total").GetInt32());
        Assert.IsType<NotFoundResult>(await As(new MessagesController(_db), Cleo).ForLead(lead.Id));
    }

    [Fact]
    public async Task TheMessageOverview_CanBeFilteredPerOrganisation()
    {
        var pg = SeedLead(Pg, "InPg");
        var karibu = SeedLead(Karibu, "InKaribu");
        var messages = As(new MessagesController(_db), Anna);
        await messages.CreateDraft(pg.Id, new CreateMessageDto("Email", "a", "body a"));
        await messages.CreateDraft(karibu.Id, new CreateMessageDto("Email", "b", "body b"));

        int Count(string? org) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            As(new MessagesController(_db), Anna).List(null, null, null, null, 1, 50, org).Result).Value).GetProperty("total").GetInt32();
        Assert.Equal(2, Count(null));
        Assert.Equal(1, Count("karibu-mara"));
        Assert.Equal(0, Count(Pog.ToString()));
    }

    [Fact]
    public async Task TodayAndDashboard_FollowTheOrganisationFilter()
    {
        var pg = SeedLead(Pg, "InPg");
        var karibu = SeedLead(Karibu, "InKaribu");
        foreach (var l in new[] { pg, karibu })
            _db.NextActions.Add(new NextAction { LeadId = l.Id, Title = "call " + l.Name, DueAt = DateTime.UtcNow.AddHours(-1), AssignedToUserId = Anna, CreatedByUserId = Anna });
        _db.SaveChanges();

        List<TodayActionDto> Today(string? org) => Value<List<TodayActionDto>>(As(new NextActionsController(_db, _access), Anna).TodayAsync(DateTime.UtcNow, org).Result);
        Assert.Equal(2, Today(null).Count);
        var only = Assert.Single(Today("karibu-mara"));
        Assert.Equal(Karibu, only.OrganizationId);

        var dash = Value<DashboardDto>(await As(new DashboardController(_db), Anna).BuildAsync(DateTime.UtcNow, "prospergenics"));
        Assert.Equal(1, dash.Counts.Leads);
    }

    // ---- create, move, merge -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithOneOrganisation_UsesIt_WithSeveral_RequiresAChoice()
    {
        var dto = new CreateLeadDto("OneOrg", "https://oneorg.example.nl", "", "", "", "", "test");
        var ben = Value<LeadDto>(await Leads(Ben).CreateLead(dto));
        Assert.Equal(Pg, ben.OrganizationId);

        var ambiguous = Assert.IsType<BadRequestObjectResult>(await Leads(Anna).CreateLead(dto with { Name = "Two", Website = "https://two.example.nl" }));
        Assert.Contains("Choose an organisation", JsonSerializer.Serialize(ambiguous.Value));

        var chosen = Value<LeadDto>(await Leads(Anna).CreateLead(dto with { Name = "Two", Website = "https://two.example.nl", OrganizationId = Karibu }));
        Assert.Equal(Karibu, chosen.OrganizationId);
    }

    [Fact]
    public async Task Create_InAnOrganisationYouAreNotIn_IsRefused()
    {
        var result = await Leads(Ben).CreateLead(new CreateLeadDto("X", "https://x.example.nl", "", "", "", "", "test", OrganizationId: Pog));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.Leads);
    }

    [Fact]
    public async Task TheSameBusiness_CanBeALeadOfTwoOrganisations_ButNotTwiceInOne()
    {
        var dto = new CreateLeadDto("Same BV", "https://same.example.nl", "", "", "", "", "test", OrganizationId: Pg);
        Assert.IsType<CreatedAtActionResult>(await Leads(Anna).CreateLead(dto));
        Assert.IsType<CreatedAtActionResult>(await Leads(Anna).CreateLead(dto with { OrganizationId = Karibu }));
        // the duplicate check works within the organisation
        Assert.IsType<ConflictObjectResult>(await Leads(Anna).CreateLead(dto));
        Assert.Equal(2, _db.Leads.Count());
    }

    [Fact]
    public async Task AMemberCanMoveALeadToAnotherOrganisationTheyAreIn_NotToOthers()
    {
        var lead = SeedLead(Pg, "Mover");
        UpdateLeadDto Update(Guid? org) => new("Mover", null, "", "", "", "", "", "", "", "", "", "", null, org);

        Assert.IsType<OkObjectResult>(await Leads(Anna).UpdateLead(lead.Id, Update(Karibu)));
        Assert.Equal(Karibu, _db.Leads.Single().OrganizationId);

        // Anna is not in Port of Giethoorn
        Assert.IsType<BadRequestObjectResult>(await Leads(Anna).UpdateLead(lead.Id, Update(Pog)));
        Assert.Equal(Karibu, _db.Leads.Single().OrganizationId);

        // Ben is not in Karibu Mara any more than he is allowed to see the lead there
        Assert.IsType<NotFoundResult>(await Leads(Ben).UpdateLead(lead.Id, Update(Pg)));
    }

    [Fact]
    public async Task Moving_IntoAnOrganisationThatHoldsTheSameBusiness_IsAConflict()
    {
        var mine = SeedLead(Pg, "Twin");
        SeedLead(Karibu, "Twin");
        var result = await Leads(Anna).UpdateLead(mine.Id, new UpdateLeadDto("Twin", null, "", "", "", "", "", "", "", "", "", "", null, Karibu));
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task LeadsOfDifferentOrganisations_CannotBeMerged()
    {
        var a = SeedLead(Pg, "A");
        var b = SeedLead(Karibu, "B");
        Assert.IsType<BadRequestObjectResult>(await Leads(Anna).MergeLead(a.Id, new MergeLeadDto(b.Id)));
        Assert.Equal(2, _db.Leads.Count());
    }

    [Fact]
    public async Task AssigningALead_ToSomeoneOutsideItsOrganisation_IsRefused()
    {
        var lead = SeedLead(Pg);
        Assert.IsType<OkObjectResult>(await Leads(Anna).AssignLead(lead.Id, new AssignLeadDto(Ben)));
        Assert.IsType<BadRequestObjectResult>(await Leads(Anna).AssignLead(lead.Id, new AssignLeadDto(Cleo)));
    }

    [Fact]
    public async Task CsvImport_GoesToTheChosenOrganisation()
    {
        var csv = "Name,Website\nCsv One,https://csv-one.example.nl\nCsv Two,https://csv-two.example.nl\n";
        IFormFile File() => new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)), 0, csv.Length, "file", "leads.csv");

        var needsChoice = await Leads(Anna).ImportLeadsCsv(File());
        Assert.IsType<BadRequestObjectResult>(needsChoice);

        var ok = await Leads(Anna).ImportLeadsCsv(File(), Karibu);
        Assert.Equal(2, Assert.IsType<CsvImportResultDto>(Assert.IsType<OkObjectResult>(ok).Value).Created);
        Assert.All(_db.Leads, l => Assert.Equal(Karibu, l.OrganizationId));
    }

    [Fact]
    public async Task CrmImport_MatchesWithinTheOrganisationOnly()
    {
        SeedLead(Karibu, "Known"); // https://known.example.nl in Karibu Mara
        var import = As(new CrmImportController(_db, _access), Anna);
        var request = new ImportRequestDto(new List<ImportLeadDto> { new("Known", "https://known.example.nl", null, null, null, null, "test", null, null, null) }, Pg);

        var report = Assert.IsType<ImportReportDto>(Assert.IsType<OkObjectResult>(await import.Import(request, dryRun: false)).Value);
        Assert.Equal(1, report.LeadsCreated); // not matched with the Karibu Mara lead
        Assert.Equal(2, _db.Leads.Count());
        Assert.Contains(_db.Leads, l => l.OrganizationId == Pg && l.Name == "Known");
    }

    // ---- members -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mine_ListsTheOrganisationsOfTheCaller_WithRoleAndCounts()
    {
        SeedLead(Pg, "L1"); SeedLead(Pg, "L2"); SeedLead(Karibu, "L3");
        var list = Value<IEnumerable<OrganizationDto>>(await Orgs(Anna).Mine()).ToList();
        Assert.Equal(new[] { "Karibu Mara", "Prospergenics" }, list.Select(o => o.Name));
        var pg = list.Single(o => o.Id == Pg);
        Assert.Equal(OrganizationRoles.Admin, pg.Role);
        Assert.Equal(2, pg.LeadCount);
        Assert.Equal(2, pg.MemberCount);
        Assert.Equal(OrganizationRoles.Member, list.Single(o => o.Id == Karibu).Role);
    }

    [Fact]
    public async Task Members_AreVisibleToMembersOnly()
    {
        Assert.Equal(2, Value<IEnumerable<OrganizationMemberDto>>(await Orgs(Ben).Members(Pg)).Count());
        Assert.IsType<NotFoundResult>(await Orgs(Cleo).Members(Pg));
        Assert.IsType<NotFoundResult>(await Orgs(Dirk).Members(Pg));
    }

    [Fact]
    public async Task AnAdmin_AddsAMemberByEmail_WhoThenSeesTheLeads()
    {
        var lead = SeedLead(Pg);
        Assert.IsType<NotFoundResult>(await Leads(Dirk).GetLead(lead.Id));

        var added = await Orgs(Anna).AddMember(Pg, new AddMemberDto("  Dirk@Example.test ", null));
        var member = Value<OrganizationMemberDto>(added);
        Assert.Equal("dirk@example.test", member.Email);
        Assert.Equal(OrganizationRoles.Member, member.Role);
        Assert.False(member.Pending); // Dirk has an account: resolved at once

        Assert.IsType<OkObjectResult>(await Leads(Dirk).GetLead(lead.Id));
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdmin_CannotChangeMembers()
    {
        var denied = Assert.IsType<ObjectResult>(await Orgs(Ben).AddMember(Pg, new AddMemberDto("dirk@example.test", null)));
        Assert.Equal(403, denied.StatusCode);
        var ann = _db.OrganizationMembers.Single(m => m.OrganizationId == Pg && m.UserId == Anna);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Orgs(Ben).RemoveMember(Pg, ann.Id)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Orgs(Ben).UpdateMember(Pg, ann.Id, new UpdateMemberDto("member"))).StatusCode);
        // and a person outside the organisation does not even learn that it exists
        Assert.IsType<NotFoundResult>(await Orgs(Cleo).AddMember(Pg, new AddMemberDto("dirk@example.test", null)));
        Assert.IsType<NotFoundResult>(await Orgs(Dirk).RemoveMember(Pg, ann.Id));
        Assert.Equal(2, _db.OrganizationMembers.Count(m => m.OrganizationId == Pg));
    }

    [Fact]
    public async Task AddMember_ValidatesEmailAndRole()
    {
        Assert.IsType<BadRequestObjectResult>(await Orgs(Anna).AddMember(Pg, new AddMemberDto("not an email", null)));
        Assert.IsType<BadRequestObjectResult>(await Orgs(Anna).AddMember(Pg, new AddMemberDto("x@example.test", "owner")));
    }

    [Fact]
    public async Task AdminsChangeRoles_AndRemoveMembers_ButTheLastAdminStays()
    {
        var ben = _db.OrganizationMembers.Single(m => m.OrganizationId == Pg && m.UserId == Ben);
        var anna = _db.OrganizationMembers.Single(m => m.OrganizationId == Pg && m.UserId == Anna);

        Assert.IsType<OkObjectResult>(await Orgs(Anna).UpdateMember(Pg, ben.Id, new UpdateMemberDto("admin")));
        Assert.Equal(OrganizationRoles.Admin, _db.OrganizationMembers.Single(m => m.Id == ben.Id).Role);
        Assert.IsType<OkObjectResult>(await Orgs(Anna).UpdateMember(Pg, ben.Id, new UpdateMemberDto("member")));

        // Anna is the only admin
        Assert.IsType<ConflictObjectResult>(await Orgs(Anna).UpdateMember(Pg, anna.Id, new UpdateMemberDto("member")));
        Assert.IsType<ConflictObjectResult>(await Orgs(Anna).RemoveMember(Pg, anna.Id));

        Assert.IsType<NoContentResult>(await Orgs(Anna).RemoveMember(Pg, ben.Id));
        var lead = SeedLead(Pg);
        Assert.IsType<NotFoundResult>(await Leads(Ben).GetLead(lead.Id)); // removed: no access any more
    }

    [Fact]
    public async Task AMemberAddedByEmailBeforeTheyHaveAnAccount_IsPending_UntilTheyLogIn()
    {
        var lead = SeedLead(Pg);
        var added = Value<OrganizationMemberDto>(await Orgs(Anna).AddMember(Pg, new AddMemberDto("newbie@example.test", "member")));
        Assert.True(added.Pending);
        Assert.Null(_db.OrganizationMembers.Single(m => m.Email == "newbie@example.test").UserId);

        // later the person logs in for the first time (SSO creates the user, then attaches)
        _db.Users.Add(new ApplicationUser { Id = "user-newbie", UserName = "newbie@example.test", Email = "Newbie@Example.test", NormalizedEmail = "NEWBIE@EXAMPLE.TEST" });
        _db.SaveChanges();
        Assert.Equal(0, await _db.Leads.VisibleTo(_db, "user-newbie").CountAsync()); // pending gives no access
        Assert.Equal(1, await Organizations.AttachPendingAsync(_db, "user-newbie", "Newbie@Example.test"));

        Assert.Equal(1, await _db.Leads.VisibleTo(_db, "user-newbie").CountAsync());
        Assert.IsType<OkObjectResult>(await Leads("user-newbie").GetLead(lead.Id));
        // a second attach changes nothing
        Assert.Equal(0, await Organizations.AttachPendingAsync(_db, "user-newbie", "newbie@example.test"));
    }

    [Fact]
    public async Task APendingMembership_IsNotAttachedToAnotherEmailAddress()
    {
        await Orgs(Anna).AddMember(Pg, new AddMemberDto("someone@example.test", null));
        Assert.Equal(0, await Organizations.AttachPendingAsync(_db, "user-dirk", "dirk@example.test"));
        Assert.Equal(0, await _db.Leads.VisibleTo(_db, Dirk).CountAsync());
    }

    [Fact]
    public async Task OnlyApplicationAdmins_CreateOrganisations_Via_TheRoleAttribute()
    {
        var method = typeof(OrganizationsController).GetMethod(nameof(OrganizationsController.Create))!;
        var authorize = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Contains(authorize, a => a.Roles == "Admin");
        Assert.NotNull(method.GetCustomAttributes(typeof(DenyApiKeyAttribute), true).FirstOrDefault());

        // the creator becomes its admin
        var created = Value<OrganizationDto>(await Orgs(Anna).Create(new CreateOrganizationDto("Brand New")));
        Assert.Equal("brand-new", created.Slug);
        Assert.Equal(OrganizationRoles.Admin, created.Role);
        Assert.IsType<ConflictObjectResult>(await Orgs(Anna).Create(new CreateOrganizationDto("Brand New")));
        await Task.CompletedTask;
    }

    // ---- hunts ----------------------------------------------------------------------------------------------------------

    private HuntController Hunts(string user)
    {
        var places = new GooglePlacesEnrichmentService(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sso:CallbackBaseUrl"] = "https://clients.example.test" }).Build();
        return As(new HuntController(_db, new FakeDispatcher(), places, new HuntQueue(), new HuntRegistry(), config), user);
    }

    [Fact]
    public async Task AHunt_BelongsToAnOrganisation_AndIsVisibleToItsMembers()
    {
        var accepted = await Hunts(Anna).Start(new StartHuntRequest("graphic designers", null, null, null, null, null, Karibu), default);
        Assert.IsType<AcceptedResult>(accepted);
        var job = _db.HuntJobs.Single();
        Assert.Equal(Karibu, job.OrganizationId);

        // Anna is in two organisations: starting without a choice is refused
        _db.HuntJobs.Remove(job);
        _db.SaveChanges();
        Assert.IsType<BadRequestObjectResult>(await Hunts(Anna).Start(new StartHuntRequest("graphic designers", null, null, null, null, null), default));

        // a colleague of the hunt's organisation can follow it; someone outside it cannot
        Assert.IsType<AcceptedResult>(await Hunts(Ben).Start(new StartHuntRequest("web shops", null, null, null, null, null), default));
        var jobId = _db.HuntJobs.Single().Id;
        Assert.IsType<OkObjectResult>(await Hunts(Anna).Get(jobId));
        Assert.IsType<NotFoundResult>(await Hunts(Cleo).Get(jobId));
        Assert.IsType<NotFoundResult>(await Hunts(Cleo).Cancel(jobId));
    }

    [Fact]
    public async Task StartingAHunt_InAnOrganisationYouAreNotIn_IsRefused()
    {
        Assert.IsType<BadRequestObjectResult>(await Hunts(Ben).Start(new StartHuntRequest("graphic designers", null, null, null, null, null, Pog), default));
        Assert.Empty(_db.HuntJobs);
    }

    [Fact]
    public async Task AVerify_OfLeadsFromTwoOrganisations_IsRefused()
    {
        var a = SeedLead(Pg, "A");
        var b = SeedLead(Karibu, "B");
        var result = await Hunts(Anna).VerifyMany(new VerifyLeadsRequest(new[] { a.Id, b.Id }));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- API keys act as their user ----------------------------------------------------------------------------------

    private async Task<(string text, bool isError)> McpTool(string userId, string tool, object? args = null)
    {
        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey { Name = "t", KeyHash = ApiKeyDefaults.Hash(key), Prefix = key[..8], UserId = userId });
        _db.SaveChanges();
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        http.Request.Host = new HostString("clients-test.example");
        var options = new McpConnectorOptions { ServerName = "t", ServerVersion = "1", Realm = "JengoClients" };
        var controller = new LeadManagerMcpController(options, new ApiKeyTokenValidator(_db), _db, _access, NullLogger<LeadManagerMcpController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        var body = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } });
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(body)).Value);
        var result = reply.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task AnApiKey_ActsAsItsUser_AndFollowsTheSameMembershipRules()
    {
        var lead = SeedLead(Pog, "Secret Giethoorn");

        // Anna's key: not a member of Port of Giethoorn
        var (searchText, _) = await McpTool(Anna, "search_leads", new { query = "secret" });
        Assert.DoesNotContain("Secret Giethoorn", searchText);
        var (getText, getError) = await McpTool(Anna, "get_lead", new { parent_type = "leads", parent_id = lead.Id });
        Assert.True(getError);
        Assert.DoesNotContain("Secret Giethoorn", getText);

        // Cleo's key sees it, with its organisation
        var (cleoText, cleoError) = await McpTool(Cleo, "get_lead", new { parent_type = "leads", parent_id = lead.Id });
        Assert.False(cleoError, cleoText);
        Assert.Contains("Port of Giethoorn", cleoText);

        // Dirk (no organisation) with a key sees nothing and cannot create
        var (dirkSearch, _) = await McpTool(Dirk, "search_leads", new { query = "secret" });
        Assert.DoesNotContain("Secret Giethoorn", dirkSearch);
        var (createText, createError) = await McpTool(Dirk, "create_lead", new { name = "Nope", website = "https://nope.example.nl" });
        Assert.True(createError);
        Assert.Contains("not a member of any organisation", createText);
    }

    [Fact]
    public async Task McpTools_TakeAnOrganisation_ForCreateAndSearch()
    {
        // list_organizations
        var (orgsText, orgsError) = await McpTool(Anna, "list_organizations");
        Assert.False(orgsError);
        Assert.Contains("Prospergenics", orgsText);
        Assert.Contains("Karibu Mara", orgsText);
        Assert.DoesNotContain("Port of Giethoorn", orgsText);

        // several organisations: the tool asks for a choice, a slug works, a foreign organisation does not
        var (needsChoice, needsError) = await McpTool(Anna, "create_lead", new { name = "Chosen", website = "https://chosen.example.nl" });
        Assert.True(needsError);
        Assert.Contains("Choose an organisation", needsChoice);
        var (okText, okError) = await McpTool(Anna, "create_lead", new { name = "Chosen", website = "https://chosen.example.nl", organization = "karibu-mara" });
        Assert.False(okError, okText);
        Assert.Equal(Karibu, _db.Leads.Single().OrganizationId);
        var (foreign, foreignError) = await McpTool(Anna, "create_lead", new { name = "Foreign", website = "https://foreign.example.nl", organization = "port-of-giethoorn" });
        Assert.True(foreignError);
        Assert.Contains("Unknown organisation", foreign);

        // the search filter
        SeedLead(Pg, "Chosen Too");
        var (inKaribu, _) = await McpTool(Anna, "search_leads", new { query = "chosen", organization = "karibu-mara" });
        Assert.Contains("\"Chosen\"", inKaribu);
        Assert.DoesNotContain("Chosen Too", inKaribu);
    }

    // ---- migration ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMigration_PutsExistingLeadsInAnOrganisation_GiethoornInPortOfGiethoorn_TheRestInProspergenics()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(conn).Options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // the database as it is in production: the migration before organisations
        migrator.Migrate("20261120200601_LeadDemoIdeas");
        void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        long Scalar(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return (long)c.ExecuteScalar()!; }
        const string cols = "(\"Id\",\"Name\",\"Website\",\"Sector\",\"City\",\"Phone\",\"CompanyEmail\",\"OwnerName\",\"OwnerFirstName\",\"OwnerLastName\",\"PersonalEmail\",\"AnymailfinderResult\",\"LinkedInUrl\",\"Source\",\"Status\",\"IsEnriched\",\"ImportedAt\",\"CreatedAt\",\"UpdatedAt\",\"WebsiteStatus\",\"EnrichmentVersion\",\"PagesCrawled\",\"ChunksIndexed\",\"IsPartOfGroup\",\"HasUploadedDocuments\",\"PipelineStatus\",\"EnrichmentAttempts\",\"WorkflowStep\",\"ImportedByUserId\")";
        void Lead(string id, string name, string city) => Exec(
            $"INSERT INTO \"Leads\" {cols} VALUES ('{id}','{name}','https://{name}.nl','','{city}','','','','','','','','','',0,0,'2026-01-01','2026-01-01','2026-01-01',0,0,0,0,0,0,0,0,1,'u1')");
        Lead("11111111-1111-1111-1111-111111111111", "boat-rental", "Giethoorn");
        Lead("22222222-2222-2222-2222-222222222222", "b-and-b", " giethoorn ");
        Lead("33333333-3333-3333-3333-333333333333", "design-studio", "Zwolle");
        Lead("44444444-4444-4444-4444-444444444444", "no-city", "");
        Exec("INSERT INTO \"LeadNotes\" (\"Id\",\"LeadId\",\"Content\",\"CreatedAt\",\"CreatedByUserId\") SELECT '55555555-5555-5555-5555-555555555555','33333333-3333-3333-3333-333333333333','n','2026-01-01',NULL WHERE 0");

        migrator.Migrate();

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM \"Leads\""));
        Assert.Equal(7, Scalar("SELECT COUNT(*) FROM \"Organizations\""));
        Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM \"Leads\" WHERE \"OrganizationId\" = '{Pog.ToString().ToUpperInvariant()}'"));
        Assert.Equal(2, Scalar($"SELECT COUNT(*) FROM \"Leads\" WHERE \"OrganizationId\" = '{Pg.ToString().ToUpperInvariant()}'"));
        // nothing is left without a valid organisation, and the foreign keys hold
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM \"Leads\" l WHERE NOT EXISTS (SELECT 1 FROM \"Organizations\" o WHERE o.\"Id\" = l.\"OrganizationId\")"));
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check";
            using var r = check.ExecuteReader();
            Assert.False(r.Read());
        }
        // the model works on the migrated database
        Assert.Equal(2, db.Leads.Count(l => l.OrganizationId == Pog));
        Assert.Equal("Port of Giethoorn", db.Leads.Where(l => l.Name == "boat-rental").Select(l => l.Organization!.Name).Single());
    }

    // ---- seeding -------------------------------------------------------------------------------------------------------------

    private const string SeedJson = """
        { "members": [
          { "name": "Anna", "email": "ANNA@example.test", "role": "admin", "orgs": ["Port of Giethoorn", "weerzien-mara"] },
          { "name": "Later", "email": "later@example.test", "orgs": ["Karibu Mara"] },
          { "name": "Broken", "email": "not-an-email", "orgs": ["Karibu Mara"] },
          { "name": "Nowhere", "email": "nowhere@example.test", "orgs": ["No Such Org"] }
        ] }
        """;

    [Fact]
    public async Task TheSeeder_AddsMembersByEmail_ResolvedWhenTheUserExists_PendingWhenNot()
    {
        var count = await OrganizationSeeder.SeedAsync(_db, SeedJson, NullLogger.Instance);

        Assert.Equal(3, count);
        var annaPog = _db.OrganizationMembers.Single(m => m.OrganizationId == Pog && m.Email == "anna@example.test");
        Assert.Equal(Anna, annaPog.UserId);
        Assert.Equal(OrganizationRoles.Admin, annaPog.Role);
        var later = _db.OrganizationMembers.Single(m => m.Email == "later@example.test");
        Assert.Null(later.UserId);
        Assert.Equal(OrganizationRoles.Member, later.Role);
        Assert.DoesNotContain(_db.OrganizationMembers, m => m.Email == "not-an-email" || m.Email == "nowhere@example.test");
    }

    [Fact]
    public async Task TheSeeder_AppliesEachPairOnce_SoARemovedMemberIsNotPutBack()
    {
        await OrganizationSeeder.SeedAsync(_db, SeedJson, NullLogger.Instance);
        var later = _db.OrganizationMembers.Single(m => m.Email == "later@example.test");
        _db.OrganizationMembers.Remove(later);
        _db.SaveChanges();

        Assert.Equal(0, await OrganizationSeeder.SeedAsync(_db, SeedJson, NullLogger.Instance));
        Assert.DoesNotContain(_db.OrganizationMembers, m => m.Email == "later@example.test");

        // a new line in the file is picked up
        var more = SeedJson.Replace("\"Nowhere\"", "\"Nowhere\"").Replace("{ \"name\": \"Later\"", "{ \"name\": \"Added\", \"email\": \"added@example.test\", \"orgs\": [\"Karibu Mara\"] }, { \"name\": \"Later\"");
        Assert.Equal(1, await OrganizationSeeder.SeedAsync(_db, more, NullLogger.Instance));
    }

    [Fact]
    public async Task TheSeeder_DoesNotOverwriteAMembershipThatExists_AndIgnoresBrokenJson()
    {
        // Ben is a plain member of Prospergenics; the file says admin: the existing row is left alone
        var json = """{ "members": [ { "email": "ben@example.test", "role": "admin", "orgs": ["Prospergenics"] } ] }""";
        await OrganizationSeeder.SeedAsync(_db, json, NullLogger.Instance);
        Assert.Equal(OrganizationRoles.Member, _db.OrganizationMembers.Single(m => m.OrganizationId == Pg && m.UserId == Ben).Role);

        Assert.Equal(0, await OrganizationSeeder.SeedAsync(_db, "{ not json", NullLogger.Instance));
        Assert.Equal(0, await OrganizationSeeder.SeedAsync(_db, null, NullLogger.Instance));
    }

    [Fact]
    public void TheShippedSeedFile_IsValid_AndNamesOnlyExistingOrganisations()
    {
        var json = OrganizationSeeder.ReadSource(new ConfigurationBuilder().Build());
        Assert.False(string.IsNullOrWhiteSpace(json));
        var file = JsonSerializer.Deserialize<OrganizationSeeder.SeedFile>(json!, new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip })!;
        var known = OrganizationSeed.All().ToList();
        Assert.NotEmpty(file.Members!);
        foreach (var person in file.Members!)
        {
            Assert.True(Organizations.LooksLikeEmail(Organizations.NormalizeEmail(person.Email)), person.Name);
            Assert.True(OrganizationRoles.IsValid(person.Role ?? "member"), person.Name);
            foreach (var org in person.Orgs!)
                Assert.Contains(known, o => string.Equals(o.Name, org, StringComparison.OrdinalIgnoreCase));
        }
        // Martien is admin of the six organisations he was given
        var martien = file.Members!.Single(m => m.Email == "info@martiendejong.nl");
        Assert.Equal("admin", martien.Role);
        Assert.Equal(6, martien.Orgs!.Count);
    }
}

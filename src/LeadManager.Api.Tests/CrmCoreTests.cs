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

/// <summary>
/// JengoWork task 8023: Jengo Clients CRM core (contacts, interactions, next actions,
/// typed links, API keys, lookup). Controller-level tests on real in-memory SQLite, so the
/// check constraint and unique indexes are exercised for real.
/// </summary>
public class CrmCoreTests : IDisposable
{
    private const string Me = "user-me";
    private const string Other = "user-other";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public CrmCoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private T As<T>(T controller, string userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test")),
            },
        };
        return controller;
    }

    private InteractionsController Interactions(string user = Me) => As(new InteractionsController(_db, _access), user);
    private NextActionsController NextActions(string user = Me) => As(new NextActionsController(_db, _access), user);
    private EntityLinksController Links(string user = Me) => As(new EntityLinksController(_db, _access), user);
    private ContactsController Contacts(string user = Me) => As(new ContactsController(_db, _access), user);
    private CrmSearchController Search(string user = Me) => As(new CrmSearchController(_db, _access), user);

    private Lead SeedLead(string name = "Hogenkamp", string website = "https://www.hogenkamp.nl", string owner = Me,
        string phone = "088 244 25 00", string? mobile = null, string email = "info@hogenkamp.nl")
    {
        var lead = new Lead { Name = name, Website = website, ImportedByUserId = owner, Phone = phone, OwnerMobile = mobile, CompanyEmail = email };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    private Client SeedClient(string name = "ThuisNotaris", string owner = Me)
    {
        var client = new Client { Name = name, CreatedByUserId = owner, Website = "https://thuisnotaris.nl", PrimaryContactEmail = "bas@thuisnotaris.nl" };
        _db.Clients.Add(client);
        _db.SaveChanges();
        return client;
    }

    private static T Value<T>(IActionResult result) where T : class
    {
        var body = result switch
        {
            OkObjectResult ok => ok.Value,
            CreatedAtActionResult created => created.Value,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected result {result.GetType().Name}"),
        };
        return Assert.IsAssignableFrom<T>(body);
    }

    // ---- Migration ------------------------------------------------------------------

    [Fact]
    public void Migrations_ApplyCleanlyOnEmptyDatabase_AndEnforceConstraints()
    {
        // The other tests use EnsureCreated (the model), which would hide a broken migration.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(conn).AddInterceptors(new TestOrgInterceptor()).Options);

        db.Database.Migrate();

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Contains(db.Database.GetAppliedMigrations(), m => m.EndsWith("AddCrmCore"));

        var lead = new Lead { Name = "M", Website = "https://m.nl", ImportedByUserId = Me };
        db.Leads.Add(lead);
        db.SaveChanges();
        db.Interactions.Add(new Interaction { LeadId = lead.Id, Summary = "a", SourceRef = "r1" });
        db.SaveChanges();

        db.Interactions.Add(new Interaction { LeadId = lead.Id, Summary = "dup", SourceRef = "r1" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        db.Interactions.Add(new Interaction { Summary = "orphan" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    // ---- Interactions ---------------------------------------------------------------

    [Fact]
    public async Task OutboundCall_LogsInteraction_AndMovesNewLeadToContacted()
    {
        var lead = SeedLead();

        var result = await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Call", null, null, "Gebeld, Werner niet bereikbaar", "geen gehoor", null, null));

        var dto = Value<InteractionDto>(result);
        Assert.Equal("Call", dto.Kind);
        Assert.Equal("Outbound", dto.Direction);
        Assert.Equal("geen gehoor", dto.Outcome);
        Assert.Equal(PipelineStatus.Contacted, _db.Leads.Single(l => l.Id == lead.Id).PipelineStatus);
    }

    [Fact]
    public async Task InboundMailAndNotes_DoNotMovePipeline()
    {
        var lead = SeedLead();

        await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Email", "Inbound", null, "Reactie ontvangen", null, null, null));
        await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Note", null, null, "Alleen een notitie", null, null, null));

        Assert.Equal(PipelineStatus.New, _db.Leads.Single(l => l.Id == lead.Id).PipelineStatus);
    }

    [Fact]
    public async Task Interaction_Validation_RejectsBadInput()
    {
        var lead = SeedLead();

        Assert.IsType<BadRequestObjectResult>(await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Call", null, null, "  ", null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Telepathy", null, null, "x", null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Call", "Sideways", null, "x", null, null, null)));
    }

    [Fact]
    public async Task SourceRef_IsIdempotent_AndDatabaseEnforcesIt()
    {
        var lead = SeedLead();
        var dto = new CreateInteractionDto("Email", "Outbound", null, "Offerte verstuurd", null, null, "mail:3:Sent:409");

        await Interactions().Create("leads", lead.Id, dto);
        var second = await Interactions().Create("leads", lead.Id, dto);

        Assert.IsType<OkObjectResult>(second); // existing returned, not a second row
        Assert.Equal(1, _db.Interactions.Count(i => i.LeadId == lead.Id));

        // Bypass the controller: the unique index itself must reject a duplicate.
        _db.Interactions.Add(new Interaction { LeadId = lead.Id, Summary = "dup", SourceRef = "mail:3:Sent:409" });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task SameSourceRef_OnDifferentParents_IsAllowed()
    {
        var a = SeedLead("A", "https://a.nl");
        var b = SeedLead("B", "https://b.nl");
        var dto = new CreateInteractionDto("Meeting", null, null, "Gesprek", null, null, "meet:XYZ");

        await Interactions().Create("leads", a.Id, dto);
        await Interactions().Create("leads", b.Id, dto);

        Assert.Equal(2, _db.Interactions.Count(i => i.SourceRef == "meet:XYZ"));
    }

    [Fact]
    public async Task Interaction_RejectsContactOfAnotherParent()
    {
        var a = SeedLead("A", "https://a.nl");
        var b = SeedLead("B", "https://b.nl");
        var contactOfB = Value<ContactDto>(await Contacts().Create("leads", b.Id,
            new UpsertContactDto("Piet", null, null, null, null, null, null, null)));

        var result = await Interactions().Create("leads", a.Id,
            new CreateInteractionDto("Call", null, null, "x", null, contactOfB.Id, null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void CheckConstraint_RejectsRowWithBothOrNeitherParent()
    {
        var lead = SeedLead();
        var client = SeedClient();

        _db.Interactions.Add(new Interaction { LeadId = lead.Id, ClientId = client.Id, Summary = "both" });
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
        _db.ChangeTracker.Clear();

        _db.Interactions.Add(new Interaction { Summary = "neither" });
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Fact]
    public async Task Timeline_MergesInteractionsAndActivities_NewestFirst()
    {
        var lead = SeedLead();
        var t0 = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        _db.Activities.Add(new LeadActivity { LeadId = lead.Id, ActivityType = ActivityType.Created, Note = "Aangemaakt", CreatedAt = t0 });
        _db.Interactions.Add(new Interaction { LeadId = lead.Id, Kind = InteractionKind.Call, Summary = "Gebeld", OccurredAt = t0.AddDays(2) });
        _db.Interactions.Add(new Interaction { LeadId = lead.Id, Kind = InteractionKind.Email, Summary = "Offerte", OccurredAt = t0.AddDays(1) });
        _db.SaveChanges();

        var items = Value<IEnumerable<TimelineItemDto>>(await Interactions().Timeline("leads", lead.Id)).ToList();

        Assert.Equal(new[] { "Gebeld", "Offerte", "Aangemaakt" }, items.Select(i => i.Summary));
        Assert.Equal(new[] { "interaction", "interaction", "activity" }, items.Select(i => i.Source));
    }

    [Fact]
    public async Task Timeline_ForClient_HasInteractionsOnly()
    {
        var client = SeedClient();
        await Interactions().Create("clients", client.Id,
            new CreateInteractionDto("Meeting", null, null, "Kick-off", null, null, null));

        var items = Value<IEnumerable<TimelineItemDto>>(await Interactions().Timeline("clients", client.Id)).ToList();

        Assert.Single(items);
        Assert.Equal("Kick-off", items[0].Summary);
    }

    // ---- Ownership ------------------------------------------------------------------

    [Fact]
    public async Task OtherUser_CannotReadOrWrite_AnyCrmDataOfMyLead()
    {
        var lead = SeedLead();
        var interaction = Value<InteractionDto>(await Interactions().Create("leads", lead.Id,
            new CreateInteractionDto("Call", null, null, "geheim", null, null, null)));
        var action = Value<NextActionDto>(await NextActions().Create("leads", lead.Id,
            new CreateNextActionDto("Terugbellen", DateTime.UtcNow, null, null)));
        var contact = Value<ContactDto>(await Contacts().Create("leads", lead.Id,
            new UpsertContactDto("Werner", null, null, null, null, null, null, null)));
        var link = Value<EntityLinkDto>(await Links().Create("leads", lead.Id,
            new CreateEntityLinkDto("Web", "Site", "https://hogenkamp.nl", null)));

        Assert.IsType<NotFoundResult>(await Interactions(Other).List("leads", lead.Id));
        Assert.IsType<NotFoundResult>(await Interactions(Other).Timeline("leads", lead.Id));
        Assert.IsType<NotFoundResult>(await Interactions(Other).Create("leads", lead.Id,
            new CreateInteractionDto("Note", null, null, "x", null, null, null)));
        Assert.IsType<NotFoundResult>(await Interactions(Other).Delete(interaction.Id));
        Assert.IsType<NotFoundResult>(await NextActions(Other).List("leads", lead.Id));
        Assert.IsType<NotFoundResult>(await NextActions(Other).Complete(action.Id));
        Assert.IsType<NotFoundResult>(await Contacts(Other).Update(contact.Id,
            new UpsertContactDto("Hacked", null, null, null, null, null, null, null)));
        Assert.IsType<NotFoundResult>(await Links(Other).Delete(link.Id));

        Assert.Equal("Werner", _db.Contacts.Single().Name);
        Assert.Single(_db.Interactions);
    }

    [Fact]
    public async Task UnknownParentType_IsNotFound()
    {
        var lead = SeedLead();
        Assert.IsType<NotFoundResult>(await Interactions().List("projects", lead.Id));
    }

    [Fact]
    public async Task DeletingLead_CascadesCrmData()
    {
        var lead = SeedLead();
        await Interactions().Create("leads", lead.Id, new CreateInteractionDto("Note", null, null, "x", null, null, null));
        await Contacts().Create("leads", lead.Id, new UpsertContactDto("Werner", null, null, null, null, null, null, null));

        _db.Leads.Remove(_db.Leads.Single(l => l.Id == lead.Id));
        _db.SaveChanges();

        Assert.Empty(_db.Interactions);
        Assert.Empty(_db.Contacts);
    }

    // ---- Next actions ---------------------------------------------------------------

    [Fact]
    public async Task Today_ListsOverdueAndDueToday_NotFutureNotDoneNotOthers()
    {
        var lead = SeedLead(mobile: "06 52 373 595");
        var client = SeedClient();
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc); // 11:00 in NL

        await NextActions().Create("leads", lead.Id, new CreateNextActionDto("Overdue", now.AddDays(-2), null, null));
        await NextActions().Create("leads", lead.Id, new CreateNextActionDto("Vanmiddag", now.AddHours(3), null, null));
        await NextActions().Create("clients", client.Id, new CreateNextActionDto("Morgen", now.AddDays(1), null, null));
        var done = Value<NextActionDto>(await NextActions().Create("leads", lead.Id,
            new CreateNextActionDto("Al gedaan", now.AddHours(-1), null, null)));
        await NextActions().Complete(done.Id);
        // An action assigned to someone else (legacy data) must not show up on my list. The API no
        // longer lets you assign to another user, so set it directly.
        var other = Value<NextActionDto>(await NextActions().Create("leads", lead.Id, new CreateNextActionDto("Voor ander", now, null, null)));
        _db.NextActions.Single(a => a.Id == other.Id).AssignedToUserId = Other;
        _db.SaveChanges();

        var rows = Value<IEnumerable<TodayActionDto>>(await NextActions().TodayAsync(now)).ToList();

        Assert.Equal(new[] { "Overdue", "Vanmiddag" }, rows.Select(r => r.Title));
        Assert.True(rows[0].Overdue);
        Assert.False(rows[1].Overdue);
        Assert.Equal("06 52 373 595", rows[0].Phone); // owner's mobile wins over the switchboard
        Assert.Equal("Hogenkamp", rows[0].ParentName);
    }

    [Fact]
    public async Task Today_FallsBackToSwitchboard_WhenNoMobile()
    {
        var lead = SeedLead(mobile: null);
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        await NextActions().Create("leads", lead.Id, new CreateNextActionDto("Bellen", now, null, null));

        var rows = Value<IEnumerable<TodayActionDto>>(await NextActions().TodayAsync(now)).ToList();

        Assert.Equal("088 244 25 00", Assert.Single(rows).Phone);
    }

    [Fact]
    public void EndOfDayNl_UsesDutchCalendarDay()
    {
        // 22:30 UTC on 18 Nov = 23:30 in NL (CET): still the 18th, ends 22:59:59 UTC.
        var late = NextActionsController.EndOfDayNl(new DateTime(2026, 11, 18, 22, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 11, 18, 22, 59, 59, DateTimeKind.Utc), late.AddTicks(-(late.Ticks % TimeSpan.TicksPerSecond)));

        // 23:30 UTC on 18 Nov = 00:30 on the 19th in NL: that day ends at 22:59:59 UTC on the 19th.
        var next = NextActionsController.EndOfDayNl(new DateTime(2026, 11, 18, 23, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 11, 19, 22, 59, 59, DateTimeKind.Utc), next.AddTicks(-(next.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public async Task NextAction_UpdateAndComplete()
    {
        var lead = SeedLead();
        var created = Value<NextActionDto>(await NextActions().Create("leads", lead.Id,
            new CreateNextActionDto("Bellen", DateTime.UtcNow, null, null)));
        Assert.Equal(Me, created.AssignedToUserId);

        var updated = Value<NextActionDto>(await NextActions().Update(created.Id,
            new UpdateNextActionDto("Terugbellen na 15:00", null, null)));
        Assert.Equal("Terugbellen na 15:00", updated.Title);

        var done = Value<NextActionDto>(await NextActions().Complete(created.Id));
        Assert.NotNull(done.DoneAt);

        var open = Value<IEnumerable<NextActionDto>>(await NextActions().List("leads", lead.Id)).ToList();
        Assert.Empty(open);
        var all = Value<IEnumerable<NextActionDto>>(await NextActions().List("leads", lead.Id, includeDone: true)).ToList();
        Assert.Single(all);
    }

    // ---- Links ----------------------------------------------------------------------

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    public async Task Link_RejectsUnsafeOrInvalidUrls(string url)
    {
        var lead = SeedLead();
        var result = await Links().Create("leads", lead.Id, new CreateEntityLinkDto("Web", "x", url, null));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.EntityLinks);
    }

    [Fact]
    public async Task Link_CreateIsIdempotent_AndAcceptsKnownKinds()
    {
        var lead = SeedLead();
        var dto = new CreateEntityLinkDto("JengoWorkBoard", "Bord Hogenkamp", "https://tasks.prospergenics.com/boards/20", "20");

        var first = await Links().Create("leads", lead.Id, dto);
        var second = await Links().Create("leads", lead.Id, dto);

        Assert.IsType<CreatedAtActionResult>(first);
        Assert.IsType<OkObjectResult>(second);
        Assert.Single(_db.EntityLinks);
        Assert.Equal(EntityLinkKind.JengoWorkBoard, _db.EntityLinks.Single().Kind);
    }

    [Fact]
    public async Task Link_RequiresLabelAndTarget_AndKnownKind()
    {
        var lead = SeedLead();
        Assert.IsType<BadRequestObjectResult>(await Links().Create("leads", lead.Id, new CreateEntityLinkDto("Web", "", "https://x.nl", null)));
        Assert.IsType<BadRequestObjectResult>(await Links().Create("leads", lead.Id, new CreateEntityLinkDto("Web", "x", null, null)));
        Assert.IsType<BadRequestObjectResult>(await Links().Create("leads", lead.Id, new CreateEntityLinkDto("Bogus", "x", "https://x.nl", null)));
    }

    // ---- API keys -------------------------------------------------------------------

    [Fact]
    public void ApiKey_GenerateAndHash()
    {
        var a = ApiKeyDefaults.Generate();
        var b = ApiKeyDefaults.Generate();

        Assert.StartsWith("lm_", a);
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        Assert.Equal(ApiKeyDefaults.Hash(a), ApiKeyDefaults.Hash(a));
        Assert.NotEqual(ApiKeyDefaults.Hash(a), ApiKeyDefaults.Hash(b));
        Assert.DoesNotContain(a, ApiKeyDefaults.Hash(a));
    }

    [Fact]
    public async Task ApiKeys_PlainKeyShownOnce_OnlyHashStored_AndRevocable()
    {
        var controller = As(new ApiKeysController(_db), Me);

        var created = Value<CreatedApiKeyDto>(await controller.Create(new CreateApiKeyDto("Jengo agent")));
        var stored = _db.ApiKeys.Single();

        Assert.StartsWith("lm_", created.Key);
        Assert.NotEqual(created.Key, stored.KeyHash);
        Assert.Equal(ApiKeyDefaults.Hash(created.Key), stored.KeyHash);
        Assert.Equal(Me, stored.UserId);

        var listed = Value<IEnumerable<ApiKeyDto>>(await controller.List()).ToList();
        Assert.Single(listed);
        Assert.DoesNotContain(created.Key, System.Text.Json.JsonSerializer.Serialize(listed));

        Assert.IsType<NotFoundResult>(await As(new ApiKeysController(_db), Other).Revoke(created.Id));
        Assert.IsType<NoContentResult>(await controller.Revoke(created.Id));
        Assert.NotNull(_db.ApiKeys.Single().RevokedAt);
    }

    [Fact]
    public void ApiKeysController_IsJwtOnly_SoAKeyCannotMintKeys()
    {
        // Regression guard (found in the HTTP smoke test): widening the DefaultPolicy to accept
        // API keys also widens this attribute, because ASP.NET merges the default policy in.
        // The "Smart" scheme in Program.cs avoids that; this pins the attribute itself.
        var attr = typeof(ApiKeysController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        Assert.Equal("Bearer", attr.AuthenticationSchemes);
    }

    // ---- Lookup ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.Hogenkamp.nl/over-ons?x=1", "hogenkamp.nl")]
    [InlineData("werner@hogenkamp.nl", "hogenkamp.nl")]
    [InlineData("hogenkamp.nl", "hogenkamp.nl")]
    [InlineData("localhost", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeDomain_Cases(string? input, string? expected) =>
        Assert.Equal(expected, CrmSearchController.NormalizeDomain(input));

    [Fact]
    public async Task Find_ByEmail_DomainAndName()
    {
        var lead = SeedLead();
        var client = SeedClient();

        var byEmail = Value<IEnumerable<CrmMatchDto>>(await Search().Find("INFO@hogenkamp.nl", null, null)).ToList();
        Assert.Equal(lead.Id, Assert.Single(byEmail).ParentId);
        Assert.Equal("email", byEmail[0].MatchedOn);

        var byOtherMailbox = Value<IEnumerable<CrmMatchDto>>(await Search().Find("werner@hogenkamp.nl", null, null)).ToList();
        Assert.Equal("domain", Assert.Single(byOtherMailbox).MatchedOn);

        var byName = Value<IEnumerable<CrmMatchDto>>(await Search().Find(null, null, "thuisnot")).ToList();
        Assert.Equal(client.Id, Assert.Single(byName).ParentId);
        Assert.Equal("clients", byName[0].ParentType);
    }

    [Fact]
    public async Task Find_FreeMailDomain_DoesNotMatchCompanyWebsites()
    {
        SeedLead("Gmail Shop", "https://gmail.com");

        var rows = Value<IEnumerable<CrmMatchDto>>(await Search().Find("iemand@gmail.com", null, null)).ToList();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Find_ResolvesContactEmailToItsParent()
    {
        var lead = SeedLead("REIN", "https://rein.nl", email: "info@rein.nl");
        await Contacts().Create("leads", lead.Id,
            new UpsertContactDto("Mw. Jansen", "Partner", "jansen@privaat-mail.example", null, "06 1", null, null, null));

        var rows = Value<IEnumerable<CrmMatchDto>>(await Search().Find("Jansen@Privaat-Mail.example", null, null)).ToList();

        var match = Assert.Single(rows);
        Assert.Equal(lead.Id, match.ParentId);
        Assert.Equal("contact", match.MatchedOn);
    }

    [Fact]
    public async Task Find_NeverReturnsOtherUsersData()
    {
        SeedLead(owner: Other);

        var rows = Value<IEnumerable<CrmMatchDto>>(await Search().Find("info@hogenkamp.nl", null, "hogenkamp")).ToList();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Find_RequiresAtLeastOneCriterion()
    {
        Assert.IsType<BadRequestObjectResult>(await Search().Find(null, null, "  "));
    }
}

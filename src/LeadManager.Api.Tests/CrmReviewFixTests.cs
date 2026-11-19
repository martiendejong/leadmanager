using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>
/// Findings of the independent review of PR 48 (task 8023): API key scope and user state, UTC timestamps,
/// input validation, race on SourceRef, search ownership.
/// </summary>
public class CrmReviewFixTests : IDisposable
{
    private const string Me = "user-me";
    private const string Other = "user-other";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;

    public CrmReviewFixTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new CrmParentAccess(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private T As<T>(T controller, string userId = Me) where T : ControllerBase
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

    private Lead SeedLead(string owner = Me, string name = "Hogenkamp", string website = "https://hogenkamp.nl")
    {
        var lead = new Lead { Name = name, Website = website, ImportedByUserId = owner, CompanyEmail = "info@hogenkamp.nl" };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
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

    // ---- API key: user state and scope --------------------------------------------------

    private async Task<AuthenticateResult> AuthenticateWithKey(string? key)
    {
        var context = new DefaultHttpContext();
        if (key != null) context.Request.Headers[ApiKeyDefaults.HeaderName] = key;

        var options = new StaticOptionsMonitor(new AuthenticationSchemeOptions());
        var handler = new ApiKeyAuthenticationHandler(options, NullLoggerFactory.Instance, UrlEncoder.Default, _db);
        await handler.InitializeAsync(new AuthenticationScheme(ApiKeyDefaults.Scheme, null, typeof(ApiKeyAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    private sealed class StaticOptionsMonitor(AuthenticationSchemeOptions value) : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;
        public AuthenticationSchemeOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private string SeedUserWithKey(bool active, out string plainKey, bool revoked = false)
    {
        var user = new ApplicationUser { UserName = "agent@example.test", Email = "agent@example.test", FirstName = "A", LastName = "", IsActive = active };
        _db.Users.Add(user);
        plainKey = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey
        {
            Name = "agent", KeyHash = ApiKeyDefaults.Hash(plainKey), Prefix = plainKey[..8], UserId = user.Id,
            RevokedAt = revoked ? DateTime.UtcNow : null,
        });
        _db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task ApiKey_OfActiveUser_Authenticates_AsThatUser()
    {
        var userId = SeedUserWithKey(active: true, out var key);

        var result = await AuthenticateWithKey(key);

        Assert.True(result.Succeeded);
        Assert.Equal(userId, result.Principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.True(result.Principal.HasClaim("auth_method", "api_key"));
    }

    [Fact]
    public async Task ApiKey_OfDeactivatedUser_IsRefused()
    {
        SeedUserWithKey(active: false, out var key);

        var result = await AuthenticateWithKey(key);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ApiKey_StopsWorking_WhenUserIsDeactivatedLater()
    {
        var userId = SeedUserWithKey(active: true, out var key);
        Assert.True((await AuthenticateWithKey(key)).Succeeded);

        _db.Users.Single(u => u.Id == userId).IsActive = false;
        _db.SaveChanges();

        Assert.False((await AuthenticateWithKey(key)).Succeeded);
    }

    [Fact]
    public async Task ApiKey_Revoked_Unknown_Malformed_AndMissing()
    {
        SeedUserWithKey(active: true, out var revokedKey, revoked: true);

        Assert.False((await AuthenticateWithKey(revokedKey)).Succeeded);
        Assert.False((await AuthenticateWithKey("lm_does-not-exist")).Succeeded);
        Assert.False((await AuthenticateWithKey("not-a-key")).Succeeded);
        Assert.True((await AuthenticateWithKey(null)).None);
    }

    [Fact]
    public void OnlyCrmControllers_AreMarkedAllowApiKey()
    {
        // A key must reach the CRM and nothing else (paid enrichment, profile, settings, intake approval).
        var controllers = typeof(CrmControllerBase).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(controllers);

        foreach (var type in controllers)
        {
            var allowed = type.GetCustomAttribute<AllowApiKeyAttribute>(inherit: true) != null;
            // Clients and Projects are CRM data too (task 8090): the same user, the same ownership rules.
            var isCrm = typeof(CrmControllerBase).IsAssignableFrom(type)
                || type.Name is nameof(ClientsController) or nameof(ProjectsController) or nameof(DocumentsController);
            Assert.True(allowed == isCrm, $"{type.Name}: AllowApiKey={allowed} but isCrm={isCrm}");
        }

        // Destructive actions stay closed to keys even inside a controller that allows them.
        foreach (var (controller, action) in new[] { (typeof(ClientsController), nameof(ClientsController.DeleteClient)),
                                                     (typeof(ProjectsController), nameof(ProjectsController.Delete)) })
            Assert.NotNull(controller.GetMethod(action)!.GetCustomAttribute<DenyApiKeyAttribute>());

        // Spot-check the ones that must stay closed.
        foreach (var name in new[] { nameof(LeadsController), nameof(IntakeController),
                                     nameof(EnrichmentController), nameof(ProfileController), nameof(SalesSettingsController),
                                     nameof(UsersController), nameof(ApiKeysController) })
        {
            var type = controllers.Single(t => t.Name == name);
            Assert.Null(type.GetCustomAttribute<AllowApiKeyAttribute>(inherit: true));
        }
    }

    // ---- UTC timestamps -----------------------------------------------------------------

    [Fact]
    public void Timestamps_AreWrittenWithZ_EvenWhenKindIsUnspecified()
    {
        // This is what SQLite returns: same instant, Kind=Unspecified.
        var unspecified = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Unspecified);
        var dto = new NextActionDto(Guid.NewGuid(), Guid.NewGuid(), null, null, "Bellen", unspecified, Me, null, unspecified);

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"dueAt\":\"2026-11-19T08:00:00Z\"", json.Replace("DueAt", "dueAt"));
        Assert.DoesNotContain("T08:00:00\"", json);
    }

    [Fact]
    public void Timestamps_WithoutZone_AreReadAsUtc_AndOffsetsAreConverted()
    {
        var plain = JsonSerializer.Deserialize<CreateNextActionDto>("{\"Title\":\"x\",\"DueAt\":\"2026-11-19T09:00:00\"}")!;
        Assert.Equal(DateTimeKind.Utc, plain.DueAt.Kind);
        Assert.Equal(9, plain.DueAt.Hour);

        var offset = JsonSerializer.Deserialize<CreateNextActionDto>("{\"Title\":\"x\",\"DueAt\":\"2026-11-19T09:00:00+01:00\"}")!;
        Assert.Equal(DateTimeKind.Utc, offset.DueAt.Kind);
        Assert.Equal(new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc), offset.DueAt);
    }

    [Fact]
    public async Task NextAction_RoundTripsThroughTheDatabase_AsUtcWithZ()
    {
        var lead = SeedLead();
        var created = Value<NextActionDto>(await As(new NextActionsController(_db, _access)).Create("leads", lead.Id,
            new CreateNextActionDto("Bellen", new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc), null, null)));
        _db.ChangeTracker.Clear(); // force a fresh read like a real request

        var listed = Value<IEnumerable<NextActionDto>>(await As(new NextActionsController(_db, _access)).List("leads", lead.Id)).Single();
        var json = JsonSerializer.Serialize(listed);

        Assert.Equal(created.DueAt, listed.DueAt);
        Assert.Contains("2026-11-19T08:00:00Z", json);
    }

    // ---- Validation ---------------------------------------------------------------------

    [Theory]
    [InlineData("Call", true)]
    [InlineData("call", true)]
    [InlineData("99", false)]
    [InlineData("1", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    [InlineData("Telepathy", false)]
    public void TryParseName_AcceptsNamesOnly(string input, bool expected) =>
        Assert.Equal(expected, CrmRules.TryParseName<InteractionKind>(input, out _));

    [Fact]
    public async Task Interaction_RejectsNumericKindsAndDirections_AndOverlongFields()
    {
        var lead = SeedLead();
        var c = As(new InteractionsController(_db, _access));

        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("99", null, null, "x", null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("1", null, null, "x", null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("Call", "7", null, "x", null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("Note", null, null, new string('x', CrmRules.MaxSummary + 1), null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("Note", null, null, "x", new string('x', CrmRules.MaxOutcome + 1), null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateInteractionDto("Note", null, null, "x", null, null, new string('x', CrmRules.MaxSourceRef + 1))));
        Assert.Empty(_db.Interactions);
    }

    [Fact]
    public async Task SourceRef_IsTrimmed_SoWhitespaceVariantsAreTheSameItem()
    {
        var lead = SeedLead();
        var c = As(new InteractionsController(_db, _access));

        await c.Create("leads", lead.Id, new CreateInteractionDto("Email", null, null, "a", null, null, " mail:3:Sent:1 "));
        var again = await c.Create("leads", lead.Id, new CreateInteractionDto("Email", null, null, "a", null, null, "mail:3:Sent:1"));
        var third = await c.Create("leads", lead.Id, new CreateInteractionDto("Email", null, null, "a", null, null, "  mail:3:Sent:1"));

        Assert.IsType<OkObjectResult>(again);
        Assert.IsType<OkObjectResult>(third);
        var row = Assert.Single(_db.Interactions);
        Assert.Equal("mail:3:Sent:1", row.SourceRef);
    }

    [Fact]
    public async Task NextAction_IsAlwaysAssignedToTheCaller()
    {
        var lead = SeedLead();

        var created = Value<NextActionDto>(await As(new NextActionsController(_db, _access)).Create("leads", lead.Id,
            new CreateNextActionDto("Bellen", DateTime.UtcNow, null, Other)));
        Assert.Equal(Me, created.AssignedToUserId);

        var updated = Value<NextActionDto>(await As(new NextActionsController(_db, _access)).Update(created.Id,
            new UpdateNextActionDto(null, null, Other)));
        Assert.Equal(Me, updated.AssignedToUserId);
    }

    [Fact]
    public async Task Link_NormalisesBeforeDuplicateCheck_AndRejectsOverlongUrl()
    {
        var lead = SeedLead();
        var c = As(new EntityLinksController(_db, _access));

        await c.Create("leads", lead.Id, new CreateEntityLinkDto("Web", "Site", "https://x.nl", null));
        var again = await c.Create("leads", lead.Id, new CreateEntityLinkDto("web", " Site ", "  https://x.nl  ", "  "));

        Assert.IsType<OkObjectResult>(again);
        Assert.Single(_db.EntityLinks);

        var longUrl = "https://x.nl/" + new string('a', CrmRules.MaxUrl);
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateEntityLinkDto("Web", "x", longUrl, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id, new CreateEntityLinkDto("99", "x", "https://y.nl", null)));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("linkedin.com/in/x")]
    public async Task Contact_RejectsUnsafeLinkedInUrl(string url)
    {
        var lead = SeedLead();
        var c = As(new ContactsController(_db, _access));

        var result = await c.Create("leads", lead.Id, new UpsertContactDto("Werner", null, null, null, null, url, null, null));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.Contacts);
    }

    [Fact]
    public async Task Contact_AcceptsHttpsLinkedIn_AndRejectsOverlongNotesAndNames()
    {
        var lead = SeedLead();
        var c = As(new ContactsController(_db, _access));

        Assert.IsType<CreatedAtActionResult>(await c.Create("leads", lead.Id,
            new UpsertContactDto("Werner", null, null, null, null, "https://www.linkedin.com/in/werner", null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id,
            new UpsertContactDto(new string('n', CrmRules.MaxName + 1), null, null, null, null, null, null, null)));
        Assert.IsType<BadRequestObjectResult>(await c.Create("leads", lead.Id,
            new UpsertContactDto("Ok", null, null, null, null, null, null, new string('n', CrmRules.MaxNotes + 1))));
    }

    // ---- Search -------------------------------------------------------------------------

    [Fact]
    public async Task Find_OtherUsersContactsWithTheSameEmail_CannotCrowdOutMine()
    {
        // Thirty contacts of another user share the address, more than the result cap. The caller's own
        // contact must still be found: ownership is part of the query, not a filter after the cap.
        var otherLead = SeedLead(owner: Other, name: "Other BV", website: "https://other.example");
        for (var i = 0; i < 30; i++)
            _db.Contacts.Add(new Contact { LeadId = otherLead.Id, Name = $"C{i}", Email = "shared@privaat.example" });
        var mine = SeedLead(owner: Me, name: "Mine BV", website: "https://mine.example");
        _db.Contacts.Add(new Contact { LeadId = mine.Id, Name = "Mine", Email = "shared@privaat.example" });
        _db.SaveChanges();

        var rows = Value<IEnumerable<CrmMatchDto>>(await As(new CrmSearchController(_db, _access)).Find("shared@privaat.example", null, null)).ToList();

        var match = Assert.Single(rows);
        Assert.Equal(mine.Id, match.ParentId);
        Assert.Equal("contact", match.MatchedOn);
    }

    [Fact]
    public async Task Find_DomainMatchIsExact_NotASubstring()
    {
        SeedLead(name: "Lookalike", website: "https://nothogenkamp.nl");
        var real = SeedLead(name: "Hogenkamp", website: "https://www.hogenkamp.nl");

        var rows = Value<IEnumerable<CrmMatchDto>>(await As(new CrmSearchController(_db, _access)).Find(null, "hogenkamp.nl", null)).ToList();

        Assert.Equal(real.Id, Assert.Single(rows).ParentId);
    }
}

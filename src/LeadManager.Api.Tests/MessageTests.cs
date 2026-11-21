using System.Security.Claims;
using System.Text.Json;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Messages;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using JengoComponents.McpConnector;

namespace LeadManager.Api.Tests;

/// <summary>Messages to leads: drafts, "Mark as sent", prefill, templates, bulk drafts, queue, overview, MCP. The app sends nothing.</summary>
public class MessageTests : IDisposable
{
    private const string Me = "user-me";
    private const string Other = "user-other";

    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;

    public MessageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static T As<T>(T controller, string userId = Me) where T : ControllerBase
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

    private MessagesController Messages(string user = Me) => As(new MessagesController(_db), user);
    private MessageTemplatesController Templates(string user = Me) => As(new MessageTemplatesController(_db), user);

    private void EnsureUser(string id)
    {
        if (_db.Users.Any(u => u.Id == id)) return;
        _db.Users.Add(new ApplicationUser { Id = id, UserName = id, Email = id + "@x.test", FirstName = "A", LastName = id, IsActive = true });
        _db.SaveChanges();
    }

    private Lead SeedLead(string owner = Me, string name = "Tine van Wel", string website = "https://tinevanwel.nl", PipelineStatus status = PipelineStatus.New, int score = 8)
    {
        var lead = new Lead
        {
            Name = name, Website = website, ImportedByUserId = owner, PipelineStatus = status, SalesPriorityScore = score,
            OwnerName = "Tine van Wel", OwnerFirstName = "Tine", CompanyEmail = "info@tinevanwel.nl",
            WhyLead = "Eigenaar bereikbaar. On its own site: \"Ik ben Tine, ontwerper en eigenaar van mijn eigen bureau\".",
            Needs = "Online zichtbaarheid: \"Mijn website is toe aan vernieuwing\"",
            ProductFitJson = "[{\"product\":\"siteaudit\",\"reason\":\"The site is dated.\",\"evidenceQuote\":\"Mijn website is toe aan vernieuwing\",\"sourceUrl\":\"https://tinevanwel.nl/\"}]",
        };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        return lead;
    }

    private static T Value<T>(IActionResult result) where T : class
    {
        var body = result switch { OkObjectResult ok => ok.Value, CreatedResult c => c.Value, _ => throw new Xunit.Sdk.XunitException($"Unexpected {result.GetType().Name}: {(result as ObjectResult)?.Value}") };
        return Assert.IsAssignableFrom<T>(body);
    }

    private async Task<LeadMessageDto> Draft(Lead lead, string channel = "Email", string subject = "Hallo", string body = "Een bericht", string user = Me) =>
        Value<LeadMessageDto>(await Messages(user).CreateDraft(lead.Id, new CreateMessageDto(channel, subject, body)));

    // ---- the status rules ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SavingADraft_NeverChangesStatusOrContactDate()
    {
        var lead = SeedLead();
        var m = await Draft(lead);
        await Messages().UpdateDraft(m.Id, new UpdateMessageDto("Email", "Nieuw onderwerp", "Nieuwe tekst"));
        var fresh = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        Assert.Equal(PipelineStatus.New, fresh.PipelineStatus);
        Assert.Null(fresh.LastContactedAt);
        Assert.Empty(_db.Activities.Where(a => a.LeadId == lead.Id));
    }

    [Fact]
    public async Task MarkSent_NeedsConfirmation()
    {
        var lead = SeedLead();
        var m = await Draft(lead);
        foreach (var dto in new[] { new MarkSentDto(null), new MarkSentDto(false) })
        {
            var bad = Assert.IsType<BadRequestObjectResult>(await Messages().MarkSent(m.Id, dto));
            Assert.Contains("confirmation_required", JsonSerializer.Serialize(bad.Value));
        }
        var fresh = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        Assert.Equal(PipelineStatus.New, fresh.PipelineStatus);
        Assert.Equal("Draft", (await _db.LeadMessages.AsNoTracking().SingleAsync(x => x.Id == m.Id)).Status);
    }

    [Fact]
    public async Task MarkSent_MovesNewToContacted_WritesActivities_AndSetsDates()
    {
        var lead = SeedLead();
        var m = await Draft(lead);
        var res = Value<object>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));
        var json = JsonSerializer.Serialize(res);
        Assert.Contains("\"statusChanged\":true", json);

        var fresh = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        Assert.Equal(PipelineStatus.Contacted, fresh.PipelineStatus);
        Assert.NotNull(fresh.LastContactedAt);
        var msg = await _db.LeadMessages.AsNoTracking().SingleAsync(x => x.Id == m.Id);
        Assert.Equal("Sent", msg.Status);
        Assert.NotNull(msg.SentAt);
        var acts = _db.Activities.Where(a => a.LeadId == lead.Id).ToList();
        Assert.Contains(acts, a => a.ActivityType == ActivityType.EmailSent && a.Note.Contains("Hallo"));
        Assert.Contains(acts, a => a.ActivityType == ActivityType.StatusChanged);
    }

    [Theory]
    [InlineData(PipelineStatus.Contacted)]
    [InlineData(PipelineStatus.Qualified)]
    [InlineData(PipelineStatus.ProposalSent)]
    [InlineData(PipelineStatus.Won)]
    [InlineData(PipelineStatus.Lost)]
    public async Task MarkSent_NeverDowngradesALaterStatus(PipelineStatus status)
    {
        var lead = SeedLead(status: status);
        var m = await Draft(lead);
        Value<object>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));
        var fresh = await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id);
        Assert.Equal(status, fresh.PipelineStatus);
        Assert.NotNull(fresh.LastContactedAt);
        Assert.DoesNotContain(_db.Activities.Where(a => a.LeadId == lead.Id), a => a.ActivityType == ActivityType.StatusChanged);
    }

    [Fact]
    public async Task MarkSent_Twice_IsRefused_AndASentMessageIsHistory()
    {
        var lead = SeedLead();
        var m = await Draft(lead);
        Value<object>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));
        Assert.IsType<ConflictObjectResult>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));
        Assert.IsType<ConflictObjectResult>(await Messages().UpdateDraft(m.Id, new UpdateMessageDto(null, null, "andere tekst")));
        Assert.IsType<ConflictObjectResult>(await Messages().DeleteDraft(m.Id));
        Assert.Equal(1, _db.Activities.Count(a => a.LeadId == lead.Id && a.ActivityType == ActivityType.EmailSent));
    }

    // ---- ownership -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OtherUsers_CannotSeeEditSendOrDeleteMyMessages()
    {
        var lead = SeedLead();
        var m = await Draft(lead);

        Assert.IsType<NotFoundResult>(await Messages(Other).CreateDraft(lead.Id, new CreateMessageDto("Email", "x", "y")));
        Assert.IsType<NotFoundResult>(await Messages(Other).ForLead(lead.Id));
        Assert.IsType<NotFoundResult>(await Messages(Other).Prefill(lead.Id, "Email", null));
        Assert.IsType<NotFoundResult>(await Messages(Other).UpdateDraft(m.Id, new UpdateMessageDto(null, null, "gekaapt")));
        Assert.IsType<NotFoundResult>(await Messages(Other).MarkSent(m.Id, new MarkSentDto(true)));
        Assert.IsType<NotFoundResult>(await Messages(Other).DeleteDraft(m.Id));
        Assert.IsType<NotFoundResult>(await Messages(Other).Mailto(m.Id));

        var list = Value<object>(await Messages(Other).List(null, null, null, null));
        Assert.Contains("\"total\":0", JsonSerializer.Serialize(list));
        var fresh = await _db.LeadMessages.AsNoTracking().SingleAsync(x => x.Id == m.Id);
        Assert.Equal("Een bericht", fresh.Body);
        Assert.Equal("Draft", fresh.Status);
        Assert.Equal(PipelineStatus.New, (await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id)).PipelineStatus);
    }

    [Fact]
    public async Task ABulkRequest_CannotReachOtherUsersLeads()
    {
        var mine = SeedLead();
        var theirs = SeedLead(Other, "Andermans");
        var res = Bulk(await Messages().BulkDrafts(new BulkDraftsDto(new[] { mine.Id, theirs.Id }, "Email", null)));
        Assert.Equal(1, res.Created);
        Assert.Equal(1, res.Skipped);
        Assert.Empty(_db.LeadMessages.Where(m => m.LeadId == theirs.Id));
    }

    private record BulkResult2(int Created, int Skipped);

    private static BulkResult2 Bulk(IActionResult r) => JsonSerializer.Deserialize<BulkResult2>(JsonSerializer.Serialize(Value<object>(r), Web), Web)!;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- validation: limits and header injection -------------------------------------------------------------------

    [Fact]
    public async Task LimitsAndClean_Up_AreEnforced()
    {
        var lead = SeedLead();
        Assert.IsType<BadRequestObjectResult>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("Email", "s", "  ")));
        Assert.IsType<BadRequestObjectResult>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("Email", "s", new string('a', MessageRules.MaxBody + 1))));
        Assert.IsType<BadRequestObjectResult>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("Telegram", "s", "x")));
        Assert.IsType<BadRequestObjectResult>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("Email", new string('s', MessageRules.MaxSubject * 2 + 1), "x")));

        var m = Value<LeadMessageDto>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("email", "Regel een\r\nBcc: boos@evil.test", "Hallo\u0000 daar\r\nTweede regel")));
        Assert.Equal("Email", m.Channel);
        Assert.DoesNotContain('\n', m.Subject);
        Assert.DoesNotContain('\r', m.Subject);
        Assert.DoesNotContain('\0', m.Body);
        Assert.Contains("Tweede regel", m.Body);

        // a subject only exists for e-mail
        var li = Value<LeadMessageDto>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("LinkedIn", "genegeerd", "Hoi")));
        Assert.Equal("", li.Subject);

        var longSubject = Value<LeadMessageDto>(await Messages().CreateDraft(lead.Id, new CreateMessageDto("Email", new string('s', MessageRules.MaxSubject + 50), "x")));
        Assert.True(longSubject.Subject.Length <= MessageRules.MaxSubject);
    }

    [Fact]
    public void Mailto_IsEncoded_AndCannotCarryAHeaderInjection()
    {
        var url = MessageRules.Mailto("info@tinevanwel.nl", "Hallo\r\nBcc: boos@evil.test", "Regel 1\nRegel 2 & meer = ?");
        Assert.NotNull(url);
        Assert.StartsWith("mailto:info@tinevanwel.nl?subject=", url);
        Assert.DoesNotContain("\r", url); Assert.DoesNotContain("\n", url); Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("Bcc:", url);                       // the colon and space are encoded and the subject is a single line
        Assert.Equal(1, url!.Split("subject=").Length - 1);
        Assert.Equal(1, url.Split("&body=").Length - 1);          // an & in the text cannot start a new parameter
        Assert.Contains("%0D%0A", url);                           // body line breaks are CRLF
        Assert.Contains("%26", url);
    }

    [Theory]
    [InlineData("info@x.nl,boos@evil.test")]
    [InlineData("info@x.nl?bcc=boos@evil.test")]
    [InlineData("info@x.nl\r\nBcc: a@b.nl")]
    [InlineData("a b@x.nl")]
    [InlineData("geen-adres")]
    [InlineData("")]
    [InlineData(null)]
    public void Mailto_IsOnlyBuiltForAValidPublishedAddress(string? address) => Assert.Null(MessageRules.Mailto(address, "s", "b"));

    [Fact]
    public void Mailto_IsNullWhenTheMessageIsTooLongForALink() =>
        Assert.Null(MessageRules.Mailto("info@x.nl", "s", new string('x', 3000)));

    [Fact]
    public async Task MailtoEndpoint_NeedsAnEmailMessageAndAValidAddress()
    {
        var lead = SeedLead();
        var email = await Draft(lead);
        var ok = Value<object>(await Messages().Mailto(email.Id));
        Assert.Contains("mailto:info@tinevanwel.nl", JsonSerializer.Serialize(ok));

        var li = await Draft(lead, "LinkedIn", "", "Hoi");
        Assert.IsType<BadRequestObjectResult>(await Messages().Mailto(li.Id));

        lead.CompanyEmail = "geen adres"; lead.PersonalEmail = "";
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Messages().Mailto(email.Id));
    }

    // ---- prefill and templates ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Prefill_IsDutchForADutchLead_AndUsesOnlyStoredFacts()
    {
        var lead = SeedLead();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.Equal("nl", p.Language);
        Assert.Contains("Hoi Tine", p.Body);
        Assert.Contains("Mijn website is toe aan vernieuwing", p.Body);
        Assert.Contains("Ik ben Tine, ontwerper en eigenaar van mijn eigen bureau", p.Body);   // the verified quote, not the internal score text
        Assert.DoesNotContain("Eigenaar bereikbaar", p.Body);
        Assert.Contains("Site Audit", p.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", p.Body);
        Assert.Contains("Tine van Wel", p.Subject);
        Assert.Equal("info@tinevanwel.nl", p.ToAddress);
        Assert.Empty(p.Warnings);
    }

    [Fact]
    public async Task Prefill_DutchLead_NeverContainsEnglishNeedLabelsOrReasons_AndHasDutchBenefitAndSignOff()
    {
        var lead = SeedLead();
        // a need without a quote is the agent's English text; the English reason is stored with the fit
        lead.Needs = "First contact is by phone or mail, all arranged by hand." + "\n" + "Online zichtbaarheid: \"Mijn website is toe aan vernieuwing\"";
        await _db.SaveChangesAsync();
        EnsureUser(Me);
        var user = _db.Users.Single(u => u.Id == Me); user.FirstName = "Frank"; user.LastName = "Tester"; await _db.SaveChangesAsync();

        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.Contains("Mijn website is toe aan vernieuwing", p.Body);
        Assert.DoesNotContain("First contact", p.Body);
        Assert.DoesNotContain("The site is dated", p.Body);
        Assert.Contains("Een maandelijkse Site audit", p.Body);                     // Dutch phrasing of the product
        Assert.EndsWith("Met vriendelijke groet," + "\n" + "Frank Tester", p.Body);          // the sign-off carries the user's name
    }

    [Fact]
    public async Task Prefill_NeedWithoutAnyQuote_LeavesTheLineOut()
    {
        var lead = SeedLead();
        lead.Needs = "Handmatige administratie";    // typed by a person, no quote
        await _db.SaveChangesAsync();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.DoesNotContain("Handmatige administratie", p.Body);
        Assert.DoesNotContain("Op uw website lees ik", p.Body);
    }

    [Fact]
    public async Task Prefill_WithoutAUserName_EndsAtTheGreetingLine_NoEmptyPlaceholder()
    {
        var lead = SeedLead();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.EndsWith("Met vriendelijke groet,", p.Body);
        Assert.DoesNotContain("{", p.Body);
    }

    [Fact]
    public void Benefits_ExistForEveryCatalogProduct_InBothLanguages()
    {
        foreach (var product in LeadManager.Api.Services.Hunt.ProductCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(MessageTemplates.Benefit(product.Key, "nl")), product.Key);
            Assert.False(string.IsNullOrWhiteSpace(MessageTemplates.Benefit(product.Key, "en")), product.Key);
            Assert.DoesNotContain("EUR", MessageTemplates.Benefit(product.Key, "nl"));      // no internal prices in a client text
            Assert.DoesNotContain("per month", MessageTemplates.Benefit(product.Key, "en"));
        }
    }

    private static string KenyaFit(string key) =>
        "[{\"product\":\"" + key + "\",\"reason\":\"Bookings only by phone.\",\"evidenceQuote\":\"Call or WhatsApp us to book\",\"sourceUrl\":\"https://mara-camp.example.ke/\"}]";

    [Theory]
    [InlineData("karibumara")]
    [InlineData("farmmgmt")]
    [InlineData("maaride")]
    public async Task Prefill_KenyaProducts_AreEnglish_EvenForADutchLookingLead_NoPriceNoDutch(string key)
    {
        var lead = SeedLead();   // .nl site, Dutch needs: language would be nl for any other product
        lead.ProductFitJson = KenyaFit(key);
        lead.Needs = "Booking: \"Call or WhatsApp us to book\"";
        await _db.SaveChangesAsync();
        EnsureUser(Me);
        var user = _db.Users.Single(u => u.Id == Me); user.FirstName = "Frank"; user.LastName = "Tester"; await _db.SaveChangesAsync();

        foreach (var channel in new[] { "Email", "LinkedIn", "WhatsApp", "Phone" })
        {
            var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, channel, key));
            Assert.Equal("en", p.Language);
            Assert.DoesNotContain("{", p.Body);
            Assert.DoesNotContain("Hoi", p.Body);
            Assert.DoesNotContain("EUR", p.Body);
            Assert.Contains("Call or WhatsApp us to book", p.Body);
            Assert.Contains(MessageTemplates.Benefit(key, "en"), channel == "Phone" ? MessageTemplates.Benefit(key, "en") : p.Body);
        }
        var mail = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", key));
        Assert.StartsWith("Hello Tine", mail.Body);
        Assert.EndsWith("Kind regards,\nFrank Tester", mail.Body);
        // the product comes from the stored fit when none is chosen
        var fromFit = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.Equal("en", fromFit.Language);
        Assert.StartsWith("Hello Tine", fromFit.Body);
    }

    [Fact]
    public void LanguageOf_KenyanLead_IsEnglish_AndOldKeysStayDutchForADutchLead()
    {
        var local = new Lead { Name = "Mara Camp", Website = "https://mara-camp.co.ke", Phone = "0712 345 678" };   // 07.. would look Dutch
        Assert.Equal("en", MessageTemplates.LanguageOf(local));
        var intl = new Lead { Name = "Rena", Website = "https://rena.example.com", Phone = "+254 712 345 678" };
        Assert.Equal("en", MessageTemplates.LanguageOf(intl));
        var dutch = new Lead { Name = "Bakker", Website = "https://bakker.nl", Phone = "06 12345678" };
        Assert.Equal("nl", MessageTemplates.LanguageOf(dutch));
        Assert.Equal("nl", MessageTemplates.LanguageOf(dutch, "siteaudit"));
        Assert.Equal("en", MessageTemplates.LanguageOf(dutch, "karibumara"));
        Assert.Equal("en", MessageTemplates.LanguageOf(dutch, " MaaRide "));
        Assert.Equal("nl", MessageTemplates.LanguageOf(dutch, "unknown"));
    }

    [Fact]
    public void KenyaProducts_HaveEnglishTemplatesForEveryChannel_AndOtherProductsKeepTheirTemplates()
    {
        Assert.Equal(new[] { "karibumara", "farmmgmt", "maaride" }, LeadManager.Api.Services.Hunt.ProductCatalog.EnglishFirst);
        Assert.All(LeadManager.Api.Services.Hunt.ProductCatalog.EnglishFirst, k => Assert.Contains(k, LeadManager.Api.Services.Hunt.ProductCatalog.Keys));
        foreach (var ch in new[] { "Email", "LinkedIn", "WhatsApp", "Phone" })
        {
            Assert.Equal("en", MessageTemplates.Get(ch, "nl", "maaride").Language);
            Assert.Equal("nl", MessageTemplates.Get(ch, "nl", "siteaudit").Language);
            Assert.Equal("nl", MessageTemplates.Get(ch, "nl", null).Language);
        }
        Assert.Equal(4, MessageTemplates.KenyaTemplates.Count);
    }

    [Fact]
    public async Task Prefill_IsEnglishForAnEnglishLead()
    {
        var lead = SeedLead(name: "Acme Ltd", website: "https://acme.example.com");
        lead.Phone = "+44 20 7946 0000"; await _db.SaveChangesAsync();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.Equal("en", p.Language);
        Assert.Contains("Hi Tine", p.Body);
        Assert.Contains("Kind regards", p.Body);
    }

    [Fact]
    public async Task Prefill_WithNothingStored_IsShortAndNeutral_NothingInvented()
    {
        var lead = new Lead { Name = "Bakkerij Zonder Data", Website = "https://bakkerij.nl", ImportedByUserId = Me };
        _db.Leads.Add(lead); await _db.SaveChangesAsync();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null));
        Assert.DoesNotContain("{", p.Body);
        Assert.StartsWith("Goedendag,", p.Body);                  // no owner known: a neutral greeting
        Assert.DoesNotContain("\"", p.Body);                      // no quotes: nothing was stored to quote
        Assert.Contains("Bakkerij Zonder Data", p.Body);
        Assert.True(p.Body.Length < 400);
        Assert.Contains(p.Warnings, w => w.Contains("e-mail address"));
    }

    [Theory]
    [InlineData("Email")] [InlineData("LinkedIn")] [InlineData("WhatsApp")] [InlineData("Phone")]
    public async Task Prefill_WorksForEveryChannel_SubjectOnlyForEmail(string channel)
    {
        var lead = SeedLead();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, channel, null));
        Assert.False(string.IsNullOrWhiteSpace(p.Body));
        Assert.Equal(channel == "Email", p.Subject.Length > 0);
    }

    [Fact]
    public async Task Prefill_UnknownProductOrChannel_IsRefused()
    {
        var lead = SeedLead();
        Assert.IsType<BadRequestObjectResult>(await Messages().Prefill(lead.Id, "Email", "trip"));
        Assert.IsType<BadRequestObjectResult>(await Messages().Prefill(lead.Id, "Fax", null));
    }

    [Fact]
    public async Task Prefill_WarnsAboutADoNotContactNote()
    {
        var lead = SeedLead();
        EnsureUser(Me);
        _db.LeadNotes.Add(new LeadNote { LeadId = lead.Id, Content = "Gebeld: meneer zei duidelijk niet bellen graag.", CreatedByUserId = Me });
        await _db.SaveChangesAsync();
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Phone", null));
        Assert.Contains(p.Warnings, w => w.Contains("not to contact"));
    }

    [Fact]
    public async Task OwnTemplates_AreUsed_ProductFirst_ThenDefault_ThenBuiltIn()
    {
        var lead = SeedLead();
        Value<object>(await Templates().Upsert(new UpsertTemplateDto("Email", "", "Standaard {name}", "Standaard voor {owner}: {need}")));
        Assert.StartsWith("Standaard voor Tine", Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null)).Body);

        Value<object>(await Templates().Upsert(new UpsertTemplateDto("Email", "SiteAudit", "Audit {name}", "Audit-tekst met {product}")));
        var p = Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", "siteaudit"));
        Assert.Equal("Audit Tine van Wel", p.Subject);
        Assert.StartsWith("Audit-tekst met", p.Body);

        // the product template is also what the best fit gives when the lead has that fit
        Assert.StartsWith("Audit-tekst", Value<PrefillDto>(await Messages().Prefill(lead.Id, "Email", null)).Body);
        // another channel is unaffected
        Assert.Equal("built-in template", Value<PrefillDto>(await Messages().Prefill(lead.Id, "WhatsApp", null)).TemplateSource);
        // another user has their own
        var other = SeedLead(Other, "Elders");
        Assert.Equal("built-in template", Value<PrefillDto>(await Messages(Other).Prefill(other.Id, "Email", null)).TemplateSource);
    }

    [Fact]
    public async Task TemplateValidation_UnknownPlaceholderProductChannelAndEmpty_AreRefused()
    {
        Assert.IsType<BadRequestObjectResult>(await Templates().Upsert(new UpsertTemplateDto("Email", "", "s", "Hoi {nam}")));
        Assert.IsType<BadRequestObjectResult>(await Templates().Upsert(new UpsertTemplateDto("Email", "trip", "s", "Hoi")));
        Assert.IsType<BadRequestObjectResult>(await Templates().Upsert(new UpsertTemplateDto("Fax", "", "s", "Hoi")));
        Assert.IsType<BadRequestObjectResult>(await Templates().Upsert(new UpsertTemplateDto("Email", "", "s", " ")));
        Assert.IsType<BadRequestObjectResult>(await Templates().Upsert(new UpsertTemplateDto("Email", "", "s", new string('a', MessageRules.MaxBody + 1))));
        Assert.Empty(_db.MessageTemplates);
    }

    [Fact]
    public async Task Template_UpsertReplaces_AndDeleteIsOwnerScoped()
    {
        Value<object>(await Templates().Upsert(new UpsertTemplateDto("LinkedIn", "", "", "Een")));
        Value<object>(await Templates().Upsert(new UpsertTemplateDto("LinkedIn", "", "", "Twee")));
        var t = Assert.Single(_db.MessageTemplates);
        Assert.Equal("Twee", t.Body);
        Assert.IsType<NotFoundResult>(await Templates(Other).Delete(t.Id));
        Assert.IsType<NoContentResult>(await Templates().Delete(t.Id));
        Assert.Empty(_db.MessageTemplates);
    }

    [Fact]
    public void TemplateRendering_OneLineWithoutValueIsLeftOut_AndAStackOfBlankLinesCollapses()
    {
        var v = new TemplateValues("Acme", "", "", "", "", "", "nl");
        var text = MessageTemplates.RenderBody("Hoi {owner},\n\nRegel met {need}\n\nRegel met {why}\n\nGroet", v);
        Assert.Equal("Goedendag,\n\nGroet", text);
    }

    [Fact]
    public void TemplateSubject_CannotContainLineBreaks_EvenFromAValue()
    {
        var v = new TemplateValues("Acme\r\nBcc: x@y.nl", "", "", "", "", "", "nl");
        var s = MessageTemplates.RenderSubject("Hallo {name}", v);
        Assert.DoesNotContain('\n', s); Assert.DoesNotContain('\r', s);
    }

    // ---- bulk, queue, overview, filters ----------------------------------------------------------------------------

    [Fact]
    public async Task BulkDrafts_CreatesDraftsOnly_SkipsExistingDrafts_AndNeverChangesStatus()
    {
        var a = SeedLead(name: "A"); var b = SeedLead(name: "B");
        await Draft(a);
        var res = Bulk(await Messages().BulkDrafts(new BulkDraftsDto(new[] { a.Id, b.Id, b.Id }, "Email", null)));
        Assert.Equal(1, res.Created);
        Assert.Equal(1, res.Skipped);
        Assert.All(_db.Leads.AsNoTracking(), l => { Assert.Equal(PipelineStatus.New, l.PipelineStatus); Assert.Null(l.LastContactedAt); });
        Assert.All(_db.LeadMessages.AsNoTracking(), m => Assert.Equal("Draft", m.Status));
        Assert.Empty(_db.Activities);
    }

    [Fact]
    public async Task BulkDrafts_Validation()
    {
        var a = SeedLead();
        Assert.IsType<BadRequestObjectResult>(await Messages().BulkDrafts(new BulkDraftsDto(null, "Email", null)));
        Assert.IsType<BadRequestObjectResult>(await Messages().BulkDrafts(new BulkDraftsDto(new Guid[0], "Email", null)));
        Assert.IsType<BadRequestObjectResult>(await Messages().BulkDrafts(new BulkDraftsDto(new[] { a.Id }, "Fax", null)));
        Assert.IsType<BadRequestObjectResult>(await Messages().BulkDrafts(new BulkDraftsDto(new[] { a.Id }, "Email", "trip")));
        var many = Enumerable.Range(0, MessagesController.MaxBulk + 1).Select(_ => Guid.NewGuid()).ToArray();
        Assert.IsType<BadRequestObjectResult>(await Messages().BulkDrafts(new BulkDraftsDto(many, "Email", null)));
    }

    [Fact]
    public async Task Queue_ListsOnlyNewUncontactedHighScoreLeadsOfTheUser_BestFirst()
    {
        var high = SeedLead(name: "Hoog", score: 9);
        var mid = SeedLead(name: "Midden", score: 7);
        SeedLead(name: "Laag", score: 3);
        SeedLead(name: "Al benaderd", status: PipelineStatus.Contacted, score: 9);
        SeedLead(Other, "Andermans", score: 9);
        var contacted = SeedLead(name: "Net gemarkeerd", score: 9);
        var m = await Draft(contacted);
        Value<object>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));

        var items = Assert.IsAssignableFrom<IEnumerable<QueueItemDto>>(Assert.IsType<OkObjectResult>(await Messages().Queue(20, 6)).Value).ToList();
        Assert.Equal(new[] { "Hoog", "Midden" }, items.Select(i => i.Name).ToArray());
        Assert.Equal(high.Id, items[0].Id);
        Assert.NotNull(items[0].ProductFit);
        Assert.False(items[0].HasDraft);
        await Draft(mid);
        var again = Assert.IsAssignableFrom<IEnumerable<QueueItemDto>>(Assert.IsType<OkObjectResult>(await Messages().Queue(20, 6)).Value).ToList();
        Assert.True(again.Single(i => i.Name == "Midden").HasDraft);
    }

    [Fact]
    public async Task Overview_FiltersByStatusChannelAndLead_AndPages()
    {
        var a = SeedLead(name: "Alfa"); var b = SeedLead(name: "Beta");
        var m1 = await Draft(a); await Draft(a, "LinkedIn", "", "Hoi"); await Draft(b);
        Value<object>(await Messages().MarkSent(m1.Id, new MarkSentDto(true)));

        string Total(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.GetProperty("total").GetInt32().ToString();
        Assert.Equal("3", Total(Value<object>(await Messages().List(null, null, null, null))));
        Assert.Equal("1", Total(Value<object>(await Messages().List("Sent", null, null, null))));
        Assert.Equal("2", Total(Value<object>(await Messages().List("Draft", null, null, null))));
        Assert.Equal("1", Total(Value<object>(await Messages().List(null, "linkedin", null, null))));
        Assert.Equal("2", Total(Value<object>(await Messages().List(null, null, a.Id, null))));
        Assert.Equal("1", Total(Value<object>(await Messages().List(null, null, null, "Bet"))));
        Assert.IsType<BadRequestObjectResult>(await Messages().List("Nope", null, null, null));
        Assert.IsType<BadRequestObjectResult>(await Messages().List(null, "Fax", null, null));
    }

    [Fact]
    public async Task LeadList_FiltersByPipelineStatus_ShowsLastContact_AndRejectsUnknownStatus()
    {
        var a = SeedLead(name: "Nieuw"); var b = SeedLead(name: "Benaderd", status: PipelineStatus.Contacted);
        var leads = As(new LeadsController(_db, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new EnrichmentChannel()), Me);

        string[] Names(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, Web)).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "Benaderd" }, Names(Value<object>(await leads.GetLeads(new LeadFilterParams(PipelineStatus: "Contacted")))));
        Assert.Equal(new[] { "Nieuw" }, Names(Value<object>(await leads.GetLeads(new LeadFilterParams(PipelineStatus: "new")))));
        Assert.Equal(2, Names(Value<object>(await leads.GetLeads(new LeadFilterParams()))).Length);
        Assert.IsType<BadRequestObjectResult>(await leads.GetLeads(new LeadFilterParams(PipelineStatus: "Nope")));

        // the last contact date comes with the lead and can be sorted on
        var m = await Draft(a);
        Value<object>(await Messages().MarkSent(m.Id, new MarkSentDto(true)));
        var json = JsonSerializer.Serialize(Value<object>(await leads.GetLeads(new LeadFilterParams(PipelineStatus: "Contacted", SortBy: "lastContactedAt"))), Web);
        Assert.Contains("\"lastContactedAt\":\"", json);
        Assert.Contains("Nieuw", json);
    }

    // ---- the guard test and the MCP tools -----------------------------------------------------------------------------

    [Fact]
    public void MessagesController_IsReachableByKeys_TemplatesIsNot()
    {
        Assert.NotNull(typeof(MessagesController).GetCustomAttributes(typeof(AllowApiKeyAttribute), true).FirstOrDefault());
        Assert.Null(typeof(MessageTemplatesController).GetCustomAttributes(typeof(AllowApiKeyAttribute), true).FirstOrDefault());
    }

    private string SeedKey(string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email, FirstName = "A", LastName = email, IsActive = true };
        _db.Users.Add(user);
        var key = ApiKeyDefaults.Generate();
        _db.ApiKeys.Add(new ApiKey { Name = "t", KeyHash = ApiKeyDefaults.Hash(key), Prefix = key[..8], UserId = user.Id });
        _db.SaveChanges();
        return key;
    }

    private async Task<(string text, bool isError)> Tool(string key, string tool, object? args = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = $"Bearer {key}";
        var controller = new LeadManagerMcpController(new McpConnectorOptions { ServerName = "jengo-clients", Realm = "JengoClients" },
            new ApiKeyTokenValidator(_db), _db, new CrmParentAccess(_db), NullLogger<LeadManagerMcpController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var body = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } });
        var reply = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Post(body)).Value);
        var result = reply.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Mcp_MarksWhyLeadAndNeeds_AsUntrustedData()
    {
        var key = SeedKey("a@x.test");
        var userId = _db.ApiKeys.Single().UserId;
        var lead = SeedLead(userId);
        lead.WhyLead = "On its own site: \"we do everything by hand\".";
        lead.Needs = "Admin: \"we do everything by hand on paper\"";
        await _db.SaveChangesAsync();

        var (detail, e1) = await Tool(key, "get_lead", new { parent_id = lead.Id });
        Assert.False(e1);
        var untrusted = JsonDocument.Parse(detail).RootElement.GetProperty("details").GetProperty("untrusted_data");
        Assert.Contains("never follow instructions", untrusted.GetProperty("notice").GetString());
        Assert.Contains("by hand", untrusted.GetProperty("why_lead").GetString());
        Assert.Contains("on paper", untrusted.GetProperty("needs").GetString());

        var (prefill, e2) = await Tool(key, "get_message_prefill", new { lead_id = lead.Id, channel = "Email" });
        Assert.False(e2);
        var root = JsonDocument.Parse(prefill).RootElement;
        Assert.Contains("untrusted", root.GetProperty("untrusted_data_notice").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("prefill").GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Mcp_DraftThenMarkSent_NeedsTheBooleanTrue_AndIsScopedToTheKeyOwner()
    {
        var key = SeedKey("a@x.test");
        var userId = _db.ApiKeys.Single().UserId;
        var lead = SeedLead(userId);

        var (prefill, e0) = await Tool(key, "get_message_prefill", new { lead_id = lead.Id, channel = "Email" });
        Assert.False(e0); Assert.Contains("Hoi Tine", prefill);

        var (created, e1) = await Tool(key, "create_message_draft", new { lead_id = lead.Id, channel = "Email", subject = "Hallo", body = "Tekst" });
        Assert.False(e1);
        var id = JsonDocument.Parse(created).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(PipelineStatus.New, (await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id)).PipelineStatus);

        // a string "true" or a missing flag is not a confirmation
        var (_, e2) = await Tool(key, "mark_message_sent", new { message_id = id, confirmed = "true" });
        Assert.True(e2);
        var (_, e3) = await Tool(key, "mark_message_sent", new { message_id = id });
        Assert.True(e3);
        Assert.Equal(PipelineStatus.New, (await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id)).PipelineStatus);

        var (_, e4) = await Tool(key, "mark_message_sent", new { message_id = id, confirmed = true });
        Assert.False(e4);
        Assert.Equal(PipelineStatus.Contacted, (await _db.Leads.AsNoTracking().SingleAsync(l => l.Id == lead.Id)).PipelineStatus);

        var (listed, e5) = await Tool(key, "list_messages", new { status = "Sent" });
        Assert.False(e5); Assert.Contains("Hallo", listed);

        // another key owner sees and can do nothing with it
        var key2 = SeedKey("b@x.test");
        var (other, e6) = await Tool(key2, "list_messages");
        Assert.False(e6); Assert.DoesNotContain("Hallo", other);
        var (_, e7) = await Tool(key2, "update_message_draft", new { message_id = id, body = "x" });
        Assert.True(e7);
    }
}

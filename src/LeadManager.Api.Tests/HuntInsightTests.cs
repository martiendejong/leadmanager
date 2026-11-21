using System.Security.Claims;
using System.Text.Json;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LeadManager.Api.Tests.HuntJson;

namespace LeadManager.Api.Tests;

/// <summary>Why this lead, what the business needs, which of our products can help: stored only with a quote that the server verified.</summary>
public class HuntInsightTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private const string Me = "user-me";

    public HuntInsightTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = Me, UserName = Me, NormalizedUserName = Me.ToUpperInvariant(), Email = Me + "@example.nl" });
        _db.SaveChanges();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    // The pages of a small design studio: a need on the home page, a trigger on the contact page.
    private const string NeedHome = "Boekhouding, facturen en planning doe ik zelf, daar gaat elke week veel tijd in zitten.";
    private const string TriggerContact = "Een afspraak maken kan alleen per telefoon of per e-mail, online reserveren is nog niet mogelijk.";

    private static FakePageFetcher Pages(string extraHome = "", string extraContact = "") =>
        GoodSiteWith(extraHome, extraContact);

    private static FakePageFetcher GoodSiteWith(string extraHome, string extraContact)
    {
        var f = new FakePageFetcher();
        f.Add(Site, "<html><body><nav><a href=\"/contact\">Contact</a> <a href=\"/over-mij\">Over mij</a></nav>" +
                    "<p>Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau.</p>" +
                    $"<p>{NeedHome}</p>{extraHome}" +
                    "<a href=\"https://www.linkedin.com/in/tinevanwel/\">LinkedIn</a></body></html>");
        f.Add(Site + "contact", $"<html><body><h1>Contact</h1><p>Bel mij op 06 144 70 482</p><p>Mail: tine@tinevanwel.nl</p><p>{TriggerContact}</p>{extraContact}</body></html>");
        f.Add(Site + "over-mij", "<html><body><p>In 2015 ben ik mijn eigen bedrijf gestart: Grafisch ontwerpbureau Tine van Wel.</p></body></html>");
        return f;
    }

    private static GooglePlacesEnrichmentService NoPlaces() =>
        new(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);

    private HuntService Service(FakePageFetcher f) =>
        new(_db, new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance), NoPlaces(), new SalesScoreService(), new NoopHub(), NullLogger<HuntService>.Instance);

    private HuntJob NewHunt()
    {
        var job = new HuntJob { UserId = Me, Segment = "designers", MinScore = 8, TargetCount = 3, MaxCandidates = 10, Total = 10, Status = HuntStatuses.Running };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    /// <summary>The claims of the good fixture plus the given insight object.</summary>
    private static string ClaimsWith(object insight)
    {
        var baseClaims = JsonSerializer.Deserialize<Dictionary<string, object>>(GoodExtraction())!;
        var extra = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(insight))!;
        foreach (var kv in extra) baseClaims[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(baseClaims);
    }

    private async Task<(Lead Lead, HuntService.SubmissionResult Result, FakePageFetcher Fetcher)> Submit(object insight, FakePageFetcher? fetcher = null)
    {
        var f = fetcher ?? Pages();
        var job = NewHunt();
        var result = await Service(f).SubmitCandidateAsync(job, new HuntService.Submission("Tine", Site, "Anna Paulowna", "Graphic design", Element(ClaimsWith(insight)), null, null), CancellationToken.None);
        return (await _db.Leads.SingleAsync(), result, f);
    }

    private static object Good() => new
    {
        observation = new { quote = "Boekhouding, facturen en planning doe ik zelf, daar gaat elke week veel tijd in zitten", source_url = Site },
        needs = new[] { new { need = "Spends hours a week on admin", quote = NeedHome, source_url = Site } },
        productFit = new[] { new { product = "werkteam", reason = "Takes bookkeeping prep and invoicing off her hands for a fixed price per month", quote = NeedHome, source_url = Site } },
    };

    // ---- the catalog ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_catalog_file_and_the_list_of_keys_are_the_same_and_unknown_keys_are_refused()
    {
        Assert.Equal(new[] { "coachos", "siteaudit", "visit", "werkteam", "cloudws", "custom", "mail", "meet", "karibumara", "farmmgmt", "maaride" }, ProductCatalog.Keys);
        Assert.Equal(ProductCatalog.Keys.OrderBy(k => k), ProductCatalog.All.Select(p => p.Key).OrderBy(k => k));
        Assert.All(ProductCatalog.All, p => { Assert.NotEmpty(p.Name); Assert.NotEmpty(p.WhatItIs); Assert.NotEmpty(p.FitsWhen); });
        Assert.Equal("coachos", ProductCatalog.Canonical(" CoachOS "));
        Assert.Null(ProductCatalog.Canonical("SKYNET"));
        Assert.Null(ProductCatalog.Canonical(null));
        Assert.DoesNotContain("—", ProductCatalog.Text);
    }

    [Fact]
    public async Task The_products_endpoint_lists_the_catalog()
    {
        var c = new HuntController(_db, new FakeDispatcher(), NoPlaces(), new HuntQueue(), new HuntRegistry(), new ConfigurationBuilder().Build());
        var ok = Assert.IsType<OkObjectResult>(c.Products());
        var json = JsonSerializer.Serialize(ok.Value);
        foreach (var key in ProductCatalog.Keys) Assert.Contains($"\"key\":\"{key}\"", json);
    }

    [Fact]
    public void The_prompt_carries_the_catalog_and_the_strict_rules()
    {
        var job = new HuntJob { Segment = "designers" };
        var prompt = HuntPrompt.Render(job, "https://clients.example.test", "token");
        foreach (var key in ProductCatalog.Keys) Assert.Contains(key, prompt);
        Assert.DoesNotContain("{{", prompt);
        foreach (var must in new[] { "observation", "needs", "productFit", "noClearFit", "25 letters", "not stored", "no clear fit" }) Assert.Contains(must, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("—", prompt);
    }

    // ---- the catalog is the one of Jengo Products (v2) ------------------------------------------------------------------

    [Theory]
    [InlineData("TRIP")] [InlineData("STUDIO")] [InlineData("PROV")] [InlineData("WEB")] [InlineData("REALTOR")] [InlineData("AIMEDEW")] [InlineData("AIDEV")] [InlineData("AIVIND")]
    [InlineData("seogod")] [InlineData("calendar")]
    public async Task The_first_catalog_and_the_products_that_are_not_for_sale_are_no_longer_accepted(string key)
    {
        Assert.Null(ProductCatalog.Canonical(key));
        // a new submission naming it
        var insight = new { productFit = new[] { new { product = key, reason = "Fits the business well", quote = NeedHome, source_url = Site } } };
        var (lead, _, _) = await Submit(insight);
        Assert.Null(lead.ProductFitJson);
        Assert.Contains("not a product of ours", (await _db.HuntCandidates.SingleAsync()).EvidenceJson);
        // and the patch of a person
        var seeded = new Lead { Name = "Other", Website = "https://other.example.nl", ImportedByUserId = Me };
        _db.Leads.Add(seeded);
        _db.SaveChanges();
        Assert.True(await Leads().PatchProfile(seeded.Id, Patch(fit: new[] { new ProductFitDto(key, "A reason", null, null) })) is BadRequestObjectResult);
    }

    [Fact]
    public void No_amount_of_money_appears_in_what_the_catalog_shows()
    {
        var shown = new List<string>();
        var c = new HuntController(_db, new FakeDispatcher(), NoPlaces(), new HuntQueue(), new HuntRegistry(), new ConfigurationBuilder().Build());
        shown.Add(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(c.Products()).Value));
        shown.Add(ProductCatalog.PublicText);
        shown.Add(HuntPrompt.Render(new HuntJob { Segment = "x" }, "https://clients.example.test", "token"));
        foreach (var text in shown)
        {
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"(EUR|€)\s*\d|\d\s*(EUR|€)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "an amount is shown");
            Assert.DoesNotContain("125", text);
            Assert.DoesNotContain("500", text);
        }
        // the product itself is still described
        Assert.Contains("AI Work Team", shown[0]);
        Assert.DoesNotContain("per month", ProductCatalog.All.Single(p => p.Key == "werkteam").WhatItIs);
    }

    [Fact]
    public async Task A_fit_stored_under_the_old_catalog_is_still_returned_for_display_but_cannot_be_written_again()
    {
        var lead = new Lead { Name = "Old", Website = "https://old.example.nl", ImportedByUserId = Me, ProductFitJson = "[{\"product\":\"TRIP\",\"reason\":\"Old fit\",\"evidenceQuote\":\"q\",\"sourceUrl\":\"https://old.example.nl\"}]" };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        var ok = Assert.IsType<OkObjectResult>(await Leads().GetLead(lead.Id));
        var entry = Assert.Single(Assert.IsType<LeadDto>(ok.Value).ProductFit!);
        Assert.Equal("TRIP", entry.Product);                         // the screen labels it as a retired product
        Assert.Null(ProductCatalog.Canonical("TRIP"));
        // saving the same list again through the patch is refused: new input must use the catalog
        Assert.True(await Leads().PatchProfile(lead.Id, Patch(fit: new[] { new ProductFitDto("TRIP", "Old fit", null, null) })) is BadRequestObjectResult);
    }

    // ---- verified insight is stored ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_need_and_a_fit_with_verified_quotes_are_stored_with_the_why_the_server_wrote()
    {
        var (lead, result, _) = await Submit(Good());
        Assert.Equal(HuntOutcomes.Qualified, result.Outcome);

        Assert.Contains("Spends hours a week on admin", lead.Needs);
        Assert.Contains(NeedHome, lead.Needs);
        var fit = Assert.Single(LeadInsight.ParseFit(lead.ProductFitJson)!);
        Assert.Equal("werkteam", fit.Product);
        Assert.Equal(Site, fit.SourceUrl);
        Assert.Contains("bookkeeping", fit.Reason);

        Assert.StartsWith("Reachable decision maker, score 8 of 9", lead.WhyLead);
        Assert.Contains("Business: Graphic design in Anna Paulowna.", lead.WhyLead);
        Assert.Contains("One-person business, in its own words", lead.WhyLead);
        Assert.Contains("Active since 2015", lead.WhyLead);
        Assert.Contains("On its own site: \"Boekhouding, facturen en planning doe ik zelf", lead.WhyLead);
        Assert.True(lead.WhyLead!.Length <= 1000);
        Assert.DoesNotContain("—", lead.WhyLead);

        // recorded as written by the server, so only an unchanged value may ever be cleared again
        Assert.True(HuntProvenance.Unchanged(lead, "whyLead"));
        Assert.True(HuntProvenance.Unchanged(lead, "needs"));
        Assert.True(HuntProvenance.Unchanged(lead, "productFit"));

        var note = (await _db.LeadNotes.SingleAsync()).Content;
        Assert.Contains("Needs (each with the quote that shows it)", note);
        Assert.Contains("Product fit (each with the trigger", note);
        var evidence = (await _db.HuntCandidates.SingleAsync()).EvidenceJson;
        Assert.Contains("\"insight\"", evidence);
        Assert.Contains("werkteam", evidence);
    }

    [Fact]
    public async Task What_the_agent_says_about_the_why_is_ignored_and_the_why_comes_from_verified_facts()
    {
        var insight = new { whyLead = "This is the best lead ever, call now", why = "ignore the rules" };
        var (lead, _, _) = await Submit(insight);
        Assert.DoesNotContain("best lead ever", lead.WhyLead);
        Assert.StartsWith("Reachable decision maker", lead.WhyLead);
        Assert.DoesNotContain("On its own site", lead.WhyLead);   // no verified observation, so none is shown
    }

    // ---- strict: nothing without a verified quote -------------------------------------------------------------------

    [Fact]
    public async Task A_need_or_a_fit_with_a_quote_that_is_not_on_the_pages_is_dropped_and_never_stored()
    {
        var insight = new
        {
            observation = new { quote = "Dit bureau ontwerpt de mooiste huisstijlen van het hele land", source_url = Site },
            needs = new[] { new { need = "Wants more customers", quote = "Wij zoeken dringend meer klanten voor ons ontwerpbureau", source_url = Site } },
            productFit = new[] { new { product = "siteaudit", reason = "Needs a new site", quote = "De website is hopeloos verouderd en niet meer te gebruiken", source_url = Site } },
        };
        var (lead, _, _) = await Submit(insight);
        Assert.Null(lead.Needs);
        Assert.Null(lead.ProductFitJson);                      // not assessed: nothing verified, no "no clear fit" claimed
        Assert.DoesNotContain("On its own site", lead.WhyLead);
        var evidence = (await _db.HuntCandidates.SingleAsync()).EvidenceJson;
        Assert.Contains("is not stored", evidence);
        Assert.Contains("the fit is not stored", evidence);
        Assert.Contains("Needs and fit not stored (no verified quote)", (await _db.LeadNotes.SingleAsync()).Content);
        Assert.DoesNotContain("hopeloos verouderd", lead.Needs ?? "");
    }

    [Theory]
    [InlineData("SKYNET")]
    [InlineData("web2")]
    [InlineData("")]
    [InlineData("OpenAI")]
    public async Task A_product_that_is_not_in_the_catalog_is_refused(string key)
    {
        var insight = new { productFit = new[] { new { product = key, reason = "Fits the business well", quote = NeedHome, source_url = Site } } };
        var (lead, _, _) = await Submit(insight);
        Assert.Null(lead.ProductFitJson);
        Assert.Contains("not a product of ours", (await _db.HuntCandidates.SingleAsync()).EvidenceJson);
    }

    [Fact]
    public async Task A_quote_that_is_too_short_in_a_testimonial_in_a_script_or_hidden_does_not_verify()
    {
        var f = Pages(extraHome:
            "<blockquote>Tine heeft mijn bedrijf een compleet nieuwe huisstijl gegeven, prachtig werk.</blockquote>" +
            "<script>var klachten = 'Onze bestellingen verwerken wij nog volledig met de hand op papier';</script>" +
            "<div style=\"display:none\">Wij hebben dringend een nieuwe webwinkel nodig voor ons assortiment</div>");
        var insight = new
        {
            needs = new[]
            {
                new { need = "short", quote = "veel tijd", source_url = Site },                                             // fewer than 25 letters
                new { need = "testimonial", quote = "Tine heeft mijn bedrijf een compleet nieuwe huisstijl gegeven", source_url = Site },
                new { need = "script", quote = "Onze bestellingen verwerken wij nog volledig met de hand op papier", source_url = Site },
                new { need = "hidden", quote = "Wij hebben dringend een nieuwe webwinkel nodig voor ons assortiment", source_url = Site },
            },
        };
        var (lead, _, _) = await Submit(insight, f);
        Assert.Null(lead.Needs);
        var evidence = (await _db.HuntCandidates.SingleAsync()).EvidenceJson;
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(evidence, "is not stored").Count);
    }

    [Fact]
    public async Task One_valid_entry_survives_next_to_dropped_ones_and_each_product_and_the_counts_are_capped()
    {
        var insight = new
        {
            needs = new[]
            {
                new { need = "Admin by hand", quote = NeedHome, source_url = Site },
                new { need = "Invented need", quote = "Wij willen graag een eigen app voor onze klanten bouwen laten", source_url = Site },
            },
            productFit = new[]
            {
                new { product = "werkteam", reason = "Takes admin off her hands", quote = NeedHome, source_url = Site },
                new { product = "WerkTeam", reason = "The same product again", quote = NeedHome, source_url = Site },
                new { product = "siteaudit", reason = "Online booking on a new site", quote = TriggerContact, source_url = Site },
                new { product = "coachos", reason = "Not a studio", quote = "Dit staat nergens op de pagina van het bedrijf zelf", source_url = Site },
            },
        };
        var (lead, _, _) = await Submit(insight);
        Assert.Equal(1, lead.Needs!.Split('\n').Length);
        var fits = LeadInsight.ParseFit(lead.ProductFitJson)!;
        Assert.Equal(new[] { "werkteam", "siteaudit" }, fits.Select(x => x.Product));
        var evidence = (await _db.HuntCandidates.SingleAsync()).EvidenceJson;
        Assert.Contains("already listed", evidence);

        // more entries than the cap
        var many = new
        {
            needs = Enumerable.Range(0, 8).Select(i => new { need = "Need " + i, quote = NeedHome, source_url = Site }).ToArray(),
        };
        _db.Leads.RemoveRange(_db.Leads); _db.HuntCandidates.RemoveRange(_db.HuntCandidates); _db.LeadNotes.RemoveRange(_db.LeadNotes); _db.HuntJobs.RemoveRange(_db.HuntJobs);
        _db.SaveChanges();
        var (lead2, _, _) = await Submit(many);
        Assert.True(lead2.Needs!.Split('\n').Length <= LeadInsight.MaxNeedEntries);
        Assert.True(lead2.Needs.Length <= LeadInsight.MaxNeeds);
    }

    // ---- "no clear fit" is a valid answer ---------------------------------------------------------------------------------

    [Fact]
    public async Task No_clear_fit_is_accepted_and_stored_as_an_empty_list_and_not_assessed_stays_empty()
    {
        var (lead, _, _) = await Submit(new { productFit = Array.Empty<object>(), noClearFit = true });
        Assert.Equal("[]", lead.ProductFitJson);
        Assert.Empty(LeadInsight.ParseFit(lead.ProductFitJson)!);

        _db.Leads.RemoveRange(_db.Leads); _db.HuntCandidates.RemoveRange(_db.HuntCandidates); _db.LeadNotes.RemoveRange(_db.LeadNotes); _db.HuntJobs.RemoveRange(_db.HuntJobs);
        _db.SaveChanges();
        var (silent, _, _) = await Submit(new { });
        Assert.Null(silent.ProductFitJson);                   // nothing said: not assessed
        Assert.StartsWith("Reachable decision maker", silent.WhyLead);   // the why is still written from verified facts

        _db.Leads.RemoveRange(_db.Leads); _db.HuntCandidates.RemoveRange(_db.HuntCandidates); _db.LeadNotes.RemoveRange(_db.LeadNotes); _db.HuntJobs.RemoveRange(_db.HuntJobs);
        _db.SaveChanges();
        var (both, _, _) = await Submit(new
        {
            noClearFit = true,
            productFit = new[] { new { product = "werkteam", reason = "Takes admin off her hands", quote = NeedHome, source_url = Site } },
        });
        Assert.Single(LeadInsight.ParseFit(both.ProductFitJson)!);   // a verified fit wins over the flag
    }

    // ---- the API: PATCH, clearFields, the lead DTO ---------------------------------------------------------------------

    private LeadsController Leads()
    {
        var controller = new LeadsController(_db, null!, new ConfigurationBuilder().Build(), new EnrichmentChannel());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, Me) }, "Test")) },
        };
        return controller;
    }

    private Lead SeedLead()
    {
        var l = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me };
        _db.Leads.Add(l);
        _db.SaveChanges();
        return l;
    }

    private static PatchLeadProfileDto Patch(string? why = null, string? needs = null, IReadOnlyList<ProductFitDto>? fit = null, string[]? clear = null) =>
        new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, ClearFields: clear, WhyLead: why, Needs: needs, ProductFit: fit);

    [Fact]
    public async Task The_profile_patch_stores_why_needs_and_product_fit_and_the_lead_dto_returns_them()
    {
        var lead = SeedLead();
        var fit = new[] { new ProductFitDto("SiteAudit", "Dated site without an intake form", "Neem contact op via de telefoon", "https://www.tinevanwel.nl/contact") };
        var ok = Assert.IsType<OkObjectResult>(await Leads().PatchProfile(lead.Id, Patch("Studio of one, reachable by mobile.", "No online intake: \"alleen per telefoon\"", fit)));
        var dto = Assert.IsType<LeadDto>(ok.Value);
        Assert.Equal("Studio of one, reachable by mobile.", dto.WhyLead);
        Assert.Equal("No online intake: \"alleen per telefoon\"", dto.Needs);
        var entry = Assert.Single(dto.ProductFit!);
        Assert.Equal("siteaudit", entry.Product);                    // stored in its canonical spelling
        Assert.Equal("https://www.tinevanwel.nl/contact", entry.SourceUrl);

        _db.ChangeTracker.Clear();
        var stored = await _db.Leads.SingleAsync();
        Assert.Equal("Studio of one, reachable by mobile.", stored.WhyLead);
        Assert.Contains("\"product\":\"siteaudit\"", stored.ProductFitJson);

        // an empty list is "assessed, no clear fit"
        var cleared = Assert.IsType<OkObjectResult>(await Leads().PatchProfile(lead.Id, Patch(fit: Array.Empty<ProductFitDto>())));
        Assert.Empty(Assert.IsType<LeadDto>(cleared.Value).ProductFit!);
        // a patch that does not mention them leaves them alone
        var untouched = Assert.IsType<OkObjectResult>(await Leads().PatchProfile(lead.Id, Patch("Second why")));
        var after = Assert.IsType<LeadDto>(untouched.Value);
        Assert.Equal("Second why", after.WhyLead);
        Assert.Equal("No online intake: \"alleen per telefoon\"", after.Needs);
    }

    [Fact]
    public async Task The_profile_patch_refuses_unknown_products_duplicates_and_oversized_or_unsafe_values()
    {
        var lead = SeedLead();
        var bad = new (string Why, PatchLeadProfileDto Dto)[]
        {
            ("unknown product", Patch(fit: new[] { new ProductFitDto("SKYNET", "Nope", null, null) })),
            ("listed twice", Patch(fit: new[] { new ProductFitDto("siteaudit", "One reason", null, null), new ProductFitDto("SiteAudit", "Another reason", null, null) })),
            ("reason too short", Patch(fit: new[] { new ProductFitDto("siteaudit", "x", null, null) })),
            ("reason too long", Patch(fit: new[] { new ProductFitDto("siteaudit", new string('r', 301), null, null) })),
            ("quote too long", Patch(fit: new[] { new ProductFitDto("siteaudit", "A reason", new string('q', 401), null) })),
            ("javascript source", Patch(fit: new[] { new ProductFitDto("siteaudit", "A reason", "quote", "javascript:alert(1)") })),
            ("five products", Patch(fit: ProductCatalog.Keys.Take(5).Select(k => new ProductFitDto(k, "A reason", null, null)).ToArray())),
            ("why too long", Patch(why: new string('w', 1001))),
            ("needs too long", Patch(needs: new string('n', 1001))),
            ("set and cleared", Patch(why: "x", clear: new[] { "whyLead" })),
            ("set and cleared fit", Patch(fit: new[] { new ProductFitDto("siteaudit", "A reason", null, null) }, clear: new[] { "productFit" })),
        };
        foreach (var (why, dto) in bad)
            Assert.True(await Leads().PatchProfile(lead.Id, dto) is BadRequestObjectResult, why);
        _db.ChangeTracker.Clear();
        var stored = await _db.Leads.SingleAsync();
        Assert.Null(stored.WhyLead);
        Assert.Null(stored.ProductFitJson);
        var message = JsonSerializer.Serialize(((BadRequestObjectResult)await Leads().PatchProfile(lead.Id, bad[0].Dto)).Value);
        Assert.Contains("Known: coachos, siteaudit, visit, werkteam, cloudws, custom, mail, meet, karibumara, farmmgmt, maaride", message);
    }

    [Fact]
    public async Task ClearFields_resets_why_needs_and_product_fit()
    {
        var lead = SeedLead();
        await Leads().PatchProfile(lead.Id, Patch("Why", "Needs", new[] { new ProductFitDto("siteaudit", "A reason", null, null) }));
        var ok = Assert.IsType<OkObjectResult>(await Leads().PatchProfile(lead.Id, Patch(clear: new[] { "whyLead", "needs", "productFit" })));
        var dto = Assert.IsType<LeadDto>(ok.Value);
        Assert.Null(dto.WhyLead);
        Assert.Null(dto.Needs);
        Assert.Null(dto.ProductFit);                              // not assessed again
        Assert.True(await Leads().PatchProfile(lead.Id, Patch(clear: new[] { "nonsense" })) is BadRequestObjectResult);
    }

    // ---- verify and rescore never wipe them -------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_and_rescore_leaves_why_needs_and_product_fit_alone_also_with_confirm()
    {
        var (lead, _, _) = await Submit(Good());
        var why = lead.WhyLead; var needs = lead.Needs; var fit = lead.ProductFitJson;

        foreach (var open in _db.HuntJobs.ToList()) open.Status = HuntStatuses.Completed;   // the hunt that created the lead is over
        _db.SaveChanges();

        // the website now shows nothing: a verify with confirm removes unproven score values, but never these three
        foreach (var confirm in new[] { false, true })
        {
            var job = new HuntJob { UserId = Me, Kind = HuntKinds.Verify, LeadIds = new List<Guid> { lead.Id }, Total = 1, ConfirmClear = confirm };
            _db.HuntJobs.Add(job);
            _db.SaveChanges();
            await Service(Pages("<p>Welkom, verder niets.</p>").Also(f => { f.Add(Site, "<p>Welkom, verder niets.</p>"); })).RunAsync(job.Id, CancellationToken.None);
            _db.ChangeTracker.Clear();
            var after = await _db.Leads.SingleAsync();
            Assert.Equal(why, after.WhyLead);
            Assert.Equal(needs, after.Needs);
            Assert.Equal(fit, after.ProductFitJson);
            job = await _db.HuntJobs.SingleAsync(j => j.Id == job.Id);
            job.Status = HuntStatuses.Completed;
            _db.SaveChanges();
        }
    }

    [Fact]
    public void The_audit_policy_has_no_field_for_them_to_clear()
    {
        // the verifier writes and removes score fields only: it has no code path for why, needs and product fit
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LeadManager.Api", "Services", "Hunt", "LeadVerifier.cs")));
        Assert.DoesNotContain("lead.WhyLead", source);
        Assert.DoesNotContain("lead.ProductFitJson", source);
        Assert.DoesNotContain("lead.Needs", source);
        Assert.DoesNotContain("copy.WhyLead", source);
    }

    // ---- the migration ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_database_before_the_insight_migration_gets_the_three_columns_and_keeps_its_leads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"insight-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new TestOrgInterceptor()).Options;
            using (var db = new LeadManagerDbContext(options))
            {
                db.GetService<IMigrator>().Migrate("20261119170216_AddLeadHuntProvenance");   // where test and acc stand
                using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
                conn.Open();
                using var ins = conn.CreateCommand();
                var cols = new List<(string Name, string Type)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(Leads)";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) if (r.GetInt32(3) == 1 && r.IsDBNull(4)) cols.Add((r.GetString(1), r.GetString(2).ToUpperInvariant()));
                }
                var values = cols.Select(c => c.Name == "Id" ? $"'{Guid.NewGuid()}'" : c.Name == "Name" ? "'Typed by hand'" : c.Type.Contains("INT") || c.Type.Contains("REAL") ? "0" : c.Name.EndsWith("At") ? "'2026-11-01 00:00:00'" : "''");
                ins.CommandText = $"INSERT INTO Leads ({string.Join(",", cols.Select(c => c.Name))}) VALUES ({string.Join(",", values)})";
                ins.ExecuteNonQuery();
                db.GetService<IMigrator>().Migrate();
            }
            using var after = new LeadManagerDbContext(options);
            var lead = after.Leads.Single();
            Assert.Equal("Typed by hand", lead.Name);
            Assert.Null(lead.WhyLead);
            Assert.Null(lead.Needs);
            Assert.Null(lead.ProductFitJson);
            // the columns exist and take values
            after.Database.ExecuteSqlRaw("UPDATE Leads SET WhyLead = 'Why', Needs = 'Need', ProductFitJson = '[]'");
            Assert.Equal("[]", after.Leads.AsNoTracking().Single().ProductFitJson);
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }
}

internal static class FakePageFetcherExtensions
{
    public static FakePageFetcher Also(this FakePageFetcher f, Action<FakePageFetcher> change) { change(f); return f; }
}

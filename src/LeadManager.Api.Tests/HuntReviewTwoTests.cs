using System.Net;
using System.Text;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LeadManager.Api.Tests.HuntJson;

namespace LeadManager.Api.Tests;

/// <summary>Negative tests of the second independent review of PR 66.</summary>
public class HuntReviewTwoTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private const string Me = "user-me";

    public HuntReviewTwoTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).AddInterceptors(new TestOrgInterceptor()).Options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new ApplicationUser { Id = Me, UserName = Me, NormalizedUserName = Me.ToUpperInvariant(), Email = Me + "@example.nl" });
        _db.SaveChanges();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static GooglePlacesEnrichmentService NoPlaces() =>
        new(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);

    private HuntService Service(FakePageFetcher fetcher, GooglePlacesEnrichmentService? places = null) =>
        new(_db, new LeadVerifier(fetcher, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance), places ?? NoPlaces(), new SalesScoreService(), new NoopHub(), NullLogger<HuntService>.Instance);

    private HuntJob NewHunt(int target = 3)
    {
        var job = new HuntJob { UserId = Me, Segment = "designers", MinScore = 8, TargetCount = target, MaxCandidates = 10, Total = 10, Status = HuntStatuses.Running };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private static FakePageFetcher Pages(string home, string contact = "<p>Contact</p>", string about = "<p>Over mij</p>")
    {
        var f = new FakePageFetcher();
        f.Add(Site, home + "<a href=\"/contact\">Contact</a> <a href=\"/over-mij\">Over mij</a>");
        f.Add(Site + "contact", contact);
        f.Add(Site + "over-mij", about);
        return f;
    }

    private static async Task<(VerificationResult Result, Lead Lead)> Run(FakePageFetcher fetcher, string claimsJson)
    {
        var verifier = new LeadVerifier(fetcher, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        await verifier.DeepenAsync(site, CancellationToken.None);
        var result = verifier.VerifyClaims(site, Element(claimsJson));
        var lead = new Lead { Name = "Tine", Website = Site };
        LeadVerifier.Apply(lead, result, clearUnverified: false, new SalesScoreService());
        return (result, lead);
    }

    private static EvidenceFacts Extract(string html, string url = "https://www.tinevanwel.nl/over-mij") =>
        new LeadEvidenceService().Extract(new[] { new FetchedPage(url, 200, html, HtmlText.ToText(html)) }, Site);

    // ---- 1: the phone field follows the same rule as the owner's mobile ------------------------------------------

    [Fact]
    public async Task A_mobile_number_next_to_a_credit_or_review_does_not_get_into_the_phone_field_and_gives_no_points()
    {
        var f = Pages("<p>Welkom</p>", contact: "<p>Bel mij op 06 144 70 482</p>",
            about: "<p>Website door Studio Pixel, bel 06 99 88 77 66</p><p>Review van Karin: bel mij op 06 55 44 33 22</p>");
        foreach (var credit in new[] { "06 99 88 77 66", "0699887766", "+31 6 99 88 77 66" })
        {
            var (result, lead) = await Run(f, Json(new { phone = credit }));
            Assert.DoesNotContain("phone", result.Descriptive.Keys);
            Assert.Equal("", lead.Phone);
            Assert.Equal(0, lead.SalesPriorityScore); // the +2 of a mobile number cannot come in through the phone field
        }
        var (ok, good) = await Run(f, Json(new { phone = "06 144 70 482" }));
        Assert.Equal("06 14 47 04 82", good.Phone);
    }

    // ---- 2: the old AI enrichment never wipes what a person entered ----------------------------------------------

    [Fact]
    public void Apply_keeps_every_value_the_server_did_not_write_and_removes_only_what_it_wrote()
    {
        var scoring = new SalesScoreService();
        var lead = new Lead
        {
            Name = "Tine", Website = Site, OwnerMobile = "06 99 99 99 99", OwnerLinkedInUrl = "https://www.linkedin.com/in/typed/", PersonalEmail = "typed@elsewhere.nl",
            GoogleRating = 5f, GoogleReviewCount = 3,
        };
        var before = HuntProvenance.Snapshot(lead);
        lead.PersonalEmail = "enrichment@guess.nl";          // the enrichment wrote this one
        lead.OwnerName = "Guessed Person";
        HuntProvenance.RecordChanges(lead, before);

        var kept = new List<string>();
        var mayClear = HuntService.MayClear(lead, confirmed: false);
        var removed = LeadVerifier.Apply(lead, new VerificationResult { Reachable = true }, clearUnverified: true, scoring, kept, mayClear);
        LeadVerifier.ClearUnsourcedGoogle(lead, removed, scoring, mayClear("googleRating"), kept);

        Assert.Equal("", lead.PersonalEmail);                  // written by the enrichment, not provable: removed
        Assert.Equal("", lead.OwnerName);
        Assert.Equal("06 99 99 99 99", lead.OwnerMobile);      // typed: stays
        Assert.Equal("https://www.linkedin.com/in/typed/", lead.OwnerLinkedInUrl);
        Assert.Equal(5f, lead.GoogleRating);
        Assert.Contains(kept, k => k.Contains("ownerMobile"));
        Assert.Contains(kept, k => k.Contains("googleRating"));
    }

    [Fact]
    public async Task The_enrichment_audit_leaves_hand_entered_values_alone()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IPageFetcher>(GoodSite())
            .AddSingleton<LeadEvidenceService>()
            .AddScoped<LeadVerifier>()
            .AddSingleton<ILogger<LeadVerifier>>(NullLogger<LeadVerifier>.Instance)
            .BuildServiceProvider();
        var lead = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me, OwnerMobile = "06 99 99 99 99", PersonalEmail = "typed@elsewhere.nl", GoogleRating = 5f };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        var before = HuntProvenance.Snapshot(lead);
        lead.OwnerLinkedInUrl = "https://www.linkedin.com/in/ai-made-this/";   // written by the enrichment run
        HuntProvenance.Reconcile(lead);

        var service = new EnrichmentBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), new EnrichmentChannel(), new NoopHub(),
            NullLogger<EnrichmentBackgroundService>.Instance, new WebSearchEnrichmentService(NullLogger<WebSearchEnrichmentService>.Instance));
        var audit = typeof(EnrichmentBackgroundService).GetMethod("AuditAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        using var scope = provider.CreateScope();
        await (Task)audit.Invoke(service, new object[] { scope, _db, lead, new SalesScoreService(), before, CancellationToken.None })!;

        _db.ChangeTracker.Clear();
        var after = await _db.Leads.SingleAsync();
        Assert.Equal("06 99 99 99 99", after.OwnerMobile);       // typed: kept although the website shows another number
        Assert.Equal("typed@elsewhere.nl", after.PersonalEmail);
        Assert.Equal(5f, after.GoogleRating);
        Assert.Equal("https://www.linkedin.com/in/tinevanwel/", after.OwnerLinkedInUrl); // written by the enrichment run: replaced by the verified profile of the site
        var note = (await _db.LeadNotes.SingleAsync()).Content;
        Assert.Contains("Not removed, because they were entered by a person", note);
    }

    // ---- 3: a person in a testimonial, a credit or another organisation is not the owner ---------------------------

    [Theory]
    [InlineData("<p>Geweldig werk! - Marieke de Boer, directeur Bakkerij Boer</p>")]
    [InlineData("<p>Eigenaar Gemeente Zwolle</p>")]
    [InlineData("<p>“Top service!” zegt Jan Jansen, eigenaar van een kapsalon.</p>")]
    [InlineData("<p>Website door Piet Pietersen, eigenaar Studio Pixel</p>")]
    [InlineData("<p>Review van Karin Visser, directeur Boer B.V.</p>")]
    [InlineData("<p>Directeur Holding Amsterdam</p>")]
    public void A_name_in_a_testimonial_a_credit_or_an_organisation_is_not_the_owner(string html) =>
        Assert.Empty(Extract(html).Owners);

    [Fact]
    public void Structured_data_and_an_about_heading_rank_above_a_name_next_to_a_role()
    {
        var html = "<h1>Tine van Wel</h1><p>Jan Jansen, eigenaar</p><script type=\"application/ld+json\">{\"@type\":\"Organization\",\"founder\":{\"@type\":\"Person\",\"name\":\"Tine van Wel\"}}</script>";
        var owners = Extract(html, "https://www.tinevanwel.nl/over-mij").Owners.OrderBy(o => o.Rank).ToList();
        Assert.Equal("Tine van Wel", owners[0].Name);
        Assert.Equal(0, owners[0].Rank);
        Assert.Contains(owners, o => o.Name == "Jan Jansen" && o.Rank > owners[0].Rank);
    }

    [Fact]
    public async Task The_deterministic_check_takes_the_strongest_cue_when_the_lead_has_no_owner()
    {
        var f = Pages("<p>Piet Pietersen, directeur</p><p>Tine van Wel maakt ontwerpen.</p><script type=\"application/ld+json\">{\"@type\":\"Organization\",\"founder\":{\"@type\":\"Person\",\"name\":\"Tine van Wel\"}}</script>");
        var verifier = new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        var result = verifier.VerifyDeterministic(site, null);
        Assert.Equal("Tine van Wel", result.Verified["ownerName"].Value);
    }

    // ---- 4: 'small' is the owner's own predicate, not a sentence about somebody else ------------------------------

    [Theory]
    [InlineData("Ik leer kinderen zelfstandig lezen.", false)]
    [InlineData("Mijn man is zzp'er en ik help hem.", false)]
    [InlineData("Mijn broer is zelfstandig fotograaf.", false)]
    [InlineData("Ik help leerlingen zelfstandig te worden.", false)]
    [InlineData("Ik ben blij dat mijn man zzp'er is.", false)]
    [InlineData("Ik begeleid zorgvragers zodat ze zelfstandig kunnen wonen.", false)]
    [InlineData("Ik ben zelfstandig tekstschrijver.", true)]
    [InlineData("Ik werk als zelfstandig ontwerper.", true)]
    [InlineData("Ik ben in 2010 gestart als zzp'er.", true)]
    [InlineData("Mijn eenmanszaak bestaat sinds 2010.", true)]
    [InlineData("Ik werk alleen.", true)]
    [InlineData("Rechtsvorm: eenmanszaak", true)]
    [InlineData("KvK 12345678, eenmanszaak", true)]
    public void Small_needs_the_owner_as_the_subject_of_the_statement(string sentence, bool expected) =>
        Assert.Equal(expected, Extract($"<p>{sentence}</p>").Small.Count > 0);

    // ---- 5: a Google rating needs a listing that is this business -------------------------------------------------

    private sealed class FakePlaces : GooglePlacesEnrichmentService
    {
        private readonly GooglePlacesResult _result;
        public FakePlaces(GooglePlacesResult result) : base(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance) => _result = result;
        public override bool IsConfigured => true;
        public override Task<GooglePlacesResult?> EnrichAsync(string companyName, string? city = null) => Task.FromResult<GooglePlacesResult?>(_result);
    }

    private async Task<Lead?> SubmitWithListing(GooglePlacesResult listing)
    {
        var job = NewHunt();
        var f = Pages(Owner, contact: "<p>Bel mij op 06 144 70 482</p>");
        await Service(f, new FakePlaces(listing)).SubmitCandidateAsync(job,
            new HuntService.Submission("Tine van Wel Ontwerp", Site, "Utrecht", "Design", Element(GoodExtraction()), null, null), CancellationToken.None);
        return await _db.Leads.FirstOrDefaultAsync();
    }

    private const string Owner = "<p>Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau.</p>";

    [Fact]
    public async Task A_listing_with_a_similar_name_but_another_website_gives_no_google_point()
    {
        var lead = await SubmitWithListing(new GooglePlacesResult { GoogleRating = 5f, GoogleReviewCount = 50, DisplayName = "Tine van Wel Ontwerp", Website = "https://www.other-company.example.com", GoogleMapsUrl = "https://maps.example/1" });
        Assert.Null(lead!.GoogleRating);
        Assert.Contains("does not show this business's website", (await _db.LeadNotes.SingleAsync()).Content);
    }

    [Fact]
    public async Task A_listing_on_the_businesss_own_domain_or_with_a_phone_number_from_its_pages_counts()
    {
        var byWebsite = await SubmitWithListing(new GooglePlacesResult { GoogleRating = 4.9f, GoogleReviewCount = 50, DisplayName = "X", Website = "https://tinevanwel.nl/", GoogleMapsUrl = "https://maps.example/1" });
        Assert.Equal(4.9f, byWebsite!.GoogleRating);

        _db.ChangeTracker.Clear();
        _db.Leads.RemoveRange(_db.Leads); _db.HuntJobs.RemoveRange(_db.HuntJobs); _db.HuntCandidates.RemoveRange(_db.HuntCandidates); _db.LeadNotes.RemoveRange(_db.LeadNotes);
        _db.SaveChanges();
        var byPhone = await SubmitWithListing(new GooglePlacesResult { GoogleRating = 4.7f, GoogleReviewCount = 9, DisplayName = "X", Phone = "06 14470482", GoogleMapsUrl = "https://maps.example/2" });
        Assert.Equal(4.7f, byPhone!.GoogleRating);
    }

    [Theory]
    [InlineData("https://a.wixsite.com/shop", "https://b.wixsite.com/other", false)]
    [InlineData("https://studio.github.io/x", "https://studio.github.io/y", true)]
    [InlineData("https://www.tinevanwel.nl/", "https://tinevanwel.nl", true)]
    [InlineData("https://tinevanwel.nl", "https://other.nl", false)]
    public void Hosted_site_domains_are_not_one_registrable_domain(string a, string b, bool same) =>
        Assert.Equal(same, UrlGuard.RegistrableDomain(new Uri(a).Host) == UrlGuard.RegistrableDomain(new Uri(b).Host));

    // ---- 6: only values the server wrote may be cleared ------------------------------------------------------------

    [Fact]
    public async Task A_hunt_lead_that_a_person_edited_keeps_the_edit_and_an_untouched_hunt_value_goes()
    {
        var job = NewHunt(target: 5);
        await Service(Pages(Owner, contact: "<p>Bel mij op 06 144 70 482</p>", about: "<p>Over mij</p>")).SubmitCandidateAsync(job,
            new HuntService.Submission("Tine", Site, "Utrecht", "Design", Element(GoodExtraction()), null, null), CancellationToken.None);
        var lead = await _db.Leads.SingleAsync();
        Assert.True(HuntProvenance.Unchanged(lead, "ownerMobile"));
        Assert.True(HuntProvenance.Unchanged(lead, "ownerName"));

        lead.OwnerMobile = "06 11 22 33 44";                           // the person edits this one afterwards
        lead.EmployeeCount = "4";
        _db.SaveChanges();
        Assert.False(HuntProvenance.Unchanged(lead, "ownerMobile"));

        // the website no longer shows what it showed
        var verify = new HuntJob { UserId = Me, Kind = HuntKinds.Verify, LeadIds = new List<Guid> { lead.Id }, Total = 1 };
        _db.HuntJobs.Add(verify);
        job.Status = HuntStatuses.Completed;
        _db.SaveChanges();
        await Service(Pages("<p>Welkom, niets te zien.</p>")).RunAsync(verify.Id, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var after = await _db.Leads.SingleAsync();
        Assert.Equal("06 11 22 33 44", after.OwnerMobile);             // edited by a person: stays
        Assert.Equal("4", after.EmployeeCount);
        Assert.Equal("", after.OwnerName);                              // untouched hunt values: removed
        Assert.Equal("", after.PersonalEmail);
        Assert.Null(after.FoundingYear);
    }

    // ---- 7: the migrations ------------------------------------------------------------------------------------------

    private static void InsertRow(SqliteConnection conn, string table, Dictionary<string, string> values)
    {
        var cols = new List<(string Name, string Type)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (r.GetInt32(3) == 1 && r.IsDBNull(4)) cols.Add((r.GetString(1), r.GetString(2).ToUpperInvariant()));
        }
        var all = cols.Select(c => c.Name).Union(values.Keys).ToList();
        string Fill((string Name, string Type) c) => values.TryGetValue(c.Name, out var v) ? v
            : c.Name == "Id" ? $"'{Guid.NewGuid()}'" : c.Type.Contains("INT") || c.Type.Contains("REAL") ? "0" : c.Name.EndsWith("At") ? "'2026-11-01 00:00:00'" : "''";
        var vals = all.Select(n => values.TryGetValue(n, out var v) ? v : Fill(cols.First(c => c.Name == n))).ToList();
        using var ins = conn.CreateCommand();
        ins.CommandText = $"INSERT INTO {table} ({string.Join(",", all)}) VALUES ({string.Join(",", vals)})";
        ins.ExecuteNonQuery();
    }

    [Fact]
    public void A_database_with_two_active_jobs_for_one_user_upgrades_and_keeps_only_the_newest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hunt-mig2-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new TestOrgInterceptor()).Options;
            string oldest = Guid.NewGuid().ToString(), newest = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString(), done = Guid.NewGuid().ToString();
            using (var db = new LeadManagerDbContext(options))
            {
                // the state of test and acc: the first version of the hunt migration, applied, before the unique index existed
                db.GetService<IMigrator>().Migrate("20261119155650_AddLeadHunt");
                using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
                conn.Open();
                InsertRow(conn, "HuntJobs", new() { ["Id"] = $"'{oldest}'", ["UserId"] = "'u1'", ["Status"] = "'Running'", ["CreatedAt"] = "'2026-11-10 10:00:00'", ["LeadIds"] = "'[]'" });
                InsertRow(conn, "HuntJobs", new() { ["Id"] = $"'{newest}'", ["UserId"] = "'u1'", ["Status"] = "'Pending'", ["CreatedAt"] = "'2026-11-11 10:00:00'", ["LeadIds"] = "'[]'" });
                InsertRow(conn, "HuntJobs", new() { ["Id"] = $"'{other}'", ["UserId"] = "'u2'", ["Status"] = "'Running'", ["CreatedAt"] = "'2026-11-10 10:00:00'", ["LeadIds"] = "'[]'" });
                InsertRow(conn, "HuntJobs", new() { ["Id"] = $"'{done}'", ["UserId"] = "'u1'", ["Status"] = "'Completed'", ["CreatedAt"] = "'2026-11-09 10:00:00'", ["LeadIds"] = "'[]'" });
                db.GetService<IMigrator>().Migrate();               // must not throw on the unique index
            }
            using var after = new LeadManagerDbContext(options);
            var jobs = after.HuntJobs.AsNoTracking().ToDictionary(j => j.Id.ToString(), j => j, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(HuntStatuses.Failed, jobs[oldest].Status);
            Assert.Contains("Superseded", jobs[oldest].Error);
            Assert.Equal(HuntStatuses.Pending, jobs[newest].Status);
            Assert.Equal(HuntStatuses.Running, jobs[other].Status);
            Assert.Equal(HuntStatuses.Completed, jobs[done].Status);
            Assert.False(jobs[newest].ConfirmClear);
            // the unique index is in place now
            after.HuntJobs.Add(new HuntJob { UserId = "u2", Status = HuntStatuses.Running, Segment = "x" });
            Assert.Throws<DbUpdateException>(() => after.SaveChanges());
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_database_that_stopped_at_the_previous_migration_gets_the_lead_provenance_column_and_keeps_its_leads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hunt-mig3-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new TestOrgInterceptor()).Options;
            using (var db = new LeadManagerDbContext(options))
            {
                db.GetService<IMigrator>().Migrate("20261119164155_HuntActiveJobIndexAndConfirm");   // where test and acc stand
                using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
                conn.Open();
                InsertRow(conn, "Leads", new() { ["Name"] = "'Typed by hand'", ["OwnerMobile"] = "'06 12 34 56 78'" });
                db.GetService<IMigrator>().Migrate();
            }
            using var after = new LeadManagerDbContext(options);
            var lead = after.Leads.Single();
            Assert.Equal("06 12 34 56 78", lead.OwnerMobile);
            Assert.Null(lead.HuntWrittenJson);                     // nothing in an existing lead counts as written by the server
            Assert.False(HuntProvenance.Unchanged(lead, "ownerMobile"));
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }

    // ---- 8: names come from the verified owner ----------------------------------------------------------------------

    [Fact]
    public async Task The_first_name_last_name_and_title_are_derived_from_the_verified_owner_and_a_typed_kvk_is_kept()
    {
        var f = Pages("<p>Ik ben Tine van Wel, eigenaar</p><p>KvK 12345678</p>");
        var claims = Json(new
        {
            ownerName = Claim("Tine van Wel", "Tine van Wel, eigenaar", Site),
            kvkNumber = Claim("12345678", "KvK 12345678", Site),
            ownerFirstName = "Evil", ownerLastName = "Hacker", ownerTitle = "President",
        });
        var (result, lead) = await Run(f, claims);
        Assert.Equal("Tine", lead.OwnerFirstName);
        Assert.Equal("van Wel", lead.OwnerLastName);
        Assert.Equal("Eigenaar", lead.OwnerTitle);
        Assert.DoesNotContain("ownerFirstName", result.Descriptive.Keys);
        Assert.Equal("12345678", lead.KvkNumber);

        var typed = new Lead { Name = "Tine", Website = Site, KvkNumber = "99999999" };
        var kept = new List<string>();
        LeadVerifier.Apply(typed, result, clearUnverified: false, new SalesScoreService(), kept);
        Assert.Equal("99999999", typed.KvkNumber);
        Assert.Contains(kept, k => k.Contains("kvkNumber"));
    }

    // ---- 9: the cheap ones ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_year_for_a_newsletter_or_a_website_is_not_a_founding_year_but_one_for_the_business_is()
    {
        Assert.DoesNotContain(Extract("<p>Onze nieuwsbrief is gestart in 2008.</p>").Years, y => y.Year == 2008);
        Assert.DoesNotContain(Extract("<p>Deze website is opgericht in 2009 voor het bedrijf.</p>").Years, y => y.Year == 2009);
        Assert.Contains(Extract("<p>Het bedrijf is opgericht in 2008.</p>").Years, y => y.Year == 2008);
        Assert.Contains(Extract("<p>In 2010 ben ik met Goldenbeld Advies gestart.</p>").Years, y => y.Year == 2010);
    }

    [Theory]
    [InlineData("tinevanwel", "Tine van Wel", true)]
    [InlineData("tine-van-wel-123", "Tine van Wel", true)]
    [InlineData("vanwel", "Tine van Wel", false)]        // the last name alone
    [InlineData("twel", "Tine van Wel", false)]          // an initial and the last name
    [InlineData("tine", "Tine van Wel", false)]          // the first name alone
    [InlineData("sabobomediation", "Petra Delhez", false)]
    public void A_linkedin_handle_must_carry_the_whole_name(string handle, string owner, bool expected) =>
        Assert.Equal(expected, LeadEvidenceService.HandleCarriesName(handle, owner));

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly string _json;
        public FuncHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json, Encoding.UTF8, "application/json") });
    }

    [Theory]
    [InlineData("{\"id\":7,\"url\":\"javascript:alert(1)\"}", null)]
    [InlineData("{\"id\":7,\"url\":\"data:text/html,x\"}", null)]
    [InlineData("{\"id\":7,\"url\":\"https://tasks.example.test/task/7\"}", "https://tasks.example.test/task/7")]
    public async Task Only_an_http_task_link_is_kept_by_the_dispatcher(string response, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JengoWork:BaseUrl"] = "https://tasks.example.test", ["JengoWork:ApiKey"] = "k", ["Hunt:Dispatch:BoardId"] = "20",
        }).Build();
        var dispatcher = new JengoWorkHuntDispatcher(new HttpClient(new FuncHandler(response)), config);
        var result = await dispatcher.DispatchAsync(new HuntJob { Segment = "x" }, "prompt", CancellationToken.None);
        Assert.Equal("7", result.TaskId);
        Assert.Equal(expected, result.TaskUrl);
    }

    [Fact]
    public async Task The_lock_of_a_finished_job_is_released_and_forgotten()
    {
        var job = NewHunt();
        var held = HuntLocks.TryEnterJob(job.Id);
        Assert.NotNull(held);
        await Service(new FakePageFetcher()).CompleteAsync(job, "done", failed: false);
        // the entry was dropped: a new lock for the same job is a fresh one
        using var again = HuntLocks.TryEnterJob(job.Id);
        Assert.NotNull(again);
        held!.Dispose();
    }

    [Fact]
    public void The_prompt_and_the_dispatcher_text_say_where_the_token_is_and_why_that_is_fine()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LeadManager.Api", "Services", "Hunt", "HuntDispatcher.cs"));
        Assert.DoesNotContain("never contains a secret", source);
        Assert.Contains("token OF THIS HUNT", source);
    }
}

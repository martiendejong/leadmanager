using System.Security.Claims;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
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

public class HuntServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private const string Me = "user-me";
    private const string Other = "user-other";

    public HuntServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        foreach (var id in new[] { Me, Other }) _db.Users.Add(new ApplicationUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), Email = id + "@example.nl" });
        _db.SaveChanges();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private HuntService Service(FakePageFetcher fetcher, FakeLlm llm, FakeDiscovery discovery)
    {
        var evidence = new LeadEvidenceService();
        var verifier = new LeadVerifier(fetcher, evidence, llm, NullLogger<LeadVerifier>.Instance);
        var places = new GooglePlacesEnrichmentService(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);
        return new HuntService(_db, verifier, evidence, discovery, places, new SalesScoreService(), new NoopHub(), NullLogger<HuntService>.Instance);
    }

    private static FakeLlm GoodLlm() => new((s, u) => FakeLlm.IsCritic(s) ? CriticVerifiesAll(u) : GoodExtraction());

    private HuntJob NewHunt(int min = 8, int target = 1, int max = 10, bool ratchet = true, int calls = 40, string user = Me)
    {
        var job = new HuntJob { UserId = user, Segment = "graphic designers", MinScore = min, TargetCount = target, MaxCandidates = max, Ratchet = ratchet, MaxLlmCalls = calls, Total = max };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private static DiscoveredCompany Company(string name, string site) => new(name, site, "Utrecht", "Design", "test");

    private const string Weak = "https://weak.example.nl/";
    private const string Mid = "https://mid.example.nl/";

    private static FakePageFetcher Fetcher()
    {
        var f = GoodSite();
        // No mobile and no LinkedIn: the quick look sees that 8 is out of reach.
        f.Add(Weak, "<p>Welkom bij Weak. Bel 010 206 12 34 of mail info@weak.example.nl</p>");
        // Mobile, company LinkedIn and a named mailbox: at most 7.
        f.Add(Mid, "<p>Mid Studio. Bel 06 12 34 56 78, mail piet@mid.example.nl</p><a href=\"https://www.linkedin.com/company/mid-studio\">LinkedIn</a>");
        return f;
    }

    [Fact]
    public async Task A_hunt_stores_every_researched_candidate_and_stops_at_the_target()
    {
        var job = NewHunt(target: 1);
        var discovery = new FakeDiscovery(new[] { Company("Weak", Weak), Company("Tine", Site), Company("Never reached", "https://never.example.nl/") });
        var llm = GoodLlm();
        await Service(Fetcher(), llm, discovery).RunAsync(job.Id, CancellationToken.None);

        var done = await _db.HuntJobs.Include(j => j.Candidates).SingleAsync();
        Assert.Equal(HuntStatuses.Completed, done.Status);
        Assert.Equal(1, done.Qualified);
        Assert.Equal(8, done.BestScore);
        Assert.Equal(2, done.Screened); // the third was never looked at: the target was reached
        Assert.Equal(1 + llm.Calls, done.LlmCalls);

        var weak = await _db.Leads.SingleAsync(l => l.Name == "Weak");
        Assert.Null(weak.SalesPriorityScore); // not researched, so no score is claimed
        Assert.Equal(HuntOutcomes.StoppedEarly, done.Candidates.Single(c => c.Name == "Weak").Outcome);
        Assert.True(done.Candidates.Single(c => c.Name == "Weak").MaxReachable < 8);

        var good = await _db.Leads.SingleAsync(l => l.Name == "Tine");
        Assert.Equal(8, good.SalesPriorityScore);
        Assert.StartsWith("Lead hunt ", good.Source);
        Assert.Equal(Me, good.ImportedByUserId);
        var note = await _db.LeadNotes.SingleAsync(n => n.LeadId == good.Id);
        Assert.Contains("Verified (quote and source)", note.Content);
        Assert.Contains("tine@tinevanwel.nl", note.Content);
        Assert.Equal(HuntOutcomes.Qualified, done.Candidates.Single(c => c.Name == "Tine").Outcome);
    }

    [Fact]
    public async Task The_ratchet_stops_researching_candidates_that_cannot_beat_the_best_so_far()
    {
        var job = NewHunt(min: 5, target: 3, ratchet: true);
        var discovery = new FakeDiscovery(new[] { Company("Tine", Site), Company("Mid", Mid) });
        await Service(Fetcher(), GoodLlm(), discovery).RunAsync(job.Id, CancellationToken.None);
        var mid = await _db.HuntCandidates.SingleAsync(c => c.Name == "Mid");
        Assert.Equal(HuntOutcomes.StoppedEarly, mid.Outcome); // upper bound 7 is below the best score so far (8)
        Assert.Contains("the bar is 8", mid.Reason);
    }

    [Fact]
    public async Task Without_the_ratchet_the_minimum_is_the_only_bar()
    {
        var job = NewHunt(min: 5, target: 3, ratchet: false);
        var discovery = new FakeDiscovery(new[] { Company("Tine", Site), Company("Mid", Mid) });
        // The model finds nothing for the mid site: a researched candidate with a low score is still stored.
        var llm = new FakeLlm((s, u) => FakeLlm.IsCritic(s) ? CriticVerifiesAll(u) : u.Contains("Mid Studio") ? "{}" : GoodExtraction());
        await Service(Fetcher(), llm, discovery).RunAsync(job.Id, CancellationToken.None);
        var mid = await _db.HuntCandidates.SingleAsync(c => c.Name == "Mid");
        Assert.Equal(HuntOutcomes.BelowMinimum, mid.Outcome);
        Assert.Equal(0, mid.Score);
        Assert.NotNull(await _db.Leads.SingleAsync(l => l.Name == "Mid"));
    }

    [Fact]
    public async Task Companies_that_are_already_leads_or_repeated_are_not_screened_again()
    {
        _db.Leads.Add(new Lead { Name = "Existing", Website = "https://www.tinevanwel.nl", ImportedByUserId = Me });
        _db.SaveChanges();
        var job = NewHunt(target: 2);
        var discovery = new FakeDiscovery(new[] { Company("Tine again", Site), Company("Weak", Weak), Company("Weak twice", Weak) });
        await Service(Fetcher(), GoodLlm(), discovery).RunAsync(job.Id, CancellationToken.None);
        var done = await _db.HuntJobs.SingleAsync();
        Assert.Equal(1, done.Screened);
        Assert.Contains("https://www.tinevanwel.nl", _db.Leads.Select(l => l.Website));
        Assert.Contains("tinevanwel.nl", discovery.Excluded[0]); // the search is told which domains to skip
    }

    [Fact]
    public async Task Other_users_leads_do_not_hide_a_company_from_this_user()
    {
        _db.Leads.Add(new Lead { Name = "Theirs", Website = Site, ImportedByUserId = Other });
        _db.SaveChanges();
        var job = NewHunt();
        await Service(Fetcher(), GoodLlm(), new FakeDiscovery(new[] { Company("Tine", Site) })).RunAsync(job.Id, CancellationToken.None);
        Assert.Equal(1, (await _db.HuntJobs.SingleAsync()).Qualified);
    }

    [Fact]
    public async Task An_unreachable_site_is_recorded_but_not_added_as_a_lead()
    {
        var job = NewHunt();
        await Service(new FakePageFetcher(), GoodLlm(), new FakeDiscovery(new[] { Company("Ghost", "https://ghost.example.nl/") })).RunAsync(job.Id, CancellationToken.None);
        Assert.Equal(HuntOutcomes.Unreachable, (await _db.HuntCandidates.SingleAsync()).Outcome);
        Assert.Empty(_db.Leads);
    }

    [Fact]
    public async Task A_search_without_new_companies_ends_the_job_with_a_clear_reason()
    {
        var job = NewHunt();
        var discovery = new FakeDiscovery(new IReadOnlyList<DiscoveredCompany>[] { Array.Empty<DiscoveredCompany>() });
        await Service(Fetcher(), GoodLlm(), discovery).RunAsync(job.Id, CancellationToken.None);
        var done = await _db.HuntJobs.SingleAsync();
        Assert.Equal(HuntStatuses.Completed, done.Status);
        Assert.Contains("no new companies", done.Error);
        Assert.Equal(1, discovery.Rounds); // the same search is not retried with other words
    }

    [Fact]
    public async Task A_cancelled_job_does_no_work()
    {
        var job = NewHunt();
        job.CancelRequested = true;
        _db.SaveChanges();
        var discovery = new FakeDiscovery(new[] { Company("Tine", Site) });
        await Service(Fetcher(), GoodLlm(), discovery).RunAsync(job.Id, CancellationToken.None);
        Assert.Equal(HuntStatuses.Cancelled, (await _db.HuntJobs.SingleAsync()).Status);
        Assert.Equal(0, discovery.Rounds);
    }

    [Fact]
    public async Task The_call_budget_ends_the_job_without_losing_what_was_researched()
    {
        var job = NewHunt(target: 5, calls: 3, min: 5);
        var discovery = new FakeDiscovery(new[] { Company("Tine", Site), Company("Mid", Mid) });
        var llm = GoodLlm();
        await Service(Fetcher(), llm, discovery).RunAsync(job.Id, CancellationToken.None);
        var done = await _db.HuntJobs.SingleAsync();
        Assert.True(done.LlmCalls <= 3);
        Assert.Equal(HuntStatuses.Completed, done.Status);
        Assert.NotNull(await _db.Leads.SingleOrDefaultAsync(l => l.Name == "Tine"));
    }

    [Fact]
    public async Task A_verify_job_rescored_an_existing_lead_and_removes_what_cannot_be_proven()
    {
        var lead = new Lead
        {
            Name = "Tine", Website = Site, ImportedByUserId = Me, OwnerName = "Wrong Person", GoogleRating = 5f, GoogleReviewCount = 3, FoundingYear = 2009,
            PersonalEmail = "old@elsewhere.nl", City = "Utrecht",
        };
        _db.Leads.Add(lead);
        var theirs = new Lead { Name = "Not mine", Website = "https://theirs.example.nl", ImportedByUserId = Other, OwnerName = "Keep Me" };
        _db.Leads.Add(theirs);
        _db.SaveChanges();
        var job = new HuntJob { UserId = Me, Kind = HuntKinds.Verify, LeadIds = new List<Guid> { lead.Id, theirs.Id }, Total = 2, MaxLlmCalls = 20 };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();

        await Service(Fetcher(), GoodLlm(), new FakeDiscovery()).RunAsync(job.Id, CancellationToken.None);

        _db.ChangeTracker.Clear();
        var after = await _db.Leads.SingleAsync(l => l.Id == lead.Id);
        Assert.Equal(8, after.SalesPriorityScore);
        Assert.Equal("Tine van Wel", after.OwnerName);
        Assert.Equal(2015, after.FoundingYear);                    // replaced by the verified year
        Assert.Equal("tine@tinevanwel.nl", after.PersonalEmail);
        Assert.Null(after.GoogleRating);                           // a rating without a Google listing is removed
        var note = await _db.LeadNotes.SingleAsync(n => n.LeadId == lead.Id);
        Assert.Contains("Removed because it could not be verified", note.Content);
        Assert.Contains("googleRating", note.Content);
        Assert.Equal("Keep Me", (await _db.Leads.SingleAsync(l => l.Id == theirs.Id)).OwnerName); // somebody else's lead is never touched
        Assert.Equal(HuntOutcomes.Verified, (await _db.HuntCandidates.SingleAsync()).Outcome);
        Assert.Equal(1, (await _db.HuntJobs.SingleAsync()).Screened);
    }

    [Fact]
    public async Task A_verify_of_an_unreachable_site_leaves_the_values_alone()
    {
        var lead = new Lead { Name = "Ghost", Website = "https://ghost.example.nl", ImportedByUserId = Me, OwnerName = "Keep Me" };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        var job = new HuntJob { UserId = Me, Kind = HuntKinds.Verify, LeadIds = new List<Guid> { lead.Id }, Total = 1, MaxLlmCalls = 10 };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();
        await Service(new FakePageFetcher(), GoodLlm(), new FakeDiscovery()).RunAsync(job.Id, CancellationToken.None);
        _db.ChangeTracker.Clear();
        var after = await _db.Leads.SingleAsync();
        Assert.Equal("Keep Me", after.OwnerName);
        Assert.Equal(WebsiteStatus.Unreachable, after.WebsiteStatus);
    }

    // ---- controller --------------------------------------------------------------------------------------

    private HuntController Controller(string user, bool aiConfigured = true, HuntQueue? queue = null)
    {
        var places = new GooglePlacesEnrichmentService(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);
        var controller = new HuntController(_db, new FakeLlm((_, _) => "{}", aiConfigured), places, queue ?? new HuntQueue(), new HuntRegistry(), new ConfigurationBuilder().Build());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, user) }, "Test")) },
        };
        return controller;
    }

    [Fact]
    public async Task Without_an_AI_key_every_start_says_so_with_a_503()
    {
        var lead = new Lead { Name = "A", Website = "https://a.example.nl", ImportedByUserId = Me };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        var c = Controller(Me, aiConfigured: false);

        foreach (var result in new[]
        {
            await c.Start(new StartHuntRequest("graphic designers", null, null, null, null, null)),
            await c.VerifyOne(lead.Id),
            await c.VerifyMany(new VerifyLeadsRequest(new[] { lead.Id })),
        })
        {
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, obj.StatusCode);
            Assert.Contains("AI key not configured on this server", System.Text.Json.JsonSerializer.Serialize(obj.Value));
        }
        Assert.Empty(_db.HuntJobs);

        var caps = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(c.Capabilities()).Value);
        Assert.Contains("\"aiConfigured\":false", caps);
    }

    [Fact]
    public async Task A_valid_start_creates_a_queued_job_and_only_one_may_run_at_a_time()
    {
        var queue = new HuntQueue();
        var c = Controller(Me, queue: queue);
        var accepted = Assert.IsType<AcceptedResult>(await c.Start(new StartHuntRequest(" graphic designers ", " Utrecht ", 8, 2, 20, true)));
        Assert.NotNull(accepted.Value);
        var job = await _db.HuntJobs.SingleAsync();
        Assert.Equal("graphic designers", job.Segment);
        Assert.Equal(HuntStatuses.Pending, job.Status);
        Assert.Equal(Me, job.UserId);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal(job.Id, queued);

        Assert.IsType<ConflictObjectResult>(await c.Start(new StartHuntRequest("something else", null, null, null, null, null)));
        // another user is not blocked by it
        Assert.IsType<AcceptedResult>(await Controller(Other).Start(new StartHuntRequest("something else", null, null, null, null, null)));
    }

    [Theory]
    [InlineData("", 8, 3, 30)]
    [InlineData("ab", 8, 3, 30)]
    [InlineData("designers", 10, 3, 30)]   // the score cannot reach 10
    [InlineData("designers", 0, 3, 30)]
    [InlineData("designers", 8, 0, 30)]
    [InlineData("designers", 8, 11, 30)]
    [InlineData("designers", 8, 3, 0)]
    [InlineData("designers", 8, 3, 61)]
    public async Task Invalid_criteria_are_refused(string segment, int min, int target, int max)
    {
        var result = await Controller(Me).Start(new StartHuntRequest(segment, null, min, target, max, null));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_db.HuntJobs);
    }

    [Fact]
    public async Task Verify_only_takes_the_callers_leads_and_a_limited_number()
    {
        var mine = new Lead { Name = "Mine", Website = "https://mine.example.nl", ImportedByUserId = Me };
        var theirs = new Lead { Name = "Theirs", Website = "https://theirs.example.nl", ImportedByUserId = Other };
        _db.Leads.AddRange(mine, theirs);
        _db.SaveChanges();
        var c = Controller(Me);

        Assert.IsType<NotFoundObjectResult>(await c.VerifyOne(theirs.Id));
        Assert.IsType<BadRequestObjectResult>(await c.VerifyMany(new VerifyLeadsRequest(Array.Empty<Guid>())));
        Assert.IsType<BadRequestObjectResult>(await c.VerifyMany(new VerifyLeadsRequest(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray())));

        Assert.IsType<AcceptedResult>(await c.VerifyMany(new VerifyLeadsRequest(new[] { mine.Id, theirs.Id })));
        Assert.Equal(new List<Guid> { mine.Id }, (await _db.HuntJobs.SingleAsync()).LeadIds);
    }

    [Fact]
    public async Task Jobs_and_evidence_are_scoped_to_their_owner_and_cancel_works()
    {
        var c = Controller(Me);
        await c.Start(new StartHuntRequest("designers", null, null, null, null, null));
        var job = await _db.HuntJobs.SingleAsync();

        Assert.IsType<NotFoundResult>(await Controller(Other).Get(job.Id));
        Assert.IsType<NotFoundResult>(await Controller(Other).Cancel(job.Id));
        Assert.IsType<OkObjectResult>(await c.Get(job.Id));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(Assert.IsType<OkObjectResult>(await Controller(Other).Recent()).Value!));

        Assert.IsType<OkObjectResult>(await c.Cancel(job.Id));
        _db.ChangeTracker.Clear();
        Assert.Equal(HuntStatuses.Cancelled, (await _db.HuntJobs.SingleAsync()).Status);

        var lead = new Lead { Name = "A", Website = "https://a.example.nl", ImportedByUserId = Me };
        _db.Leads.Add(lead);
        _db.HuntCandidates.Add(new HuntCandidate { JobId = job.Id, LeadId = lead.Id, Name = "A", Outcome = HuntOutcomes.Verified, EvidenceJson = "{\"verified\":[]}" });
        _db.SaveChanges();
        Assert.IsType<NotFoundResult>(await Controller(Other).Evidence(lead.Id));
        Assert.Contains("\"available\":true", System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await c.Evidence(lead.Id)).Value));
    }

    // ---- migration ---------------------------------------------------------------------------------------

    [Fact]
    public void The_hunt_migration_applies_to_a_database_that_already_holds_leads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hunt-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            using (var db = new LeadManagerDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                migrator.Migrate("20261119105357_AddLeadUpdatedAt");
                // A lead as it exists in production before this migration: fill every required column generically.
                using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
                conn.Open();
                var cols = new List<(string Name, string Type)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(Leads)";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) if (r.GetInt32(3) == 1 && r.IsDBNull(4)) cols.Add((r.GetString(1), r.GetString(2).ToUpperInvariant()));
                }
                var values = cols.Select(c => c.Name == "Id" ? $"'{Guid.NewGuid()}'" : c.Type.Contains("INT") || c.Type.Contains("REAL") ? "0" : c.Name.EndsWith("At") ? "'2026-11-01 00:00:00'" : "''");
                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = $"INSERT INTO Leads ({string.Join(",", cols.Select(c => c.Name))}) VALUES ({string.Join(",", values)})";
                    ins.ExecuteNonQuery();
                }
                migrator.Migrate();
            }
            using var after = new LeadManagerDbContext(options);
            Assert.Equal(1, after.Leads.Count());
            var job = new HuntJob { UserId = "u", Segment = "x", LeadIds = new List<Guid> { Guid.NewGuid() } };
            after.HuntJobs.Add(job);
            after.HuntCandidates.Add(new HuntCandidate { JobId = job.Id, Name = "n" });
            after.SaveChanges();
            Assert.Equal(1, after.HuntJobs.Include(j => j.Candidates).Single().Candidates.Count);
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }
}

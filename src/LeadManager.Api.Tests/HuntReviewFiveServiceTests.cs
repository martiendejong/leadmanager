using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.Hubs;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LeadManager.Api.Tests.HuntJson;

namespace LeadManager.Api.Tests;

/// <summary>The fifth independent review of the lead hunt (PR 66): tokens, dispatch, races, the review score, limits, the hub, evidence of deleted leads.</summary>
public class HuntReviewFiveServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LeadManagerDbContext _db;
    private const string Me = "user-me";
    private const string Other = "user-other";
    private const string AdminRoleId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    public HuntReviewFiveServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new LeadManagerDbContext(new DbContextOptionsBuilder<LeadManagerDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        foreach (var id in new[] { Me, Other }) _db.Users.Add(new ApplicationUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), Email = id + "@example.nl" });
        _db.UserRoles.Add(new IdentityUserRole<string> { UserId = Me, RoleId = AdminRoleId });
        _db.SaveChanges();
        HuntAuthThrottle.Reset();
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static readonly (string, string?)[] Ready = { ("Sso:CallbackBaseUrl", "https://clients.example.test") };

    private static GooglePlacesEnrichmentService Places() =>
        new(new HttpClient(), new ConfigurationBuilder().Build(), NullLogger<GooglePlacesEnrichmentService>.Instance);

    private HuntService Service(FakePageFetcher fetcher)
    {
        var verifier = new LeadVerifier(fetcher, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        return new HuntService(_db, verifier, Places(), new SalesScoreService(), new NoopHub(), NullLogger<HuntService>.Instance);
    }

    private HuntController Controller(string user, FakeDispatcher? dispatcher = null, IConfiguration? config = null)
    {
        var controller = new HuntController(_db, dispatcher ?? new FakeDispatcher(), Places(), new HuntQueue(), new HuntRegistry(), config ?? Config(Ready), Service(GoodSite()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, user) }, "Test")) },
        };
        return controller;
    }

    private HuntAgentController Agent(string? token, FakePageFetcher? fetcher = null, string ip = "203.0.113.7")
    {
        var controller = new HuntAgentController(_db, Service(fetcher ?? GoodSite()));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (token != null) http.Request.Headers[HuntAgentController.TokenHeader] = token;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static StartHuntRequest Req(string segment = "graphic designers") => new(segment, null, 8, 2, 20, true);

    private HuntJob NewHunt(string user = Me, string status = HuntStatuses.Running, string kind = HuntKinds.Hunt, int target = 5, int max = 10)
    {
        var job = new HuntJob { UserId = user, Kind = kind, Segment = "graphic designers", MinScore = 8, TargetCount = target, MaxCandidates = max, Total = max, Status = status };
        _db.HuntJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private string TaskTokenFor(HuntJob job)
    {
        var token = HuntTokens.Issue(job);
        _db.SaveChanges();
        return token;
    }

    private static string TaskTokenOf(string prompt) =>
        Regex.Match(prompt.Split('\n').First(l => l.Contains("X-Hunt-Token: ")), @"X-Hunt-Token: ([A-Za-z0-9_-]{40,})").Groups[1].Value;

    private static string Text(object? value) => JsonSerializer.Serialize(value);

    private static string SessionOf(IActionResult brief) =>
        JsonDocument.Parse(Text(Assert.IsType<OkObjectResult>(brief).Value)).RootElement.GetProperty("session").GetProperty("token").GetString()!;

    // ---- 4: the task token is exchanged for a session token ------------------------------------------------------------

    [Fact]
    public async Task The_task_token_opens_brief_only_and_the_first_brief_exchanges_it_for_a_session_token()
    {
        var dispatcher = new FakeDispatcher();
        Assert.IsType<AcceptedResult>(await Controller(Me, dispatcher).Start(Req(), default));
        var taskToken = TaskTokenOf(Assert.Single(dispatcher.Dispatched).Prompt);
        var job = await _db.HuntJobs.SingleAsync();
        Assert.False(job.TokenExchanged);
        Assert.InRange(job.TokenExpiresAt!.Value, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));

        // every other call refuses the task token
        Assert.IsType<UnauthorizedObjectResult>(await Agent(taskToken).Progress(job.Id, new HuntProgressRequest("x")));
        Assert.IsType<UnauthorizedObjectResult>(await Agent(taskToken).Submit(job.Id, new SubmitCandidateRequest { Name = "Tine", Website = Site, Claims = Element("{}") }, default));
        Assert.IsType<UnauthorizedObjectResult>(await Agent(taskToken).Complete(job.Id, new CompleteHuntRequest("x")));
        Assert.IsType<UnauthorizedObjectResult>(await Agent(taskToken).Known(job.Id, new KnownDomainsRequest(new[] { "a.nl" })));

        var agent = Agent(taskToken);
        var brief = await agent.Brief(job.Id);
        var session = SessionOf(brief);
        Assert.NotEqual(taskToken, session);
        Assert.Equal("no-store", agent.Response.Headers.CacheControl.ToString());
        _db.ChangeTracker.Clear();
        var after = await _db.HuntJobs.SingleAsync();
        Assert.True(after.TokenExchanged);
        Assert.Equal(HuntStatuses.Running, after.Status);
        Assert.InRange(after.TokenExpiresAt!.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
        Assert.False(HuntTokens.Valid(after, taskToken));    // the task token is revoked
        Assert.True(HuntTokens.Valid(after, session));
        Assert.DoesNotContain(session, after.TokenHash);

        // the task token is dead for brief too; the session token works everywhere and is not repeated in the answer
        Assert.IsType<UnauthorizedObjectResult>(await Agent(taskToken).Brief(job.Id));
        Assert.IsType<OkObjectResult>(await Agent(session).Progress(job.Id, new HuntProgressRequest("Tine")));
        var again = JsonDocument.Parse(Text(Assert.IsType<OkObjectResult>(await Agent(session).Brief(job.Id)).Value)).RootElement;
        Assert.Equal(JsonValueKind.Null, again.GetProperty("session").ValueKind);
        Assert.DoesNotContain(session, Text(((OkObjectResult)await Agent(session).Brief(job.Id)).Value));
    }

    [Fact]
    public async Task A_session_token_slides_with_use_but_never_past_three_hours_from_the_start_of_the_hunt()
    {
        var job = NewHunt();
        var session = TaskTokenFor(job);
        job.TokenExchanged = true;
        job.TokenExpiresAt = DateTime.UtcNow.AddMinutes(2);
        _db.SaveChanges();
        Assert.IsType<OkObjectResult>(await Agent(session).Progress(job.Id, new HuntProgressRequest("x")));
        _db.ChangeTracker.Clear();
        Assert.InRange((await _db.HuntJobs.SingleAsync()).TokenExpiresAt!.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));

        var old = new HuntJob { CreatedAt = DateTime.UtcNow.AddHours(-2.9) };
        Assert.True(HuntTokens.SessionExpiry(old, DateTime.UtcNow) <= old.CreatedAt.AddHours(3));
        Assert.True(HuntTokens.SessionExpiry(old, DateTime.UtcNow) < DateTime.UtcNow.AddMinutes(10));
        Assert.True(HuntTokens.Lifetime <= TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task Wrong_tokens_are_charged_hard_per_hunt_and_per_address_even_for_the_right_token_afterwards()
    {
        var job = NewHunt();
        var session = TaskTokenFor(job);
        job.TokenExchanged = true;
        _db.SaveChanges();
        for (var i = 0; i < HuntAuthThrottle.MaxFailuresPerHunt; i++)
            Assert.IsType<UnauthorizedObjectResult>(await Agent("wrong-" + i).Progress(job.Id, new HuntProgressRequest("x")));
        var blocked = Assert.IsType<ObjectResult>(await Agent(session).Progress(job.Id, new HuntProgressRequest("x")));
        Assert.Equal(429, blocked.StatusCode);
        // somebody who guessed at the hunt from one address does not lock the real agent out on another
        Assert.IsType<OkObjectResult>(await Agent(session, ip: "203.0.113.99").Progress(job.Id, new HuntProgressRequest("x")));

        // many hunts from one address: the address is blocked as a whole
        HuntAuthThrottle.Reset();
        for (var i = 0; i < HuntAuthThrottle.MaxFailuresPerAddress; i++)
            Assert.IsType<UnauthorizedObjectResult>(await Agent("wrong", ip: "198.51.100.9").Brief(Guid.NewGuid()));
        Assert.Equal(429, Assert.IsType<ObjectResult>(await Agent("wrong", ip: "198.51.100.9").Brief(Guid.NewGuid())).StatusCode);
        // another address is not affected
        Assert.IsType<UnauthorizedObjectResult>(await Agent("wrong", ip: "198.51.100.10").Brief(Guid.NewGuid()));

        // the window passes
        Assert.False(HuntAuthThrottle.Blocked("198.51.100.9", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(11)));
    }

    // ---- 8: rate limits and body limits ---------------------------------------------------------------------------------

    [Fact]
    public void The_global_limiter_counts_per_address_over_all_hunts_and_only_for_the_agent_paths()
    {
        HttpContext Ctx(string path, string ip, string? id = null)
        {
            var c = new DefaultHttpContext();
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (id != null) c.Request.RouteValues["id"] = id;
            return c;
        }
        var a = HuntRateLimit.GlobalPartition(Ctx("/api/hunt-agent/" + Guid.NewGuid() + "/brief", "203.0.113.1"));
        var b = HuntRateLimit.GlobalPartition(Ctx("/api/hunt-agent/" + Guid.NewGuid() + "/candidates", "203.0.113.1"));
        var c2 = HuntRateLimit.GlobalPartition(Ctx("/api/hunt-agent/x/brief", "203.0.113.2"));
        Assert.Equal(a.PartitionKey, b.PartitionKey);          // two hunts, one address, one counter
        Assert.NotEqual(a.PartitionKey, c2.PartitionKey);
        Assert.Equal("other", HuntRateLimit.GlobalPartition(Ctx("/api/leads", "203.0.113.1")).PartitionKey);

        using var limiter = a.Factory(a.PartitionKey);
        for (var i = 0; i < HuntRateLimit.PerAddressPerMinute; i++) Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.False(limiter.AttemptAcquire().IsAcquired);

        // the per-hunt policy keeps its own counter per hunt and address
        Assert.NotEqual(HuntRateLimit.PolicyKey(Ctx("/x", "203.0.113.1", "hunt-1")), HuntRateLimit.PolicyKey(Ctx("/x", "203.0.113.1", "hunt-2")));
    }

    [Theory]
    [InlineData(typeof(HuntAgentController), nameof(HuntAgentController.Submit))]
    [InlineData(typeof(HuntAgentController), nameof(HuntAgentController.Progress))]
    [InlineData(typeof(HuntAgentController), nameof(HuntAgentController.Complete))]
    [InlineData(typeof(HuntAgentController), nameof(HuntAgentController.Known))]
    [InlineData(typeof(HuntController), nameof(HuntController.Start))]
    [InlineData(typeof(HuntController), nameof(HuntController.ReviewByAgent))]
    [InlineData(typeof(HuntController), nameof(HuntController.VerifyMany))]
    public void Every_action_that_takes_a_body_has_a_size_limit(Type controller, string action)
    {
        var limit = controller.GetMethod(action)!.GetCustomAttribute<RequestSizeLimitAttribute>();
        Assert.NotNull(limit);
    }

    [Fact]
    public void The_candidate_endpoint_limit_is_a_few_times_the_claims_cap()
    {
        var bytes = typeof(HuntAgentController).GetMethod(nameof(HuntAgentController.Submit))!.GetCustomAttribute<RequestSizeLimitAttribute>()!;
        var field = typeof(RequestSizeLimitAttribute).GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance);
        var value = (long?)field?.GetValue(bytes);
        if (value != null) Assert.InRange(value.Value, 60_000, 400_000);
    }

    // ---- 5: dispatch ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_dispatch_tells_the_person_one_plain_sentence_and_does_not_use_up_the_day()
    {
        var config = Config(Ready.Append(("Hunt:MaxPerUserPerDay", "1")).ToArray());
        var failing = new FakeDispatcher { Fail = true };
        var result = Assert.IsType<ObjectResult>(await Controller(Me, failing, config).Start(Req(), default));
        Assert.Equal(502, result.StatusCode);
        var shown = Text(result.Value) + (await _db.HuntJobs.SingleAsync()).Error;
        Assert.Contains(HuntController.DispatchFailedText, shown);
        Assert.DoesNotContain("secret-detail", shown);
        Assert.DoesNotContain("JengoWork", shown);
        Assert.Null((await _db.HuntJobs.SingleAsync()).TaskId);

        // the cap is 1 a day, and the failed start did not count: the next start works, the one after it is capped
        Assert.IsType<AcceptedResult>(await Controller(Me, new FakeDispatcher(), config).Start(Req("again"), default));
        var open = await _db.HuntJobs.Where(j => j.Status == HuntStatuses.Pending).ToListAsync();
        foreach (var j in open) j.Status = HuntStatuses.Completed;
        _db.SaveChanges();
        Assert.Equal(429, Assert.IsType<ObjectResult>(await Controller(Me, new FakeDispatcher(), config).Start(Req("third"), default)).StatusCode);
    }

    [Fact]
    public async Task A_timeout_after_the_task_was_created_adopts_the_task_instead_of_failing()
    {
        var dispatcher = new FakeDispatcher { Uncertain = true, TaskExistsAfterTimeout = true };
        Assert.IsType<AcceptedResult>(await Controller(Me, dispatcher).Start(Req(), default));
        var job = await _db.HuntJobs.SingleAsync();
        Assert.Equal(1, dispatcher.FindCalls);
        Assert.Equal(HuntStatuses.Pending, job.Status);
        Assert.Equal("9002", job.TaskId);
        Assert.NotNull(job.TokenHash);   // the task keeps its token: it is the one in the task text
        Assert.True(HuntTokens.Valid(job, TaskTokenOf(Assert.Single(dispatcher.Dispatched).Prompt)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_timeout_without_a_task_to_be_found_fails_the_job_revokes_the_token_and_frees_the_person(bool searchFails)
    {
        var dispatcher = new FakeDispatcher { Uncertain = true, TaskExistsAfterTimeout = false, FindFails = searchFails };
        var result = Assert.IsType<ObjectResult>(await Controller(Me, dispatcher).Start(Req(), default));
        Assert.Equal(502, result.StatusCode);
        var job = await _db.HuntJobs.SingleAsync();
        Assert.Equal(HuntStatuses.Failed, job.Status);
        Assert.Null(job.TokenHash);
        Assert.Equal(HuntController.DispatchFailedText, job.Error);
        Assert.Equal(1, dispatcher.FindCalls);
        Assert.IsType<AcceptedResult>(await Controller(Me).Start(Req("again"), default));
    }

    [Fact]
    public async Task The_capabilities_say_whether_the_agent_starts_by_itself_and_how_long_a_person_has()
    {
        var plain = JsonDocument.Parse(Text(Assert.IsType<OkObjectResult>(await Controller(Me).Capabilities()).Value)).RootElement;
        Assert.False(plain.GetProperty("agentStartsAutomatically").GetBoolean());   // board 105 is not watched by jengo-agi: a person starts the task
        Assert.Equal(60, plain.GetProperty("startWithinMinutes").GetInt32());
        Assert.Equal(45, plain.GetProperty("silenceMinutes").GetInt32());
        var auto = JsonDocument.Parse(Text(Assert.IsType<OkObjectResult>(await Controller(Me, config: Config(Ready.Append(("Hunt:Dispatch:AgentPicksUp", "true")).ToArray())).Capabilities()).Value)).RootElement;
        Assert.True(auto.GetProperty("agentStartsAutomatically").GetBoolean());
    }

    [Fact]
    public async Task A_hunt_that_nobody_started_ends_after_an_hour_and_one_that_was_running_after_45_silent_minutes()
    {
        var waiting = NewHunt(status: HuntStatuses.Pending);
        waiting.CreatedAt = DateTime.UtcNow.AddMinutes(-50);
        var forgotten = NewHunt(Other, status: HuntStatuses.Pending);
        forgotten.CreatedAt = DateTime.UtcNow.AddMinutes(-70);
        _db.SaveChanges();
        Assert.Equal(1, await HuntService.SweepStaleAsync(_db, DateTime.UtcNow, HuntSweepService.Silence));
        _db.ChangeTracker.Clear();
        Assert.Equal(HuntStatuses.Pending, (await _db.HuntJobs.SingleAsync(j => j.Id == waiting.Id)).Status);   // 50 minutes: a person may still start it
        var ended = await _db.HuntJobs.SingleAsync(j => j.Id == forgotten.Id);
        Assert.Equal(HuntStatuses.Failed, ended.Status);
        Assert.Contains("Nobody started", ended.Error);
        Assert.Null(ended.TokenHash);
        Assert.Equal(HuntTokens.Lifetime, HuntSweepService.PendingGrace);   // the task token and the wait for a person end together
    }

    // ---- 6: conditional updates ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_late_call_does_not_bring_a_cancelled_hunt_back_to_life()
    {
        var job = NewHunt(status: HuntStatuses.Pending);
        var token = TaskTokenFor(job);
        job.TokenExchanged = true;
        _db.SaveChanges();
        // the person cancels in another request: the row changes, the agent's entity is stale
        await _db.Database.ExecuteSqlRawAsync("UPDATE HuntJobs SET Status = 'Cancelled', CancelRequested = 1, TokenHash = NULL WHERE Id = {0}", job.Id);
        Assert.Equal(HuntStatuses.Pending, job.Status);

        Assert.False(await Service(GoodSite()).TouchAsync(job));
        Assert.Equal(HuntStatuses.Cancelled, job.Status);   // reloaded
        _db.ChangeTracker.Clear();
        var stored = await _db.HuntJobs.SingleAsync();
        Assert.Equal(HuntStatuses.Cancelled, stored.Status);
        Assert.Null(stored.LastAgentCallAt);
        Assert.Null(stored.StartedAt);
        Assert.IsType<UnauthorizedObjectResult>(await Agent(token).Progress(job.Id, new HuntProgressRequest("x")));
    }

    [Fact]
    public async Task Completing_a_hunt_that_was_cancelled_in_between_keeps_it_cancelled()
    {
        var job = NewHunt();
        await _db.Database.ExecuteSqlRawAsync("UPDATE HuntJobs SET Status = 'Cancelled', CancelRequested = 1 WHERE Id = {0}", job.Id);
        await Service(GoodSite()).CompleteAsync(job, "All done.", failed: false);
        _db.ChangeTracker.Clear();
        var stored = await _db.HuntJobs.SingleAsync();
        Assert.Equal(HuntStatuses.Cancelled, stored.Status);
        Assert.Null(stored.Summary);

        var running = NewHunt(Other);
        await Service(GoodSite()).CompleteAsync(running, "Done.", failed: false);
        Assert.Equal(HuntStatuses.Completed, running.Status);
        await Service(GoodSite()).CompleteAsync(running, "Done again, as failed.", failed: true);   // an ended hunt stays as it ended
        _db.ChangeTracker.Clear();
        Assert.Equal(HuntStatuses.Completed, (await _db.HuntJobs.SingleAsync(j => j.Id == running.Id)).Status);
    }

    [Fact]
    public async Task A_review_that_is_cancelled_while_its_pages_are_fetched_changes_nothing()
    {
        var lead = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me };
        _db.Leads.Add(lead);
        var job = NewHunt(kind: HuntKinds.Review, target: 1, max: 1);
        job.LeadIds = new List<Guid> { lead.Id };
        _db.SaveChanges();

        var fetcher = GoodSite();
        var fetches = 0;
        fetcher.OnFetch = () =>
        {
            if (++fetches == 3)   // the cancel arrives while the pages are being read
                _db.Database.ExecuteSqlRaw("UPDATE HuntJobs SET Status = 'Cancelled', CancelRequested = 1 WHERE Id = {0}", job.Id);
        };
        var result = await Service(fetcher).SubmitReviewAsync(job, lead.Id, Element(GoodExtraction()), 8, CancellationToken.None);
        Assert.Equal(HuntOutcomes.Rejected, result.Outcome);
        _db.ChangeTracker.Clear();
        Assert.Equal("", (await _db.Leads.SingleAsync()).OwnerName ?? "");
        Assert.Empty(_db.LeadNotes);
        Assert.Empty(_db.HuntCandidates);
        Assert.Equal(0, (await _db.HuntJobs.SingleAsync()).Screened);
    }

    // ---- 7: the verified-only score of a review -----------------------------------------------------------------------------

    [Fact]
    public async Task A_review_shows_the_stored_score_and_the_verified_only_score_and_names_what_nobody_verified()
    {
        // a person typed a mobile number that the site does not show: it stays, and it scores, but nobody verified it
        var lead = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me, OwnerMobile = "06 99 88 77 66" };
        _db.Leads.Add(lead);
        var job = NewHunt(kind: HuntKinds.Review, target: 1, max: 1);
        job.LeadIds = new List<Guid> { lead.Id };
        _db.SaveChanges();

        var claims = Json(new { ownerName = Claim("Tine van Wel", "Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau", Site) });
        var result = await Service(GoodSite()).SubmitReviewAsync(job, lead.Id, Element(claims), null, CancellationToken.None);
        Assert.Equal(HuntOutcomes.Verified, result.Outcome);
        var cand = await _db.HuntCandidates.SingleAsync();
        Assert.True(cand.Score > cand.VerifiedScore, $"stored {cand.Score} should be above verified-only {cand.VerifiedScore}");
        Assert.Contains("verified-only score", cand.Reason);
        Assert.Contains("ownerMobile", cand.Reason);
        var note = (await _db.LeadNotes.SingleAsync()).Content;
        var title = note.Split('\n')[0];
        Assert.Contains($"stored score {cand.Score}, verified-only score {cand.VerifiedScore}", title);
        Assert.Contains("ownerMobile", title);
        Assert.Equal("06 99 88 77 66", (await _db.Leads.SingleAsync()).OwnerMobile);   // still kept
    }

    [Fact]
    public async Task A_review_without_unverified_values_has_equal_scores_and_the_plain_title()
    {
        var lead = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me };
        _db.Leads.Add(lead);
        var job = NewHunt(kind: HuntKinds.Review, target: 1, max: 1);
        job.LeadIds = new List<Guid> { lead.Id };
        _db.SaveChanges();
        await Service(GoodSite()).SubmitReviewAsync(job, lead.Id, Element(GoodExtraction()), null, CancellationToken.None);
        var cand = await _db.HuntCandidates.SingleAsync();
        Assert.Equal(cand.Score, cand.VerifiedScore);
        Assert.StartsWith("Agent review", (await _db.LeadNotes.SingleAsync()).Content);
        Assert.DoesNotContain("verified-only", (await _db.LeadNotes.SingleAsync()).Content.Split('\n')[0]);
    }

    // ---- known domains ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_known_endpoint_answers_with_the_domains_the_user_has_and_nothing_of_other_users()
    {
        _db.Leads.Add(new Lead { Name = "Known", Website = "https://www.known.example.nl", ImportedByUserId = Me });
        _db.Leads.Add(new Lead { Name = "Theirs", Website = "https://theirs.example.nl", ImportedByUserId = Other });
        var job = NewHunt();
        var session = TaskTokenFor(job);
        job.TokenExchanged = true;
        _db.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await Agent(session).Known(job.Id, new KnownDomainsRequest(new[] { "known.example.nl", "https://www.Known.example.nl/about", "new.example.nl", "theirs.example.nl", "", "not a domain" })));
        var known = JsonDocument.Parse(Text(ok.Value)).RootElement.GetProperty("known").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "known.example.nl" }, known.Distinct());
        Assert.Equal(1, known.Count);   // a duplicate in the question is one answer

        var tooMany = Enumerable.Range(0, HuntAgentController.MaxKnownPerCall + 1).Select(i => $"d{i}.example.nl").ToArray();
        Assert.IsType<BadRequestObjectResult>(await Agent(session).Known(job.Id, new KnownDomainsRequest(tooMany)));
        Assert.IsType<UnauthorizedObjectResult>(await Agent("wrong").Known(job.Id, new KnownDomainsRequest(new[] { "a.nl" })));
        Assert.IsType<OkObjectResult>(await Agent(session).Known(job.Id, new KnownDomainsRequest(Array.Empty<string>())));
    }

    [Fact]
    public async Task The_brief_no_longer_lists_the_known_domains_and_a_duplicate_submission_does_not_count_against_the_candidates()
    {
        _db.Leads.Add(new Lead { Name = "Tine", Website = "https://www.tinevanwel.nl", ImportedByUserId = Me });
        var job = NewHunt(max: 3);
        var session = TaskTokenFor(job);
        job.TokenExchanged = true;
        _db.SaveChanges();
        var brief = Text(Assert.IsType<OkObjectResult>(await Agent(session).Brief(job.Id)).Value);
        Assert.DoesNotContain("tinevanwel", brief);
        Assert.DoesNotContain("knownDomains", brief);

        var dup = Assert.IsType<OkObjectResult>(await Agent(session).Submit(job.Id, new SubmitCandidateRequest { Name = "Tine", Website = Site, Claims = Element("{}") }, default));
        Assert.Contains("\"outcome\":\"Duplicate\"", Text(dup.Value));
        Assert.Contains("\"screened\":0", Text(dup.Value));
        _db.ChangeTracker.Clear();
        Assert.Equal(0, (await _db.HuntJobs.SingleAsync()).Screened);
        Assert.Equal(1, await _db.HuntCandidates.CountAsync());   // still shown in the history
    }

    // ---- 9: the hub ------------------------------------------------------------------------------------------------------------

    private sealed class FakeCaller : HubCallerContext
    {
        private readonly ClaimsPrincipal _user;
        public FakeCaller(string? userId) => _user = new ClaimsPrincipal(userId == null ? new ClaimsIdentity() : new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class FakeGroups : IGroupManager
    {
        public List<string> Added { get; } = new();
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) { Added.Add(groupName); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private (EnrichmentHub Hub, FakeGroups Groups) Hub(string? user)
    {
        var groups = new FakeGroups();
        var hub = new EnrichmentHub(_db) { Context = new FakeCaller(user), Groups = groups };
        return (hub, groups);
    }

    [Fact]
    public async Task A_user_joins_the_feed_of_a_job_of_their_own_and_of_nobody_elses()
    {
        var mine = NewHunt(Me);
        var theirs = NewHunt(Other);
        var enrichment = new EnrichmentJob { RequestedByUserId = Me };
        var othersEnrichment = new EnrichmentJob { RequestedByUserId = Other };
        _db.EnrichmentJobs.AddRange(enrichment, othersEnrichment);
        _db.SaveChanges();

        var (hub, groups) = Hub(Me);
        await hub.JoinJob(mine.Id.ToString().ToUpperInvariant());
        await hub.JoinJob(enrichment.Id.ToString());
        Assert.Equal(new[] { $"job-{mine.Id}", $"job-{enrichment.Id}" }, groups.Added);   // the group is made from the stored id

        await Assert.ThrowsAsync<HubException>(() => hub.JoinJob(theirs.Id.ToString()));
        await Assert.ThrowsAsync<HubException>(() => hub.JoinJob(othersEnrichment.Id.ToString()));
        await Assert.ThrowsAsync<HubException>(() => hub.JoinJob(Guid.NewGuid().ToString()));          // unknown: the same answer
        await Assert.ThrowsAsync<HubException>(() => hub.JoinJob("not-a-guid"));
        await Assert.ThrowsAsync<HubException>(() => hub.JoinJob("*"));
        Assert.Equal(2, groups.Added.Count);

        var (anon, anonGroups) = Hub(null);
        await Assert.ThrowsAsync<HubException>(() => anon.JoinJob(mine.Id.ToString()));
        Assert.Empty(anonGroups.Added);
    }

    // ---- 10 and 14: quote binding, ports -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.nl:8443/", false)]
    [InlineData("https://example.nl:22", false)]
    [InlineData("http://example.nl:8080", false)]
    [InlineData("http://example.nl:3306/", false)]
    [InlineData("https://example.nl:443/x", true)]
    [InlineData("http://example.nl:80/x", true)]
    [InlineData("example.nl", true)]
    [InlineData("http://example.nl", true)]
    public void Only_the_ports_of_the_web_are_fetched(string url, bool allowed) => Assert.Equal(allowed, UrlGuard.TryNormalize(url, out _));

    [Fact]
    public void Two_quotes_overlap_when_one_holds_the_other_or_they_share_twelve_letters()
    {
        Assert.True(LeadVerifier.Overlaps("Bel mij op 06 144 70 482", "Bel mij op 06 144 70 482 of mail"));
        Assert.True(LeadVerifier.Overlaps("Bel mij op 06 144 70 482 voor een afspraak", "Bel mij op 06 144 70 482"));
        Assert.True(LeadVerifier.Overlaps("Telefoon: 06 144 70 482 (bereikbaar)", "Mobiel 06 144 70 482 bereikbaar"));
        Assert.False(LeadVerifier.Overlaps("Welkom bij onze ontwerpstudio", "Bel mij op 06 144 70 482"));
        Assert.False(LeadVerifier.Overlaps("", "x"));
    }

    private static async Task<VerificationResult> Verify(FakePageFetcher f, string claims)
    {
        var verifier = new LeadVerifier(f, new LeadEvidenceService(), NullLogger<LeadVerifier>.Instance);
        var site = await verifier.QuickLookAsync(Site, CancellationToken.None);
        await verifier.DeepenAsync(site, CancellationToken.None);
        return verifier.VerifyClaims(site, Element(claims));
    }

    [Fact]
    public async Task A_true_quote_from_another_place_on_the_page_cannot_vouch_for_a_value_that_stands_elsewhere()
    {
        var f = GoodSite();
        // every quote is really on the pages, but none of them is the passage of its fact
        var claims = Json(new
        {
            ownerName = Claim("Tine van Wel", "Bel mij op 06 144 70 482", Site + "contact"),
            ownerMobile = Claim("06 144 70 482", "In 2015 ben ik mijn eigen bedrijf gestart", Site + "over-mij"),
            personalEmail = Claim("tine@tinevanwel.nl", "In 2015 ben ik mijn eigen bedrijf gestart", Site + "over-mij"),
            foundingYear = Claim("2015", "Bel mij op 06 144 70 482", Site + "contact"),
        });
        var r = await Verify(f, claims);
        Assert.DoesNotContain("ownerName", r.Verified.Keys);
        Assert.DoesNotContain("ownerMobile", r.Verified.Keys);
        Assert.DoesNotContain("personalEmail", r.Verified.Keys);
        Assert.DoesNotContain("foundingYear", r.Verified.Keys);
        Assert.Equal(4, r.Dropped.Count);

        // the same values with their own passages verify
        var good = await Verify(GoodSite(), GoodExtraction());
        foreach (var field in new[] { "ownerName", "ownerMobile", "personalEmail", "foundingYear", "ownerLinkedInUrl", "smallCompany" })
            Assert.Contains(field, good.Verified.Keys);
    }

    [Fact]
    public async Task A_link_claim_is_bound_to_its_link_and_markup_other_than_a_href_is_not_a_place_for_a_quote()
    {
        var f = new FakePageFetcher();
        f.Add(Site, "<html><body><p>Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau.</p><a href=\"https://www.linkedin.com/in/tinevanwel/\">Volg mij</a>" +
                    "<a href=\"mailto:tine@tinevanwel.nl\">Mail Tine</a>" +
                    "<script>var contact = 'tine@tinevanwel.nl is mijn eigen adres voor alles';</script><div hidden data-x=\"zie linkedin.com/in/tinevanwel voor meer\"></div></body></html>");
        string[] Dropped(VerificationResult r) => r.Dropped.Select(d => d.Field).ToArray();

        // the quote is the text of the link, or the address in the href: accepted
        var ok = await Verify(f, Json(new
        {
            ownerName = Claim("Tine van Wel", "Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau", Site),
            ownerLinkedInUrl = Claim("https://www.linkedin.com/in/tinevanwel/", "Volg mij", Site),
            personalEmail = Claim("tine@tinevanwel.nl", "mailto:tine@tinevanwel.nl", Site),
        }));
        Assert.Contains("ownerLinkedInUrl", ok.Verified.Keys);
        Assert.Contains("personalEmail", ok.Verified.Keys);

        // a quote from a script, or from a data attribute of a hidden element: not on the page
        var bad = await Verify(f, Json(new
        {
            ownerName = Claim("Tine van Wel", "Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau", Site),
            ownerLinkedInUrl = Claim("https://www.linkedin.com/in/tinevanwel/", "zie linkedin.com/in/tinevanwel voor meer", Site),
            personalEmail = Claim("tine@tinevanwel.nl", "tine@tinevanwel.nl is mijn eigen adres voor alles", Site),
        }));
        Assert.DoesNotContain("ownerLinkedInUrl", bad.Verified.Keys);
        Assert.DoesNotContain("personalEmail", bad.Verified.Keys);
        Assert.All(new[] { "ownerLinkedInUrl", "personalEmail" }, field => Assert.Contains(field, Dropped(bad)));

        // an unrelated visible sentence does not bind a profile link
        var unrelated = await Verify(f, Json(new
        {
            ownerName = Claim("Tine van Wel", "Ik ben Tine van Wel, volledig zelfstandig met mijn eigen ontwerpbureau", Site),
            ownerLinkedInUrl = Claim("https://www.linkedin.com/in/tinevanwel/", "volledig zelfstandig met mijn eigen ontwerpbureau", Site),
        }));
        Assert.DoesNotContain("ownerLinkedInUrl", unrelated.Verified.Keys);
        Assert.Contains(unrelated.Dropped, d => d.Field == "ownerLinkedInUrl" && d.Reason.Contains("quote"));
    }

    // ---- 11: deleted leads --------------------------------------------------------------------------------------------------------

    private HuntCandidate Candidate(HuntJob job, Guid? leadId, string evidence = "{\"quote\":\"private words of a business\"}") =>
        new() { JobId = job.Id, LeadId = leadId, Name = "X", Website = "https://x.example.nl", Outcome = HuntOutcomes.Qualified, Reason = "r", EvidenceJson = evidence, Score = 8 };

    [Fact]
    public async Task Deleting_a_lead_empties_the_evidence_of_its_candidate_rows_and_cuts_the_link()
    {
        var job = NewHunt();
        var gone = new Lead { Name = "Gone", Website = "https://gone.example.nl", ImportedByUserId = Me };
        var stays = new Lead { Name = "Stays", Website = "https://stays.example.nl", ImportedByUserId = Me };
        _db.Leads.AddRange(gone, stays);
        _db.SaveChanges();
        var goneCand = Candidate(job, gone.Id);
        var staysCand = Candidate(job, stays.Id);
        _db.HuntCandidates.AddRange(goneCand, staysCand);
        _db.SaveChanges();

        var controller = new LeadsController(_db, null!, new ConfigurationBuilder().Build(), new EnrichmentChannel())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, Me) }, "Test")) } },
        };
        Assert.IsType<NoContentResult>(await controller.DeleteLead(gone.Id));
        _db.ChangeTracker.Clear();
        var a = await _db.HuntCandidates.SingleAsync(c => c.Id == goneCand.Id);
        Assert.Null(a.LeadId);
        Assert.Equal("{}", a.EvidenceJson);
        Assert.Equal(8, a.Score);   // the history of the job stays
        var b = await _db.HuntCandidates.SingleAsync(c => c.Id == staysCand.Id);
        Assert.Equal(stays.Id, b.LeadId);
        Assert.Contains("private words", b.EvidenceJson);
    }

    [Fact]
    public async Task The_database_itself_nulls_the_link_when_a_lead_goes_and_a_merge_moves_the_history()
    {
        var job = NewHunt();
        var source = new Lead { Name = "Source", Website = "https://source.example.nl", ImportedByUserId = Me };
        var target = new Lead { Name = "Target", Website = "https://target.example.nl", ImportedByUserId = Me };
        _db.Leads.AddRange(source, target);
        _db.SaveChanges();
        var cand = Candidate(job, source.Id);
        _db.HuntCandidates.Add(cand);
        _db.SaveChanges();

        await HuntEvidence.ForgetAsync(_db, new[] { source.Id }, moveTo: target.Id);
        _db.ChangeTracker.Clear();
        Assert.Equal(target.Id, (await _db.HuntCandidates.SingleAsync()).LeadId);

        // a lead removed by any other path (no ForgetAsync): the foreign key sets the link to null
        _db.Leads.Remove(await _db.Leads.SingleAsync(l => l.Id == target.Id));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        Assert.Null((await _db.HuntCandidates.SingleAsync()).LeadId);
    }

    [Fact]
    public async Task The_evidence_endpoint_has_nothing_to_show_for_a_lead_whose_evidence_was_forgotten()
    {
        var job = NewHunt();
        var lead = new Lead { Name = "Tine", Website = Site, ImportedByUserId = Me };
        _db.Leads.Add(lead);
        _db.SaveChanges();
        _db.HuntCandidates.Add(Candidate(job, lead.Id));
        _db.SaveChanges();
        Assert.Contains("\"available\":true", Text(Assert.IsType<OkObjectResult>(await Controller(Me).Evidence(lead.Id)).Value));
        await HuntEvidence.ForgetAsync(_db, new[] { lead.Id });
        _db.ChangeTracker.Clear();
        Assert.Contains("\"available\":false", Text(Assert.IsType<OkObjectResult>(await Controller(Me).Evidence(lead.Id)).Value));
    }
}

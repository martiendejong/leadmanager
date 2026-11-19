using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public class SubmitCandidateRequest
{
    public string? Name { get; set; }
    public string? Website { get; set; }
    public string? City { get; set; }
    public string? Sector { get; set; }
    public int? ClaimedScore { get; set; }
    public bool? StoppedEarly { get; set; }
    public string? StopReason { get; set; }
    public JsonElement Claims { get; set; }
}

public record CompleteHuntRequest(string? Summary);
public record FailHuntRequest(string? Reason);
public record CheckWebsitesRequest(string[]? Websites);

/// <summary>
/// The endpoints the background Jengo agent of one hunt calls. They are not reachable with a normal login or API key: the agent proves
/// itself with the token OF THIS HUNT (header X-Hunt-Agent-Token; HMAC of the hunt id with the server secret Hunt:AgentSecret). That token
/// opens this hunt only and only while it is Pending or Running, so it is useless after the hunt ends and for every other hunt, which is
/// why the task text may contain it. Whatever the agent creates belongs to the user who started the hunt, and everything it submits is
/// re-checked by the server.
/// </summary>
[ApiController]
[Route("api/agent/hunts")]
[AllowAnonymous]
public class HuntAgentController : ControllerBase
{
    public const string TokenHeader = "X-Hunt-Agent-Token";
    private const int MaxBodyChars = 60_000;
    public const int MaxCheckWebsites = 25;

    private readonly LeadManagerDbContext _db;
    private readonly HuntService _hunts;
    private readonly IConfiguration _config;

    public HuntAgentController(LeadManagerDbContext db, HuntService hunts, IConfiguration config)
    {
        _db = db; _hunts = hunts; _config = config;
    }

    /// <summary>Null when the caller holds the token of this hunt; otherwise the answer to return.</summary>
    private IActionResult? Authenticate(Guid huntId)
    {
        var secret = _config["Hunt:AgentSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(503, new { error = HuntController.TokenMissing, code = "agent_token_missing" });
        var given = Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(given)) return Unauthorized(new { error = "Missing " + TokenHeader });
        return HuntTokens.Valid(secret, huntId, given) ? null : Unauthorized(new { error = "Invalid agent token for this hunt." });
    }

    /// <summary>The hunt, only while it can still receive work.</summary>
    private async Task<(HuntJob? Job, IActionResult? Error)> ActiveHuntAsync(Guid id)
    {
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == id && j.Kind == HuntKinds.Hunt);
        if (job == null) return (null, NotFound(new { error = "Unknown hunt." }));
        if (!HuntStatuses.IsActive(job.Status))
            return (null, Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true }));
        return (job, null);
    }

    // GET /api/agent/hunts/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Brief(Guid id)
    {
        if (Authenticate(id) is { } denied) return denied;
        var (job, error) = await ActiveHuntAsync(id);
        if (error != null) return error;
        HuntService.Touch(job!);
        await _db.SaveChangesAsync();
        return Ok(new
        {
            huntId = job!.Id,
            status = job.Status,
            segment = job.Segment,
            region = job.Region,
            minScore = job.MinScore,
            targetCount = job.TargetCount,
            maxCandidates = job.MaxCandidates,
            screened = job.Screened,
            qualified = job.Qualified,
            bestScoreSoFar = job.BestScore,
            stop = HuntService.ShouldStop(job),
        });
    }

    // POST /api/agent/hunts/{id}/check
    /// <summary>Which of these websites are already leads of the user? Only a yes or no per site: the list of the user's leads is never handed out.</summary>
    [HttpPost("{id:guid}/check")]
    public async Task<IActionResult> Check(Guid id, [FromBody] CheckWebsitesRequest request)
    {
        if (Authenticate(id) is { } denied) return denied;
        var (job, error) = await ActiveHuntAsync(id);
        if (error != null) return error;
        var sites = (request.Websites ?? Array.Empty<string>()).Take(MaxCheckWebsites + 1).ToArray();
        if (sites.Length == 0 || sites.Length > MaxCheckWebsites) return BadRequest(new { error = $"Send between 1 and {MaxCheckWebsites} websites." });
        HuntService.Touch(job!);
        await _db.SaveChangesAsync();
        var known = await _hunts.KnownDomainsAsync(job!.UserId, int.MaxValue);
        return Ok(new
        {
            results = sites.Select(w =>
            {
                var domain = UrlGuard.DomainOf(w);
                return new { website = w.Length > 200 ? w[..200] : w, valid = domain != "", known = domain != "" && known.Contains(domain, StringComparer.OrdinalIgnoreCase) };
            }),
        });
    }

    // POST /api/agent/hunts/{id}/candidates
    [HttpPost("{id:guid}/candidates")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitCandidateRequest request, CancellationToken ct)
    {
        if (Authenticate(id) is { } denied) return denied;
        var (job, error) = await ActiveHuntAsync(id);
        if (error != null) return error;
        if (request.Claims.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "claims must be a JSON object." });
        if (request.Claims.GetRawText().Length > MaxBodyChars) return BadRequest(new { error = "claims is too large." });

        // One candidate of a hunt at a time: the counters and the stop conditions are not safe against two submissions at once.
        using var gate = HuntLocks.TryEnterJob(id);
        if (gate == null) return StatusCode(429, new { error = "Another candidate of this hunt is being checked. Submit one at a time.", code = "one_at_a_time" });

        // The job may have changed while we waited for the lock.
        await _db.Entry(job!).ReloadAsync(CancellationToken.None);
        if (!HuntStatuses.IsActive(job!.Status)) return Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true });

        var reason = request.StoppedEarly == true ? (request.StopReason ?? "stopped early") : null;
        var result = await _hunts.SubmitCandidateAsync(job, new HuntService.Submission(
            request.Name ?? "", request.Website ?? "", request.City, request.Sector, request.Claims, request.ClaimedScore, reason), ct);
        await _db.SaveChangesAsync(CancellationToken.None);
        return Ok(new
        {
            outcome = result.Outcome,
            score = result.Score,
            reason = result.Reason,
            leadId = result.LeadId,
            duplicate = result.Duplicate,
            bestScoreSoFar = result.BestScoreSoFar,
            qualified = result.Qualified,
            screened = result.Screened,
            stop = result.Stop,
        });
    }

    // POST /api/agent/hunts/{id}/complete
    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteHuntRequest request)
    {
        if (Authenticate(id) is { } denied) return denied;
        var (job, error) = await ActiveHuntAsync(id);
        if (error != null) return error;
        await _hunts.CompleteAsync(job!, request.Summary, failed: false);
        return Ok(new { status = job!.Status, qualified = job.Qualified, screened = job.Screened, best = job.BestScore });
    }

    // POST /api/agent/hunts/{id}/fail
    [HttpPost("{id:guid}/fail")]
    public async Task<IActionResult> Fail(Guid id, [FromBody] FailHuntRequest request)
    {
        if (Authenticate(id) is { } denied) return denied;
        var (job, error) = await ActiveHuntAsync(id);
        if (error != null) return error;
        await _hunts.CompleteAsync(job!, request.Reason, failed: true);
        return Ok(new { status = job!.Status });
    }
}

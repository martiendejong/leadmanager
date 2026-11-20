using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    /// <summary>Review jobs only: the lead this submission is about (one of the leads of the review).</summary>
    public Guid? LeadId { get; set; }
    public JsonElement Claims { get; set; }
}

public record CompleteHuntRequest(string? Summary, bool? Failed = null);
public record HuntProgressRequest(string? Current);

/// <summary>
/// The endpoints the background Jengo agent of one hunt calls. They are not reachable with a user login or an API key: the agent proves itself
/// with the token of THIS hunt (header X-Hunt-Token; random, stored only as a hash, valid three hours, revoked when the hunt ends). That token
/// opens this hunt only, so a copy of the task text is useless afterwards and for every other hunt. Whatever the agent creates belongs to the
/// user who started the hunt, and everything it submits is re-checked by the server. A wrong, expired, revoked or foreign token is always the
/// same 401, whether or not the hunt exists.
/// </summary>
[ApiController]
[Route("api/hunt-agent")]
[AllowAnonymous]
[EnableRateLimiting(HuntRateLimit.Policy)]
public class HuntAgentController : ControllerBase
{
    public const string TokenHeader = "X-Hunt-Token";
    private const int MaxBodyChars = 60_000;
    public const int MaxKnownDomains = 2000;

    private readonly LeadManagerDbContext _db;
    private readonly HuntService _hunts;

    public HuntAgentController(LeadManagerDbContext db, HuntService hunts)
    {
        _db = db; _hunts = hunts;
    }

    /// <summary>The hunt when the caller holds its live token and it can still receive work; otherwise the answer to return.</summary>
    private async Task<(HuntJob? Job, IActionResult? Error)> AuthenticateAsync(Guid id)
    {
        var given = Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(given)) return (null, Unauthorized(new { error = "Missing " + TokenHeader }));
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == id && (j.Kind == HuntKinds.Hunt || j.Kind == HuntKinds.Review));
        if (job == null || !HuntTokens.Valid(job, given)) return (null, Unauthorized(new { error = "Invalid, expired or revoked agent token for this hunt." }));
        if (!HuntStatuses.IsActive(job.Status))
            return (null, Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true }));
        return (job, null);
    }

    // GET /api/hunt-agent/{id}/brief
    [HttpGet("{id:guid}/brief")]
    public async Task<IActionResult> Brief(Guid id)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        HuntService.Touch(job!);
        await _db.SaveChangesAsync();

        object? leads = null;
        if (job!.Kind == HuntKinds.Review)
        {
            var ids = job.LeadIds;
            var rows = await _db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id) && l.ImportedByUserId == job.UserId)
                .Select(l => new { l.Id, l.Name, l.Website, l.SalesPriorityScore }).ToListAsync();
            var done = await _db.HuntCandidates.AsNoTracking().Where(c => c.JobId == job.Id && c.LeadId != null).Select(c => c.LeadId!.Value).ToListAsync();
            leads = rows.Select(l => new { leadId = l.Id, name = l.Name, website = l.Website, currentScore = l.SalesPriorityScore, reviewed = done.Contains(l.Id) }).ToList();
        }
        return Ok(new
        {
            huntId = job.Id,
            mode = job.Kind == HuntKinds.Review ? "review" : "hunt",
            status = job.Status,
            segment = job.Segment,
            region = job.Region,
            minScore = job.MinScore,
            targetCount = job.TargetCount,
            maxCandidates = job.MaxCandidates,
            ratchet = job.Ratchet,
            screened = job.Screened,
            qualified = job.Qualified,
            bestScoreSoFar = job.BestScore,
            remaining = new { candidates = Math.Max(0, job.MaxCandidates - job.Screened), leadsToFind = Math.Max(0, job.TargetCount - job.Qualified) },
            tokenExpiresAt = job.TokenExpiresAt,
            rules = new[]
            {
                "Public information only: what the business publishes itself.",
                "Never guess a phone number, e-mail address or LinkedIn URL; leave the claim out.",
                "Every score-bearing claim needs source_url and a verbatim quote from the visible text of that page. The server checks both and drops what it cannot verify.",
                "The score is computed by the server from verified facts; a claimed score is only recorded next to it.",
                "Stop when stop is true. Skip the companies in knownDomains.",
            },
            knownDomains = job.Kind == HuntKinds.Hunt ? await _hunts.KnownDomainsAsync(job.UserId, MaxKnownDomains) : new List<string>(),
            leads,
            stop = HuntService.ShouldStop(job),
        });
    }

    // POST /api/hunt-agent/{id}/progress
    [HttpPost("{id:guid}/progress")]
    public async Task<IActionResult> Progress(Guid id, [FromBody] HuntProgressRequest request)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        await _hunts.ProgressAsync(job!, request.Current);
        return Ok(new { stop = HuntService.ShouldStop(job!), screened = job!.Screened, qualified = job.Qualified, bestScoreSoFar = job.BestScore });
    }

    // POST /api/hunt-agent/{id}/candidates
    [HttpPost("{id:guid}/candidates")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitCandidateRequest request, CancellationToken ct)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        if (request.Claims.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "claims must be a JSON object." });
        if (request.Claims.GetRawText().Length > MaxBodyChars) return BadRequest(new { error = "claims is too large." });
        if (job!.Kind == HuntKinds.Review && request.LeadId == null) return BadRequest(new { error = "leadId is required in a review." });

        // One candidate of a hunt at a time: the counters and the stop conditions are not safe against two submissions at once.
        using var gate = HuntLocks.TryEnterJob(id);
        if (gate == null) return StatusCode(429, new { error = "Another candidate of this hunt is being checked. Submit one at a time.", code = "one_at_a_time" });

        // The job may have changed (ended, revoked) while we waited for the lock.
        await _db.Entry(job).ReloadAsync(CancellationToken.None);
        if (!HuntStatuses.IsActive(job.Status) || job.TokenHash == null) return Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true });

        HuntService.SubmissionResult result;
        if (job.Kind == HuntKinds.Review)
            result = await _hunts.SubmitReviewAsync(job, request.LeadId!.Value, request.Claims, request.ClaimedScore, ct);
        else
        {
            var reason = request.StoppedEarly == true ? (request.StopReason ?? "stopped early") : null;
            result = await _hunts.SubmitCandidateAsync(job, new HuntService.Submission(
                request.Name ?? "", request.Website ?? "", request.City, request.Sector, request.Claims, request.ClaimedScore, reason), ct);
        }
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

    // POST /api/hunt-agent/{id}/complete   (Failed = true when the agent cannot work at all)
    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteHuntRequest request)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        await _hunts.CompleteAsync(job!, request.Summary, failed: request.Failed == true);
        return Ok(new { status = job!.Status, qualified = job.Qualified, screened = job.Screened, best = job.BestScore });
    }
}

/// <summary>Rate limit of the agent endpoints: per hunt (or per address when the id is unreadable), generous for a working agent, hard for a guesser.</summary>
public static class HuntRateLimit
{
    public const string Policy = "hunt-agent";
    public const int PerMinute = 60;
}

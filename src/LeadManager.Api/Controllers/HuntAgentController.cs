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
public record KnownDomainsRequest(string[]? Domains);

/// <summary>
/// The endpoints the background Jengo agent of one hunt calls. They are not reachable with a user login or an API key: the agent proves itself
/// with the token of THIS hunt (header X-Hunt-Token; random, stored only as a hash, valid three hours, revoked when the hunt ends). That token
/// opens this hunt only, so a copy of the task text is useless afterwards and for every other hunt. Whatever the agent creates belongs to the
/// user who started the hunt, and everything it submits is re-checked by the server. A wrong, expired, revoked or foreign token is always the
/// same 401, whether or not the hunt exists.
///
/// Two tokens per hunt. The TASK token stands in the text of the JengoWork task, where more people than the agent can read it; it is good for one
/// hour and for exactly one call: /brief, which exchanges it for a SESSION token (returned in the answer, never repeated) and revokes the task
/// token. The session token is what every other call needs; it lives 30 minutes after the last call and never longer than three hours.
/// After the exchange a copy of the task text opens nothing.
/// </summary>
[ApiController]
[Route("api/hunt-agent")]
[AllowAnonymous]
[EnableRateLimiting(HuntRateLimit.Policy)]
public class HuntAgentController : ControllerBase
{
    public const string TokenHeader = "X-Hunt-Token";
    private const int MaxBodyChars = 60_000;
    public const int MaxKnownPerCall = 20;

    private readonly LeadManagerDbContext _db;
    private readonly HuntService _hunts;

    public HuntAgentController(LeadManagerDbContext db, HuntService hunts)
    {
        _db = db; _hunts = hunts;
    }

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// The hunt when the caller holds its live token and it can still receive work; otherwise the answer to return. Wrong tokens are counted per
    /// address and per hunt (<see cref="HuntAuthThrottle"/>): past the limit the answer is 429 before the token is even looked at.
    /// <paramref name="allowTaskToken"/> is true for /brief only: every other call needs the session token.
    /// </summary>
    private async Task<(HuntJob? Job, IActionResult? Error)> AuthenticateAsync(Guid id, bool allowTaskToken = false)
    {
        if (HuntAuthThrottle.Blocked(Address, id))
            return (null, StatusCode(429, new { error = "Too many wrong tokens. Wait ten minutes.", code = "auth_throttled" }));
        var given = Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(given)) { HuntAuthThrottle.Fail(Address, id); return (null, Unauthorized(new { error = "Missing " + TokenHeader })); }
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == id && (j.Kind == HuntKinds.Hunt || j.Kind == HuntKinds.Review));
        if (job == null || !HuntTokens.Valid(job, given))
        {
            HuntAuthThrottle.Fail(Address, id);
            return (null, Unauthorized(new { error = "Invalid, expired or revoked agent token for this hunt." }));
        }
        if (!job.TokenExchanged && !allowTaskToken)
            return (null, Unauthorized(new { error = "This is the task token. Call GET /brief first: it returns the session token that every other call needs." }));
        if (!HuntStatuses.IsActive(job.Status))
            return (null, Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true }));
        return (job, null);
    }

    // GET /api/hunt-agent/{id}/brief
    [HttpGet("{id:guid}/brief")]
    public async Task<IActionResult> Brief(Guid id)
    {
        var (job, error) = await AuthenticateAsync(id, allowTaskToken: true);
        if (error != null) return error;
        Response.Headers.CacheControl = "no-store";   // the answer may carry the session token
        await _hunts.TouchAsync(job!);
        if (!HuntStatuses.IsActive(job!.Status)) return Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true });

        // The first call exchanges the task token for a session token, once: only the call that still finds the task token in place gets the new one.
        string? sessionToken = null;
        if (!job.TokenExchanged)
        {
            var session = HuntTokens.NewSession(job);
            var old = job.TokenHash;
            var rows = await _db.HuntJobs.Where(j => j.Id == id && j.TokenHash == old && !j.TokenExchanged && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.TokenHash, session.Hash).SetProperty(j => j.TokenExchanged, true).SetProperty(j => j.TokenExpiresAt, (DateTime?)session.ExpiresAt));
            if (rows != 1) return Unauthorized(new { error = "Invalid, expired or revoked agent token for this hunt." });
            sessionToken = session.Token;
            await _db.Entry(job).ReloadAsync();
        }

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
            // Present once: in the answer to the call that exchanged the task token. Keep it for the rest of the hunt and never print it.
            session = sessionToken == null ? null : new { token = sessionToken, header = TokenHeader, expiresAt = job.TokenExpiresAt, idleMinutes = (int)HuntTokens.SessionIdle.TotalMinutes },
            rules = new[]
            {
                "Public information only: what the business publishes itself.",
                "Never guess a phone number, e-mail address or LinkedIn URL; leave the claim out.",
                "Every score-bearing claim needs source_url and a verbatim quote from the visible text of that page. The server checks both and drops what it cannot verify.",
                "The score is computed by the server from verified facts; a claimed score is only recorded next to it.",
                "Stop when stop is true. Before you research a company, ask POST /known with its domain: a company the user already has is skipped.",
            },
            known = job.Kind == HuntKinds.Hunt ? new { endpoint = $"/api/hunt-agent/{job.Id}/known", maxDomainsPerCall = MaxKnownPerCall } : null,
            leads,
            stop = HuntService.ShouldStop(job),
        });
    }

    // POST /api/hunt-agent/{id}/known   {"domains": ["example.nl", ...]}  (at most 20): answers with the ones the user already has
    [HttpPost("{id:guid}/known")]
    [RequestSizeLimit(8_192)]
    public async Task<IActionResult> Known(Guid id, [FromBody] KnownDomainsRequest request)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        var asked = request.Domains ?? Array.Empty<string>();
        if (asked.Length > MaxKnownPerCall) return BadRequest(new { error = $"At most {MaxKnownPerCall} domains per call." });
        await _hunts.TouchAsync(job!);
        if (!HuntStatuses.IsActive(job!.Status)) return Conflict(new { error = $"This hunt is {job.Status.ToLowerInvariant()}.", stop = true });
        if (job.Kind != HuntKinds.Hunt) return Ok(new { known = Array.Empty<string>() });
        var wanted = asked.Where(d => d != null && d.Length <= 200).Select(UrlGuard.DomainOf).Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(new { known = await _hunts.KnownAmongAsync(job.UserId, wanted) });
    }

    // POST /api/hunt-agent/{id}/progress
    [HttpPost("{id:guid}/progress")]
    [RequestSizeLimit(4_096)]
    public async Task<IActionResult> Progress(Guid id, [FromBody] HuntProgressRequest request)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        await _hunts.ProgressAsync(job!, request.Current);
        return Ok(new { stop = HuntService.ShouldStop(job!), screened = job!.Screened, qualified = job.Qualified, bestScoreSoFar = job.BestScore });
    }

    // POST /api/hunt-agent/{id}/candidates
    [HttpPost("{id:guid}/candidates")]
    [RequestSizeLimit(200_000)]   // the claims are checked to 60 000 characters, but only after the body was read: this caps the body itself
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
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteHuntRequest request)
    {
        var (job, error) = await AuthenticateAsync(id);
        if (error != null) return error;
        await _hunts.CompleteAsync(job!, request.Summary, failed: request.Failed == true);
        return Ok(new { status = job!.Status, qualified = job.Qualified, screened = job.Screened, best = job.BestScore });
    }
}

/// <summary>
/// Rate limits of the agent endpoints, two of them: per hunt and address (the named policy, generous for a working agent, hard for a guesser) and
/// per address over all hunts (the global limiter, which only looks at /api/hunt-agent paths). Wrong tokens are charged harder on top of that
/// (<see cref="HuntAuthThrottle"/>).
/// </summary>
public static class HuntRateLimit
{
    public const string Policy = "hunt-agent";
    public const int PerMinute = 60;
    public const int PerAddressPerMinute = 300;
    public const string PathPrefix = "/api/hunt-agent";

    /// <summary>The partition of the per-hunt policy: the hunt id (or the address when it is unreadable) and the address.</summary>
    public static string PolicyKey(Microsoft.AspNetCore.Http.HttpContext ctx) =>
        (ctx.Request.RouteValues["id"]?.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") + "|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "");

    /// <summary>The partition of the global limiter: one counter per address for the agent paths, none for everything else.</summary>
    public static System.Threading.RateLimiting.RateLimitPartition<string> GlobalPartition(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase))
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("other");
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("hunt-ip|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = PerAddressPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }
}

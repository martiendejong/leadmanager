using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record StartHuntRequest(string? Segment, string? Region, int? MinScore, int? TargetCount, int? MaxCandidates, bool? Ratchet, Guid? OrganizationId = null);
public record VerifyLeadsRequest(Guid[]? Ids, bool? Confirm = null);
public record ReviewLeadsRequest(Guid[]? Ids);

/// <summary>
/// Lead hunt and verify. A hunt is researched by a background Jengo agent that the app starts (a JengoWork task); every candidate the
/// agent submits is re-checked by the server. A verify rescored existing leads from their own website by rules only: no key needed.
/// An API key may only READ here (status, evidence, catalog); starting, cancelling and verifying need a person's login.
/// </summary>
[ApiController]
[Route("api/leads")]
[Authorize]
public class HuntController : ControllerBase
{
    public const int MaxVerifyIds = 50;
    public const int MaxReviewIds = 5;
    public const string NotConfigured = DispatchNotConfiguredException.Text;   // "Agent hunting is not configured on this server"
    /// <summary>What a person is told when the task could not be created: no detail of the other system (the detail goes to the log).</summary>
    public const string DispatchFailedText = "The Jengo agent could not be started right now. Try again in a few minutes.";

    private readonly LeadManagerDbContext _db;
    private readonly IHuntDispatcher _dispatcher;
    private readonly GooglePlacesEnrichmentService _places;
    private readonly HuntQueue _queue;
    private readonly HuntRegistry _registry;
    private readonly IConfiguration _config;
    private readonly HuntService? _service;
    private readonly ILogger<HuntController>? _logger;

    public HuntController(LeadManagerDbContext db, IHuntDispatcher dispatcher, GooglePlacesEnrichmentService places, HuntQueue queue, HuntRegistry registry, IConfiguration config,
        HuntService? service = null, ILogger<HuntController>? logger = null)
    {
        _db = db; _dispatcher = dispatcher; _places = places; _queue = queue; _registry = registry; _config = config; _service = service; _logger = logger;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    /// <summary>The JengoWork connection and the public address the agent calls back on are both set.</summary>
    private bool Ready => _dispatcher.IsConfigured && HuntAccess.ApiBase(_config) != "";
    private int MaxPerDay => Math.Clamp(int.TryParse(_config["Hunt:MaxPerUserPerDay"], out var n) ? n : 5, 1, 50);

    // GET /api/leads/products : the catalog of our products that a lead's product fit may name (one file: Agent/PRODUCTS.md)
    [AllowApiKey]
    [HttpGet("products")]
    public IActionResult Products() => Ok(ProductCatalog.All.Select(p => new { key = p.Key, name = p.Name, whatItIs = p.WhatItIs, fitsWhen = p.FitsWhen }));

    // GET /api/leads/hunt/capabilities
    [AllowApiKey]
    [HttpGet("hunt/capabilities")]
    public async Task<IActionResult> Capabilities()
    {
        return Ok(new
        {
            huntAvailable = Ready,
            canStartHunt = Ready && await HuntAccess.IsAllowedAsync(_db, _config, UserId),
            dispatchConfigured = Ready,
            // false while a person has to start the JengoWork task (board not monitored by jengo-agi): the screen says so
            agentStartsAutomatically = Ready && _config.GetValue<bool>("Hunt:Dispatch:AgentPicksUp"),
            startWithinMinutes = (int)HuntSweepService.PendingGrace.TotalMinutes,
            silenceMinutes = (int)HuntSweepService.Silence.TotalMinutes,
            googlePlacesConfigured = _places.IsConfigured,
            maxPerDay = MaxPerDay,
            message = Ready ? null : NotConfigured,
        });
    }

    // POST /api/leads/hunt
    [DenyApiKey]
    [HttpPost("hunt")]
    [RequestSizeLimit(8_192)]
    public async Task<IActionResult> Start([FromBody] StartHuntRequest request, CancellationToken ct)
    {
        if (!Ready) return StatusCode(503, new { error = NotConfigured, code = "dispatch_not_configured" });
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!await HuntAccess.IsAllowedAsync(_db, _config, userId))
            return StatusCode(403, new { error = "You are not allowed to start a lead hunt. Ask an administrator.", code = "hunt_not_allowed" });

        // Free text that ends up in the agent's prompt: no line breaks, markup or instruction-like phrases. Refused, not silently repaired.
        if (TextSafety.Suspicious(request.Segment) || TextSafety.Suspicious(request.Region))
            return BadRequest(new { error = "The segment or region contains line breaks, markup characters or phrases that are not allowed. Describe the segment in plain words." });
        var segment = HuntPrompt.Sanitize(request.Segment, 121);
        var region = HuntPrompt.Sanitize(request.Region, 81);
        if (segment.Length < 3) return BadRequest(new { error = "Describe the segment to search (at least 3 characters)." });
        if (segment.Length > 120 || region.Length > 80) return BadRequest(new { error = "Segment is at most 120 characters, region at most 80." });
        var min = request.MinScore ?? 8;
        var target = request.TargetCount ?? 3;
        var maxCandidates = request.MaxCandidates ?? 30;
        if (min is < 4 or > 9) return BadRequest(new { error = "The minimum score is between 4 and 9 (the score cannot reach 10)." });
        if (target is < 1 or > 10) return BadRequest(new { error = "The number of leads to find is between 1 and 10." });
        if (maxCandidates is < 5 or > 40) return BadRequest(new { error = "Candidates to screen is between 5 and 40." });

        var (orgId, orgError) = await Organizations.ResolveTargetAsync(_db, userId, request.OrganizationId);
        if (orgId == null) return BadRequest(new { error = orgError });

        var job = new HuntJob
        {
            UserId = userId, OrganizationId = orgId.Value, Kind = HuntKinds.Hunt, Segment = segment, Region = region, MinScore = min, TargetCount = target,
            MaxCandidates = maxCandidates, Ratchet = request.Ratchet ?? true, Total = maxCandidates,
        };
        return await CreateAndDispatchAsync(job, HuntKinds.Hunt, MaxPerDay, ct);
    }

    /// <summary>Creates the job with a fresh token, then the JengoWork task whose text carries that token. A failed dispatch ends the job and revokes the token.</summary>
    private async Task<IActionResult> CreateAndDispatchAsync(HuntJob job, string kind, int perDay, CancellationToken ct)
    {
        var token = HuntTokens.Issue(job);
        var refused = await CreateJobAsync(job, kind, perDay, ct);
        if (refused != null) return refused;

        try
        {
            DispatchResult result;
            try { result = await _dispatcher.DispatchAsync(job, HuntPrompt.Render(job, HuntAccess.ApiBase(_config), token), ct); }
            catch (DispatchUncertainException uncertain)
            {
                // A timeout or a dropped connection can come after JengoWork created the task: look for it before giving up, so the job
                // does not fail while its task exists and nobody can reach it (and the person does not start a second one).
                _logger?.LogWarning(uncertain, "Dispatch of {Kind} job {JobId} is uncertain; looking for the task on the board", kind, job.Id);
                result = await FindTaskAsync(job) ?? throw uncertain;
            }
            job.TaskId = result.TaskId;
            job.TaskUrl = result.TaskUrl;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The person gets one plain sentence; what went wrong (and where) is for the log. The task token is revoked, so a task that may
            // still exist on the board is inert: its token opens nothing.
            _logger?.LogWarning(ex, "Dispatch of {Kind} job {JobId} failed", kind, job.Id);
            job.Status = HuntStatuses.Failed;
            job.Error = ex is DispatchNotConfiguredException ? NotConfigured : DispatchFailedText;
            job.CompletedAt = DateTime.UtcNow;
            HuntTokens.Revoke(job);
            await _db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(502, new { error = job.Error, jobId = job.Id });
        }
        return Accepted(new { jobId = job.Id, taskUrl = job.TaskUrl });
    }

    private async Task<DispatchResult?> FindTaskAsync(HuntJob job)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { return await _dispatcher.FindAsync(job, limit.Token); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Looking for the task of job {JobId} failed", job.Id); return null; }
    }

    // POST /api/leads/hunt/review : ask a Jengo agent to research existing leads (the server verifies what it submits)
    [DenyApiKey]
    [HttpPost("hunt/review")]
    [RequestSizeLimit(8_192)]
    public async Task<IActionResult> ReviewByAgent([FromBody] ReviewLeadsRequest request, CancellationToken ct)
    {
        if (!Ready) return StatusCode(503, new { error = NotConfigured, code = "dispatch_not_configured" });
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!await HuntAccess.IsAllowedAsync(_db, _config, userId))
            return StatusCode(403, new { error = "You are not allowed to start a lead hunt. Ask an administrator.", code = "hunt_not_allowed" });
        var ids = (request.Ids ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { error = "No lead IDs provided." });
        if (ids.Length > MaxReviewIds) return BadRequest(new { error = $"Ask for a review of at most {MaxReviewIds} leads at a time." });
        var ownRows = await _db.Leads.VisibleTo(_db, userId).Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.OrganizationId }).ToListAsync(ct);
        var own = ownRows.Select(l => l.Id).ToList();
        if (own.Count == 0) return NotFound(new { error = "None of these leads belong to your account." });
        if (ownRows.Select(l => l.OrganizationId).Distinct().Count() > 1)
            return BadRequest(new { error = "Select leads of one organisation at a time." });

        var job = new HuntJob
        {
            UserId = userId, OrganizationId = ownRows[0].OrganizationId, Kind = HuntKinds.Review, LeadIds = own, Segment = $"Review of {own.Count} lead(s)", Region = "", MinScore = 0,
            TargetCount = own.Count, MaxCandidates = own.Count, Total = own.Count, Ratchet = false,
        };
        return await CreateAndDispatchAsync(job, HuntKinds.Review, MaxPerDay, ct);
    }

    /// <summary>
    /// Creates a job when the user has no active job and is under the daily cap, atomically: a per-user lock serialises the check and the
    /// insert, and a unique index in the database refuses a second active job even if the lock were bypassed. Null means it was created.
    /// </summary>
    private async Task<IActionResult?> CreateJobAsync(HuntJob job, string kind, int perDay, CancellationToken ct)
    {
        using var gate = await HuntLocks.EnterUserAsync(job.UserId!, TimeSpan.FromSeconds(5));
        if (gate == null) return Conflict(new { error = "Another request of yours is being handled. Try again in a moment." });

        if (await _db.HuntJobs.AnyAsync(j => j.UserId == job.UserId && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running), ct))
            return Conflict(new { error = "A job is already running for you. Wait for it to finish or cancel it." });
        var since = DateTime.UtcNow.Date;
        // A hunt or review for which no task could be created never ran: it does not use up the person's day. (A verify has no task by design.)
        if (await _db.HuntJobs.CountAsync(j => j.UserId == job.UserId && j.Kind == kind && j.CreatedAt >= since && (j.Kind == HuntKinds.Verify || j.TaskId != null), ct) >= perDay)
            return StatusCode(429, new { error = kind == HuntKinds.Hunt ? $"You can start {perDay} lead hunts per day. Try again tomorrow."
                : kind == HuntKinds.Review ? $"You can start {perDay} agent reviews per day. Try again tomorrow." : $"You can start {perDay} verify runs per day. Try again tomorrow.", code = "daily_cap" });

        _db.HuntJobs.Add(job);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            _db.Entry(job).State = EntityState.Detached;
            return Conflict(new { error = "A job is already running for you. Wait for it to finish or cancel it." });
        }
        return null;
    }

    // POST /api/leads/verify   and   POST /api/leads/{id}/verify[?dryRun=true]
    [DenyApiKey]
    [HttpPost("verify")]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> VerifyMany([FromBody] VerifyLeadsRequest request, CancellationToken ct = default)
    {
        var ids = (request.Ids ?? Array.Empty<Guid>()).Distinct().ToArray();
        // A bulk run (more than one lead) is for people who may run the hunt tooling; a single lead can be verified by its owner.
        if (ids.Length > 1 && !await HuntAccess.IsAllowedAsync(_db, _config, UserId))
            return StatusCode(403, new { error = "Verifying several leads at once is for administrators and the users who may start lead hunts.", code = "hunt_not_allowed" });
        return await StartVerify(ids, request.Confirm == true, ct);
    }

    [DenyApiKey]
    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> VerifyOne(Guid id, [FromQuery] bool dryRun = false, [FromQuery] bool confirm = false, CancellationToken ct = default)
    {
        if (!dryRun) return await StartVerify(new[] { id }, confirm, ct);
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (_service == null) return StatusCode(503, new { error = "Verify is not available here." });
        var lead = await _db.Leads.VisibleTo(_db, userId).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lead == null) return NotFound(new { error = "This lead does not belong to your account." });
        return Ok(await _service.PreviewAsync(lead, confirm, ct));
    }

    private int MaxVerifyPerDay => Math.Clamp(int.TryParse(_config["Hunt:MaxVerifyPerUserPerDay"], out var n) ? n : 20, 1, 200);

    private async Task<IActionResult> StartVerify(Guid[] ids, bool confirm, CancellationToken ct)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        ids = ids.Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { error = "No lead IDs provided." });
        if (ids.Length > MaxVerifyIds) return BadRequest(new { error = $"At most {MaxVerifyIds} leads per request." });

        var ownRows = await _db.Leads.VisibleTo(_db, userId).Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.OrganizationId }).ToListAsync(ct);
        var own = ownRows.Select(l => l.Id).ToList();
        if (own.Count == 0) return NotFound(new { error = "None of these leads belong to your account." });
        if (ownRows.Select(l => l.OrganizationId).Distinct().Count() > 1)
            return BadRequest(new { error = "Select leads of one organisation at a time." });

        var job = new HuntJob { UserId = userId, OrganizationId = ownRows[0].OrganizationId, Kind = HuntKinds.Verify, LeadIds = own, Total = own.Count, ConfirmClear = confirm };
        var refused = await CreateJobAsync(job, HuntKinds.Verify, MaxVerifyPerDay, ct);
        if (refused != null) return refused;
        await _queue.Writer.WriteAsync(job.Id, ct);
        return Accepted(new { jobId = job.Id, leads = own.Count, skipped = ids.Length - own.Count });
    }

    // GET /api/leads/hunt
    [AllowApiKey]
    [HttpGet("hunt")]
    public async Task<IActionResult> Recent([FromQuery] string? organizationId = null)
    {
        var userId = UserId;
        // The hunts of the organisations the person is a member of (their colleagues' hunts included).
        var orgs = await Organizations.ParseFilterAsync(_db, userId, organizationId);
        var query = _db.HuntJobs.AsNoTracking().Where(j => _db.OrganizationMembers.Any(m => m.UserId == userId && m.OrganizationId == j.OrganizationId));
        if (orgs != null) query = query.Where(j => orgs.Contains(j.OrganizationId));
        var jobs = await query.OrderByDescending(j => j.CreatedAt).Take(10).ToListAsync();
        return Ok(jobs.Select(ToDto));
    }

    // GET /api/leads/hunt/{id}
    [AllowApiKey]
    [HttpGet("hunt/{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var userId = UserId;
        var job = await _db.HuntJobs.AsNoTracking().Include(j => j.Candidates).FirstOrDefaultAsync(j => j.Id == id && _db.OrganizationMembers.Any(m => m.UserId == userId && m.OrganizationId == j.OrganizationId));
        if (job == null) return NotFound();
        return Ok(new
        {
            job = ToDto(job),
            candidates = job.Candidates.OrderBy(c => c.CreatedAt).Select(c => new
            {
                id = c.Id, leadId = c.LeadId, name = c.Name, website = c.Website, score = c.Score, verifiedScore = c.VerifiedScore, maxReachable = c.MaxReachable,
                outcome = c.Outcome, reason = c.Reason, at = c.CreatedAt,
            }),
        });
    }

    // POST /api/leads/hunt/{id}/cancel
    [DenyApiKey]
    [HttpPost("hunt/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = UserId;
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == id && _db.OrganizationMembers.Any(m => m.UserId == userId && m.OrganizationId == j.OrganizationId));
        if (job == null) return NotFound();
        if (!HuntStatuses.IsActive(job.Status)) return Ok(ToDto(job));
        // One conditional update on the active statuses, like the agent paths: a hunt that completed while this request was being handled is left as it is.
        // An agent hunt or review ends now and its token is revoked: the agent's next call is refused and it stops. A verify job ends at its next lead.
        var endsNow = job.Status == HuntStatuses.Pending || job.Kind != HuntKinds.Verify;
        var active = _db.HuntJobs.Where(j => j.Id == id && _db.OrganizationMembers.Any(m => m.UserId == userId && m.OrganizationId == j.OrganizationId) && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running));
        if (endsNow)
            await active.ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true).SetProperty(j => j.Status, HuntStatuses.Cancelled)
                .SetProperty(j => j.CompletedAt, (DateTime?)DateTime.UtcNow).SetProperty(j => j.TokenHash, (string?)null).SetProperty(j => j.TokenExpiresAt, (DateTime?)null)
                .SetProperty(j => j.TokenExchanged, false).SetProperty(j => j.TaskTokenHash, (string?)null).SetProperty(j => j.TokenExchangedAt, (DateTime?)null));
        else
            await active.ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true));
        await _db.Entry(job).ReloadAsync();
        _registry.Cancel(job.Id);
        if (!HuntStatuses.IsActive(job.Status)) HuntLocks.Forget(job.Id);
        return Ok(ToDto(job));
    }

    // GET /api/leads/{id}/evidence : the sources and quotes behind the latest verification of a lead
    [AllowApiKey]
    [HttpGet("{id:guid}/evidence")]
    public async Task<IActionResult> Evidence(Guid id)
    {
        var userId = UserId;
        var owns = await _db.Leads.VisibleTo(_db, userId).AsNoTracking().AnyAsync(l => l.Id == id);
        if (!owns) return NotFound();
        var cand = await _db.HuntCandidates.AsNoTracking().Where(c => c.LeadId == id).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync();
        if (cand == null) return Ok(new { available = false });
        JsonElement evidence;
        try { evidence = JsonDocument.Parse(cand.EvidenceJson).RootElement.Clone(); }
        catch (JsonException) { return Ok(new { available = false }); }
        return Ok(new { available = true, outcome = cand.Outcome, score = cand.Score, reason = cand.Reason, at = cand.CreatedAt, evidence });
    }

    private static object ToDto(HuntJob j) => new
    {
        id = j.Id, kind = j.Kind, status = j.Status, segment = j.Segment, region = j.Region, minScore = j.MinScore, targetCount = j.TargetCount,
        maxCandidates = j.MaxCandidates, ratchet = j.Ratchet, total = j.Total, screened = j.Screened, qualified = j.Qualified, best = j.BestScore,
        current = j.CurrentCandidate, error = j.Error, taskId = j.TaskId, taskUrl = j.TaskUrl, summary = j.Summary, lastAgentCallAt = j.LastAgentCallAt,
        createdAt = j.CreatedAt, startedAt = j.StartedAt, completedAt = j.CompletedAt, organizationId = j.OrganizationId,
    };
}

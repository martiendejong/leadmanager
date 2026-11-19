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

public record StartHuntRequest(string? Segment, string? Region, int? MinScore, int? TargetCount, int? MaxCandidates, bool? Ratchet);
public record VerifyLeadsRequest(Guid[]? Ids);

/// <summary>
/// Lead hunt and verify. A hunt is researched by a background Jengo agent that the app starts (a JengoWork task); every candidate the
/// agent submits is re-checked by the server. A verify rescored existing leads from their own website by rules only: no key needed.
/// </summary>
[ApiController]
[Route("api/leads")]
[Authorize]
[AllowApiKey]
public class HuntController : ControllerBase
{
    public const int MaxVerifyIds = 50;
    public const string NotConfigured = "The Jengo agent dispatch is not configured on this server";
    public const string TokenMissing = "The Jengo agent token is not configured on this server";

    private readonly LeadManagerDbContext _db;
    private readonly IHuntDispatcher _dispatcher;
    private readonly GooglePlacesEnrichmentService _places;
    private readonly HuntQueue _queue;
    private readonly HuntRegistry _registry;
    private readonly IConfiguration _config;

    public HuntController(LeadManagerDbContext db, IHuntDispatcher dispatcher, GooglePlacesEnrichmentService places, HuntQueue queue, HuntRegistry registry, IConfiguration config)
    {
        _db = db; _dispatcher = dispatcher; _places = places; _queue = queue; _registry = registry; _config = config;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private bool AgentTokenConfigured => !string.IsNullOrWhiteSpace(_config["Hunt:AgentToken"]);
    private int MaxPerDay => Math.Clamp(int.TryParse(_config["Hunt:MaxPerUserPerDay"], out var n) ? n : 5, 1, 50);

    // GET /api/leads/hunt/capabilities
    [HttpGet("hunt/capabilities")]
    public async Task<IActionResult> Capabilities()
    {
        var ready = _dispatcher.IsConfigured && AgentTokenConfigured;
        return Ok(new
        {
            huntAvailable = ready,
            canStartHunt = ready && await HuntAccess.IsAllowedAsync(_db, _config, UserId),
            dispatchConfigured = _dispatcher.IsConfigured,
            agentTokenConfigured = AgentTokenConfigured,
            googlePlacesConfigured = _places.IsConfigured,
            maxPerDay = MaxPerDay,
            message = !_dispatcher.IsConfigured ? NotConfigured : !AgentTokenConfigured ? TokenMissing : null,
        });
    }

    // POST /api/leads/hunt
    [HttpPost("hunt")]
    public async Task<IActionResult> Start([FromBody] StartHuntRequest request, CancellationToken ct)
    {
        if (!_dispatcher.IsConfigured) return StatusCode(503, new { error = NotConfigured, code = "dispatch_not_configured" });
        if (!AgentTokenConfigured) return StatusCode(503, new { error = TokenMissing, code = "agent_token_missing" });
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!await HuntAccess.IsAllowedAsync(_db, _config, userId))
            return StatusCode(403, new { error = "You are not allowed to start a lead hunt. Ask an administrator.", code = "hunt_not_allowed" });

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

        if (await _db.HuntJobs.AnyAsync(j => j.UserId == userId && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running), ct))
            return Conflict(new { error = "A job is already running for you. Wait for it to finish or cancel it." });
        var since = DateTime.UtcNow.Date;
        if (await _db.HuntJobs.CountAsync(j => j.UserId == userId && j.Kind == HuntKinds.Hunt && j.CreatedAt >= since, ct) >= MaxPerDay)
            return StatusCode(429, new { error = $"You can start {MaxPerDay} lead hunts per day. Try again tomorrow.", code = "daily_cap" });

        var job = new HuntJob
        {
            UserId = userId, Kind = HuntKinds.Hunt, Segment = segment, Region = region, MinScore = min, TargetCount = target,
            MaxCandidates = maxCandidates, Ratchet = request.Ratchet ?? true, Total = maxCandidates,
        };
        _db.HuntJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            var result = await _dispatcher.DispatchAsync(job, HuntPrompt.Render(job, HuntAccess.ApiBase(_config)), ct);
            job.TaskId = result.TaskId;
            job.TaskUrl = result.TaskUrl;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.Status = HuntStatuses.Failed;
            job.Error = ex is DispatchNotConfiguredException ? NotConfigured : "The Jengo agent could not be started: " + (ex is HttpRequestException ? ex.Message : "unexpected error.");
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(502, new { error = job.Error, jobId = job.Id });
        }
        return Accepted(new { jobId = job.Id, taskUrl = job.TaskUrl });
    }

    // POST /api/leads/verify   and   POST /api/leads/{id}/verify
    [HttpPost("verify")]
    public Task<IActionResult> VerifyMany([FromBody] VerifyLeadsRequest request) => StartVerify(request.Ids ?? Array.Empty<Guid>());

    [HttpPost("{id:guid}/verify")]
    public Task<IActionResult> VerifyOne(Guid id) => StartVerify(new[] { id });

    private async Task<IActionResult> StartVerify(Guid[] ids)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        ids = ids.Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { error = "No lead IDs provided." });
        if (ids.Length > MaxVerifyIds) return BadRequest(new { error = $"At most {MaxVerifyIds} leads per request." });

        var own = await _db.Leads.Where(l => ids.Contains(l.Id) && l.ImportedByUserId == userId).Select(l => l.Id).ToListAsync();
        if (own.Count == 0) return NotFound(new { error = "None of these leads belong to your account." });

        if (await _db.HuntJobs.AnyAsync(j => j.UserId == userId && (j.Status == HuntStatuses.Pending || j.Status == HuntStatuses.Running)))
            return Conflict(new { error = "A job is already running for you. Wait for it to finish or cancel it." });

        var job = new HuntJob { UserId = userId, Kind = HuntKinds.Verify, LeadIds = own, Total = own.Count };
        _db.HuntJobs.Add(job);
        await _db.SaveChangesAsync();
        await _queue.Writer.WriteAsync(job.Id);
        return Accepted(new { jobId = job.Id, leads = own.Count, skipped = ids.Length - own.Count });
    }

    // GET /api/leads/hunt
    [HttpGet("hunt")]
    public async Task<IActionResult> Recent()
    {
        var userId = UserId;
        var jobs = await _db.HuntJobs.AsNoTracking().Where(j => j.UserId == userId).OrderByDescending(j => j.CreatedAt).Take(10).ToListAsync();
        return Ok(jobs.Select(ToDto));
    }

    // GET /api/leads/hunt/{id}
    [HttpGet("hunt/{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var userId = UserId;
        var job = await _db.HuntJobs.AsNoTracking().Include(j => j.Candidates).FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();
        return Ok(new
        {
            job = ToDto(job),
            candidates = job.Candidates.OrderBy(c => c.CreatedAt).Select(c => new
            {
                id = c.Id, leadId = c.LeadId, name = c.Name, website = c.Website, score = c.Score, maxReachable = c.MaxReachable,
                outcome = c.Outcome, reason = c.Reason, at = c.CreatedAt,
            }),
        });
    }

    // POST /api/leads/hunt/{id}/cancel
    [HttpPost("hunt/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = UserId;
        var job = await _db.HuntJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();
        if (!HuntStatuses.IsActive(job.Status)) return Ok(ToDto(job));
        job.CancelRequested = true;
        // A hunt that no agent has started yet ends now; a running hunt ends at the agent's next call (it gets stop=true).
        if (job.Status == HuntStatuses.Pending || (job.Kind == HuntKinds.Hunt && job.LastAgentCallAt == null))
        {
            job.Status = HuntStatuses.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        _registry.Cancel(job.Id);
        return Ok(ToDto(job));
    }

    // GET /api/leads/{id}/evidence : the sources and quotes behind the latest verification of a lead
    [HttpGet("{id:guid}/evidence")]
    public async Task<IActionResult> Evidence(Guid id)
    {
        var userId = UserId;
        var owns = await _db.Leads.AsNoTracking().AnyAsync(l => l.Id == id && l.ImportedByUserId == userId);
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
        createdAt = j.CreatedAt, startedAt = j.StartedAt, completedAt = j.CompletedAt,
    };
}

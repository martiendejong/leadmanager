using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Compose;
using LeadManager.Api.Services.Hunt;
using LeadManager.Api.Services.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record AiMessageRequest(string? Channel, string? Product, string? Instruction, string? Subject, string? Body);
public record AiDemosRequest(string? Instruction);
public record DemoIdeaDto(Guid Id, string Title, string Shows, string WhyFit, string ProductKey, string Prompt, string Instruction, string Model, DateTime GeneratedAt);

public static class AiComposeRateLimit
{
    public const string Policy = "ai-compose";
    public const int PerMinute = 10;

    /// <summary>One counter per bearer token (hashed, never stored), so a person is limited, not everybody behind one proxy address. The limiter runs before sign in, so the token is read from the header.</summary>
    public static string PolicyKey(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.Length > 0) return "t|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(auth)))[..24];
        return "ip|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}

/// <summary>
/// AI help for the message section of a lead: regenerate the draft with an instruction, and suggest demos we could build for the lead.
/// Nothing is sent, nothing but the demo suggestions is stored (a message stays a draft the user saves themselves), and the status of the lead
/// never changes. A person at the screen only: API keys are refused. Without a key the endpoints answer 503 "AI is not configured" and
/// everything else (templates, drafts) keeps working.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public class LeadAiController : ControllerBase
{
    public const string NotConfigured = "AI is not configured on this server. The templates still work.";

    private readonly LeadManagerDbContext _db;
    private readonly LeadAiComposer _composer;
    private readonly ILogger<LeadAiController> _logger;

    public LeadAiController(LeadManagerDbContext db, LeadAiComposer composer, ILogger<LeadAiController> logger)
    {
        _db = db; _composer = composer; _logger = logger;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private async Task<Lead?> OwnLeadAsync(Guid leadId) =>
        await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId && l.ImportedByUserId == UserId);

    private IActionResult NotConfiguredResult() => StatusCode(503, new { error = NotConfigured, code = "ai_not_configured" });

    private IActionResult Failure(Exception ex, Guid leadId)
    {
        switch (ex)
        {
            case AiNotConfiguredException:
                return NotConfiguredResult();
            case AiOutputRejectedException r:
                _logger.LogInformation("AI compose output rejected for lead {LeadId}", leadId);
                return StatusCode(502, new { error = r.Message, code = "ai_output_rejected" });
            case AiUnavailableException u:
                return StatusCode(502, new { error = u.Message, code = "ai_unavailable" });
            default:
                throw ex;
        }
    }

    // GET /api/ai/status : is AI configured (no secret, no model of the key)
    [HttpGet("ai/status")]
    public IActionResult Status() => Ok(new { configured = _composer.IsConfigured, model = _composer.IsConfigured ? _composer.Model : null,
        maxInstruction = AiGuards.MaxInstruction, message = _composer.IsConfigured ? null : NotConfigured });

    // POST /api/leads/{leadId}/ai/message : a new draft text from the lead's facts; it replaces nothing on the server
    [DenyApiKey]
    [EnableRateLimiting(AiComposeRateLimit.Policy)]
    [HttpPost("leads/{leadId:guid}/ai/message")]
    [RequestSizeLimit(32_768)]
    public async Task<IActionResult> Message(Guid leadId, [FromBody] AiMessageRequest request, CancellationToken ct)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        if (!_composer.IsConfigured) return NotConfiguredResult();

        var channel = MessageChannels.Canonical(request.Channel ?? MessageChannels.Email);
        if (channel == null) return BadRequest(new { error = $"Channel is one of: {string.Join(", ", MessageChannels.All)}." });
        string? product = null;
        if (!string.IsNullOrWhiteSpace(request.Product))
        {
            product = ProductCatalog.Canonical(request.Product);
            if (product == null) return BadRequest(new { error = $"'{request.Product}' is not one of our products. Known: {string.Join(", ", ProductCatalog.Keys)}." });
        }
        var bad = AiGuards.TryClean(request.Instruction, out var instruction);
        if (bad != null) return BadRequest(new { error = bad, code = "instruction_too_long" });

        var userId = UserId;
        // The starting point: what is in the editor now, otherwise the deterministic template (the user's own or the built-in one).
        var startSubject = AiGuards.EditorText(request.Subject, MessageRules.MaxSubject);
        var startBody = AiGuards.EditorText(request.Body, MessageRules.MaxBody);
        var from = "editor";
        if (startBody.Length == 0)
        {
            var messages = new MessagesController(_db) { ControllerContext = ControllerContext };
            var prefill = await messages.BuildPrefillAsync(lead, channel, product);
            // the template holds quotes from the business's website: markup characters out, like the editor text
            startSubject = AiGuards.EditorText(prefill.Subject, MessageRules.MaxSubject); startBody = AiGuards.EditorText(prefill.Body, MessageRules.MaxBody); from = "template";
        }
        var me = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.FirstName, u.LastName }).FirstOrDefaultAsync();
        var sender = me == null ? "" : $"{me.FirstName} {me.LastName}".Trim();

        try
        {
            var started = DateTime.UtcNow;
            var result = await _composer.ComposeMessageAsync(lead, sender, channel, product, instruction, startSubject, startBody, ct);
            _logger.LogInformation("AI compose message for lead {LeadId} channel {Channel} in {Ms} ms", leadId, channel, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return Ok(new { channel = result.Channel, subject = result.Subject, body = result.Body, language = result.Language, model = result.Model,
                notes = result.Notes, startedFrom = from });
        }
        catch (Exception ex) when (ex is AiNotConfiguredException or AiOutputRejectedException or AiUnavailableException)
        {
            return Failure(ex, leadId);
        }
    }

    private static DemoIdeaDto ToDto(LeadDemoIdea d) => new(d.Id, d.Title, d.Shows, d.WhyFit, d.ProductKey, d.Prompt, d.Instruction, d.Model, d.GeneratedAt);

    // GET /api/leads/{leadId}/ai/demos : the latest saved suggestions
    [HttpGet("leads/{leadId:guid}/ai/demos")]
    public async Task<IActionResult> Demos(Guid leadId)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        var items = await _db.LeadDemoIdeas.AsNoTracking().Where(d => d.LeadId == leadId && d.UserId == UserId).OrderBy(d => d.Position).ToListAsync();
        return Ok(new { items = items.Select(ToDto), generatedAt = items.Count == 0 ? (DateTime?)null : items.Max(i => i.GeneratedAt), configured = _composer.IsConfigured });
    }

    // POST /api/leads/{leadId}/ai/demos : (re)generate; the new set replaces the old one
    [DenyApiKey]
    [EnableRateLimiting(AiComposeRateLimit.Policy)]
    [HttpPost("leads/{leadId:guid}/ai/demos")]
    [RequestSizeLimit(8_192)]
    public async Task<IActionResult> GenerateDemos(Guid leadId, [FromBody] AiDemosRequest? request, CancellationToken ct)
    {
        var lead = await OwnLeadAsync(leadId);
        if (lead == null) return NotFound();
        if (!_composer.IsConfigured) return NotConfiguredResult();
        var bad = AiGuards.TryClean(request?.Instruction, out var instruction);
        if (bad != null) return BadRequest(new { error = bad, code = "instruction_too_long" });

        DemoSet set;
        try { set = await _composer.GenerateDemosAsync(lead, instruction, ct); }
        catch (Exception ex) when (ex is AiNotConfiguredException or AiOutputRejectedException or AiUnavailableException) { return Failure(ex, leadId); }

        var userId = UserId!;
        // One generation replaces the previous one (one SaveChanges: the old set is never half gone).
        var old = await _db.LeadDemoIdeas.Where(d => d.LeadId == leadId && d.UserId == userId).ToListAsync();
        _db.LeadDemoIdeas.RemoveRange(old);
        var batch = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = set.Ideas.Select((d, i) => new LeadDemoIdea
        {
            LeadId = leadId, UserId = userId, BatchId = batch, Position = i, Title = d.Title, Shows = d.Shows, WhyFit = d.WhyFit,
            ProductKey = d.ProductKey, Prompt = d.Prompt, Instruction = instruction, Model = set.Model, GeneratedAt = now,
        }).ToList();
        _db.LeadDemoIdeas.AddRange(rows);
        await _db.SaveChangesAsync(ct);
        return Ok(new { items = rows.Select(ToDto), generatedAt = rows.Count == 0 ? (DateTime?)null : now, noFitReason = set.NoFitReason, configured = true });
    }
}

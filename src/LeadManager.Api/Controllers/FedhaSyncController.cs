using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Fedha;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record LinkContactDto(int FedhaContactId, string? FedhaContactName, Guid ClientId);

/// <summary>
/// Pulls the invoices from Fedha into Clients (read-only towards Fedha) so the agreements overview can show sent, open and paid.
/// Contacts without a client are listed and can be linked by hand; clients are never created automatically.
/// </summary>
[ApiController]
[Authorize]
public class FedhaSyncController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly FedhaSyncService _sync;
    private readonly FedhaOptions _opt;

    public FedhaSyncController(LeadManagerDbContext db, FedhaSyncService sync, FedhaOptions opt) { _db = db; _sync = sync; _opt = opt; }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // POST /api/fedha-sync/run
    [HttpPost("api/fedha-sync/run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!_opt.Configured) return StatusCode(503, new { error = "Fedha is not configured on this environment (Fedha__ApiKey)." });
        try { return Ok(await _sync.RunAsync(userId, "manual", ct)); }
        catch (InvalidOperationException e) { return StatusCode(502, new { error = e.Message }); }
        catch (HttpRequestException e) { return StatusCode(502, new { error = "Fedha is not reachable: " + e.Message }); }
    }

    // GET /api/fedha-sync/status
    [HttpGet("api/fedha-sync/status")]
    public async Task<IActionResult> Status()
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var last = await _db.FedhaSyncRuns.Where(r => r.UserId == userId).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync();
        var lastOk = await _db.FedhaSyncRuns.Where(r => r.UserId == userId && r.Error == null && r.FinishedAt != null).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync();
        var unmatched = lastOk?.UnmatchedJson == null ? new List<UnmatchedContact>()
            : JsonSerializer.Deserialize<List<UnmatchedContact>>(lastOk.UnmatchedJson) ?? new List<UnmatchedContact>();
        return Ok(new
        {
            configured = _opt.Configured,
            lastRun = last == null ? null : new { last.StartedAt, last.FinishedAt, last.Trigger, last.Created, last.Updated, last.Unchanged, last.Unmatched, last.Error },
            lastSuccessAt = lastOk?.FinishedAt,
            unmatched,
        });
    }

    // GET /api/fedha-sync/links
    [HttpGet("api/fedha-sync/links")]
    public async Task<IActionResult> Links()
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        return Ok(await _db.FedhaContactLinks.Where(l => l.UserId == userId).OrderBy(l => l.FedhaContactName).ToListAsync());
    }

    // POST /api/fedha-sync/links  (links a Fedha contact to a client; takes effect on the next sync)
    [HttpPost("api/fedha-sync/links")]
    public async Task<IActionResult> Link([FromBody] LinkContactDto dto)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!await _db.Clients.AnyAsync(c => c.Id == dto.ClientId && c.CreatedByUserId == userId)) return NotFound();
        var name = CrmRules.Clean(dto.FedhaContactName) ?? "";
        if (name.Length > 200) return BadRequest(new { error = "Name is too long." });
        var existing = await _db.FedhaContactLinks.FirstOrDefaultAsync(l => l.UserId == userId && l.FedhaContactId == dto.FedhaContactId);
        if (existing == null) { existing = new FedhaContactLink { UserId = userId, FedhaContactId = dto.FedhaContactId }; _db.FedhaContactLinks.Add(existing); }
        existing.ClientId = dto.ClientId;
        if (name.Length > 0) existing.FedhaContactName = name;
        await _db.SaveChangesAsync();
        return Ok(existing);
    }

    // DELETE /api/fedha-sync/links/{id}
    [HttpDelete("api/fedha-sync/links/{id:guid}")]
    public async Task<IActionResult> Unlink(Guid id)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var l = await _db.FedhaContactLinks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (l == null) return NotFound();
        _db.FedhaContactLinks.Remove(l);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

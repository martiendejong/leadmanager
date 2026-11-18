using JengoTrack;
using LeadManager.Api.Data;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Track;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

public record CreateTrackedLinkDto(string Url, string? Campaign, string? Label);

/// <summary>Jengo Track on a lead or client: click timeline and tracked links (task 8071).</summary>
public class TrackController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly TrackGateway _track;

    public TrackController(LeadManagerDbContext db, CrmParentAccess access, TrackGateway track) : base(access)
    {
        _db = db;
        _track = track;
    }

    public static string EntityType(CrmParent parent) => parent.Kind == CrmParentKind.Lead ? "lead" : "client";

    // GET /api/leads/{id}/track  |  /api/clients/{id}/track
    // Never an error because of Track: disabled or unreachable gives an empty result.
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/track")]
    public async Task<IActionResult> Timeline(string parentType, Guid parentId, CancellationToken ct)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();
        return Ok(await _track.GetTimelineAsync(EntityType(parent.Value), parentId, ct));
    }

    // POST /api/leads/{id}/track/link  |  /api/clients/{id}/track/link
    [HttpPost($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/track/link")]
    public async Task<IActionResult> CreateLink(string parentType, Guid parentId, [FromBody] CreateTrackedLinkDto dto, CancellationToken ct)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();
        if (!_track.Enabled) return StatusCode(503, "Klik-tracking staat niet aan.");

        var url = Clean(dto.Url);
        if (!IsSafeHttpUrl(url)) return BadRequest("Url must be an absolute http(s) address");
        var campaign = Clean(dto.Campaign);
        var label = Clean(dto.Label);
        if (TooLong(campaign, MaxLabel)) return BadRequest($"Campaign is too long (max {MaxLabel})");
        if (TooLong(label, MaxLabel)) return BadRequest($"Label is too long (max {MaxLabel})");

        var entityLabel = parent.Value.Kind == CrmParentKind.Lead
            ? await _db.Leads.Where(l => l.Id == parentId).Select(l => l.Name).FirstAsync(ct)
            : await _db.Clients.Where(c => c.Id == parentId).Select(c => c.Name).FirstAsync(ct);

        var link = await _track.CreateLinkAsync(new LinkRequest
        {
            Url = url!,
            EntityType = EntityType(parent.Value),
            EntityId = parentId.ToString(),
            EntityLabel = entityLabel,
            Campaign = campaign,
            Label = label,
            SourceTool = "leadmanager",
            Channel = "mail",
        }, ct);

        if (link == null || string.IsNullOrEmpty(link.ShortUrl))
            return StatusCode(502, "Track kon de link niet maken (onbereikbaar, of dit domein staat niet op de toegestane lijst).");
        return Ok(new { shortUrl = link.ShortUrl, token = link.Token });
    }
}

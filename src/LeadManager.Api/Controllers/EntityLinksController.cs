using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Typed links from a lead or client to the other Jengo tools (JengoWork board or task,
/// JengoKnowledge project, mail thread, Meet session, ...). References, not copies.
/// </summary>
public class EntityLinksController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;

    public EntityLinksController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static EntityLinkDto ToDto(EntityLink l) =>
        new(l.Id, l.LeadId, l.ClientId, l.Kind.ToString(), l.Label, l.Url, l.ExternalId, l.CreatedAt);

    // GET /api/leads/{id}/links  |  /api/clients/{id}/links
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/links")]
    public async Task<IActionResult> List(string parentType, Guid parentId)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        var items = await _db.EntityLinks
            .Where(l => l.LeadId == parent.Value.LeadId && l.ClientId == parent.Value.ClientId)
            .OrderBy(l => l.Kind).ThenBy(l => l.Label)
            .ToListAsync();
        return Ok(items.Select(ToDto));
    }

    // POST /api/leads/{id}/links  |  /api/clients/{id}/links
    [HttpPost($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/links")]
    public async Task<IActionResult> Create(string parentType, Guid parentId, [FromBody] CreateEntityLinkDto dto)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        if (!Enum.TryParse<EntityLinkKind>(dto.Kind, true, out var kind))
            return BadRequest($"Invalid kind: {dto.Kind}. Allowed: {string.Join(", ", Enum.GetNames<EntityLinkKind>())}");
        if (string.IsNullOrWhiteSpace(dto.Label)) return BadRequest("Label is required");
        if (string.IsNullOrWhiteSpace(dto.Url) && string.IsNullOrWhiteSpace(dto.ExternalId))
            return BadRequest("Provide a url, an externalId or both");

        // Only http(s): the UI renders this as a clickable link, so no javascript: or data: URLs.
        if (!string.IsNullOrWhiteSpace(dto.Url) &&
            !(Uri.TryCreate(dto.Url, UriKind.Absolute, out var uri) &&
              (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            return BadRequest("Url must be an absolute http or https URL");

        // The same target is linked once per parent (idempotent for agents that retry).
        var existing = await _db.EntityLinks.FirstOrDefaultAsync(l =>
            l.LeadId == parent.Value.LeadId && l.ClientId == parent.Value.ClientId &&
            l.Kind == kind && l.Url == dto.Url && l.ExternalId == dto.ExternalId);
        if (existing != null) return Ok(ToDto(existing));

        var link = new EntityLink
        {
            LeadId = parent.Value.LeadId,
            ClientId = parent.Value.ClientId,
            Kind = kind,
            Label = dto.Label.Trim(),
            Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url.Trim(),
            ExternalId = string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId.Trim(),
            CreatedByUserId = GetCurrentUserId(),
        };
        _db.EntityLinks.Add(link);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { parentType, parentId }, ToDto(link));
    }

    // DELETE /api/links/{id}
    [HttpDelete("api/links/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var link = await _db.EntityLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link == null || await ResolveOwnerAsync(link.LeadId, link.ClientId) == null) return NotFound();

        _db.EntityLinks.Remove(link);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

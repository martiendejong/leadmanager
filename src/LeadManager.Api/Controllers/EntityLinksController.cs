using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

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
        new(l.Id, l.LeadId, l.ClientId, l.Kind.ToString(), l.Label, l.Url, l.ExternalId, l.CreatedAt, l.ProjectId);

    // GET /api/leads/{id}/links  |  /api/clients/{id}/links
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/links")]
    public async Task<IActionResult> List(string parentType, Guid parentId, [FromQuery] Guid? projectId = null, [FromQuery] bool includeProjects = false)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        // Default: the links of the lead or client itself. projectId = those of one project; includeProjects = all.
        var items = await _db.EntityLinks
            .Where(l => l.LeadId == parent.Value.LeadId && l.ClientId == parent.Value.ClientId)
            .Where(l => includeProjects || (projectId == null ? l.ProjectId == null : l.ProjectId == projectId))
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

        if (!TryParseName<EntityLinkKind>(dto.Kind, out var kind))
            return BadRequest($"Invalid kind: {dto.Kind}. Allowed: {string.Join(", ", Enum.GetNames<EntityLinkKind>())}");

        // Normalise first, so the duplicate check below compares what will actually be stored.
        var label = Clean(dto.Label);
        var url = Clean(dto.Url);
        var externalId = Clean(dto.ExternalId);

        if (label == null) return BadRequest("Label is required");
        if (TooLong(label, MaxLabel)) return BadRequest($"Label is too long (max {MaxLabel})");
        if (url == null && externalId == null) return BadRequest("Provide a url, an externalId or both");
        if (TooLong(externalId, MaxSourceRef)) return BadRequest($"ExternalId is too long (max {MaxSourceRef})");

        // Only http(s): the UI renders this as a clickable link, so no javascript: or data: URLs.
        if (url != null && !IsSafeHttpUrl(url))
            return BadRequest($"Url must be an absolute http or https URL (max {MaxUrl} characters)");

        if (dto.ProjectId.HasValue)
        {
            // Projects belong to clients only, and to the client in the route.
            var projectOk = parent.Value.Kind == CrmParentKind.Client
                && await _db.Projects.AnyAsync(p => p.Id == dto.ProjectId && p.ClientId == parent.Value.Id);
            if (!projectOk) return BadRequest("Project does not belong to this client");
        }

        // The same target is linked once per parent or project (idempotent for agents that retry).
        var existing = await _db.EntityLinks.FirstOrDefaultAsync(l =>
            l.LeadId == parent.Value.LeadId && l.ClientId == parent.Value.ClientId && l.ProjectId == dto.ProjectId &&
            l.Kind == kind && l.Url == url && l.ExternalId == externalId);
        if (existing != null) return Ok(ToDto(existing));

        var link = new EntityLink
        {
            LeadId = parent.Value.LeadId,
            ClientId = parent.Value.ClientId,
            ProjectId = dto.ProjectId,
            Kind = kind,
            Label = label,
            Url = url,
            ExternalId = externalId,
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

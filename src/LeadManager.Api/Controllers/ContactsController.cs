using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>People at a lead or client (a firm has several contacts).</summary>
public class ContactsController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ContactsController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static ContactDto ToDto(Contact c) => new(
        c.Id, c.LeadId, c.ClientId, c.Name, c.Role, c.Email, c.Phone, c.Mobile,
        c.LinkedInUrl, c.PreferredChannel, c.Notes, c.CreatedAt);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    // GET /api/leads/{id}/contacts  |  /api/clients/{id}/contacts
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/contacts")]
    public async Task<IActionResult> List(string parentType, Guid parentId)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        var items = await _db.Contacts
            .Where(c => c.LeadId == parent.Value.LeadId && c.ClientId == parent.Value.ClientId)
            .OrderBy(c => c.Name)
            .ToListAsync();
        return Ok(items.Select(ToDto));
    }

    // POST /api/leads/{id}/contacts  |  /api/clients/{id}/contacts
    [HttpPost($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/contacts")]
    public async Task<IActionResult> Create(string parentType, Guid parentId, [FromBody] UpsertContactDto dto)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required");

        var contact = new Contact
        {
            LeadId = parent.Value.LeadId,
            ClientId = parent.Value.ClientId,
            Name = dto.Name.Trim(),
            Role = Clean(dto.Role),
            Email = Clean(dto.Email),
            Phone = Clean(dto.Phone),
            Mobile = Clean(dto.Mobile),
            LinkedInUrl = Clean(dto.LinkedInUrl),
            PreferredChannel = Clean(dto.PreferredChannel),
            Notes = Clean(dto.Notes),
            CreatedByUserId = GetCurrentUserId(),
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { parentType, parentId }, ToDto(contact));
    }

    // PUT /api/contacts/{id}
    [HttpPut("api/contacts/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertContactDto dto)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == id);
        if (contact == null || await ResolveOwnerAsync(contact.LeadId, contact.ClientId) == null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required");

        contact.Name = dto.Name.Trim();
        contact.Role = Clean(dto.Role);
        contact.Email = Clean(dto.Email);
        contact.Phone = Clean(dto.Phone);
        contact.Mobile = Clean(dto.Mobile);
        contact.LinkedInUrl = Clean(dto.LinkedInUrl);
        contact.PreferredChannel = Clean(dto.PreferredChannel);
        contact.Notes = Clean(dto.Notes);

        await _db.SaveChangesAsync();
        return Ok(ToDto(contact));
    }

    // DELETE /api/contacts/{id}
    [HttpDelete("api/contacts/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == id);
        if (contact == null || await ResolveOwnerAsync(contact.LeadId, contact.ClientId) == null) return NotFound();

        _db.Contacts.Remove(contact);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

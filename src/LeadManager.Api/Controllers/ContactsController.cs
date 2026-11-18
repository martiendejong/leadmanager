using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

/// <summary>People at a lead or client (a firm has several contacts).</summary>
public class ContactsController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ContactsController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static ContactDto ToDto(Contact c) => new(
        c.Id, c.LeadId, c.ClientId, c.Name, c.Role, c.Email, c.Phone, c.Mobile,
        c.LinkedInUrl, c.PreferredChannel, c.Notes, c.CreatedAt);

    /// <summary>Validates a contact body; returns an error message or null.</summary>
    private static string? Validate(UpsertContactDto dto)
    {
        if (Clean(dto.Name) == null) return "Name is required";
        if (TooLong(dto.Name?.Trim(), MaxName)) return $"Name is too long (max {MaxName})";
        foreach (var (field, value) in new[]
                 {
                     ("Role", dto.Role), ("Email", dto.Email), ("Phone", dto.Phone),
                     ("Mobile", dto.Mobile), ("PreferredChannel", dto.PreferredChannel),
                 })
            if (TooLong(Clean(value), MaxName)) return $"{field} is too long (max {MaxName})";
        if (TooLong(Clean(dto.Notes), MaxNotes)) return $"Notes is too long (max {MaxNotes})";

        // LinkedIn is rendered as a link by clients: same http(s)-only rule as EntityLink.
        var linkedIn = Clean(dto.LinkedInUrl);
        if (linkedIn != null && !IsSafeHttpUrl(linkedIn)) return "LinkedInUrl must be an absolute http or https URL";
        return null;
    }

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
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

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
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

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

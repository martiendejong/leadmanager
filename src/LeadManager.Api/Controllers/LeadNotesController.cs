using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[Route("api/leads/{leadId:guid}/notes")]
[ApiController]
[Authorize]
public class LeadNotesController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public LeadNotesController(LeadManagerDbContext db)
    {
        _db = db;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/leads/{leadId}/notes
    [HttpGet]
    public async Task<IActionResult> GetNotes(Guid leadId)
    {
        var userId = GetCurrentUserId();

        // Verify lead belongs to user
        var leadExists = await _db.Leads.AnyAsync(l => l.Id == leadId && l.ImportedByUserId == userId);
        if (!leadExists) return NotFound("Lead not found");

        var notes = await _db.LeadNotes
            .Where(n => n.LeadId == leadId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new LeadNoteDto(
                n.Id,
                n.LeadId,
                n.Content,
                n.CreatedAt,
                n.CreatedByUserId,
                n.CreatedBy != null ? n.CreatedBy.UserName : null))
            .ToListAsync();

        return Ok(notes);
    }

    // POST /api/leads/{leadId}/notes
    [HttpPost]
    public async Task<IActionResult> CreateNote(Guid leadId, [FromBody] CreateLeadNoteDto dto)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // Verify lead belongs to user
        var leadExists = await _db.Leads.AnyAsync(l => l.Id == leadId && l.ImportedByUserId == userId);
        if (!leadExists) return NotFound("Lead not found");

        // Validate content
        if (string.IsNullOrWhiteSpace(dto.Content))
            return BadRequest("Content is required");

        if (dto.Content.Length > 5000)
            return BadRequest("Content must be 5000 characters or less");

        var note = new LeadNote
        {
            LeadId = leadId,
            Content = dto.Content.Trim(),
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        _db.LeadNotes.Add(note);
        await _db.SaveChangesAsync();

        var createdNote = await _db.LeadNotes
            .Where(n => n.Id == note.Id)
            .Select(n => new LeadNoteDto(
                n.Id,
                n.LeadId,
                n.Content,
                n.CreatedAt,
                n.CreatedByUserId,
                n.CreatedBy != null ? n.CreatedBy.UserName : null))
            .FirstOrDefaultAsync();

        return CreatedAtAction(nameof(GetNotes), new { leadId }, createdNote);
    }

    // DELETE /api/leads/{leadId}/notes/{noteId}
    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> DeleteNote(Guid leadId, Guid noteId)
    {
        var userId = GetCurrentUserId();

        // Verify lead belongs to user
        var leadExists = await _db.Leads.AnyAsync(l => l.Id == leadId && l.ImportedByUserId == userId);
        if (!leadExists) return NotFound("Lead not found");

        // Find note and verify it belongs to this lead and user created it
        var note = await _db.LeadNotes
            .FirstOrDefaultAsync(n => n.Id == noteId && n.LeadId == leadId && n.CreatedByUserId == userId);

        if (note == null) return NotFound("Note not found");

        _db.LeadNotes.Remove(note);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}

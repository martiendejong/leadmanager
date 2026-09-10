using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[Route("api/leads/{leadId:guid}/activities")]
[ApiController]
[Authorize]
public class ActivitiesController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ActivitiesController(LeadManagerDbContext db)
    {
        _db = db;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/leads/{leadId}/activities
    [HttpGet]
    public async Task<IActionResult> GetActivities(Guid leadId)
    {
        var userId = GetCurrentUserId();

        // Verify lead belongs to current user
        var leadExists = await _db.Leads.AnyAsync(l => l.Id == leadId && l.ImportedByUserId == userId);
        if (!leadExists) return NotFound();

        var activities = await _db.Activities
            .Where(a => a.LeadId == leadId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new LeadActivityDto(
                a.Id,
                a.LeadId,
                a.UserId,
                a.ActivityType.ToString(),
                a.Note,
                a.CreatedAt))
            .ToListAsync();

        return Ok(activities);
    }

    // POST /api/leads/{leadId}/activities
    [HttpPost]
    public async Task<IActionResult> AddActivity(Guid leadId, [FromBody] CreateActivityDto dto)
    {
        var userId = GetCurrentUserId();

        // Verify lead belongs to current user
        var leadExists = await _db.Leads.AnyAsync(l => l.Id == leadId && l.ImportedByUserId == userId);
        if (!leadExists) return NotFound();

        if (!Enum.TryParse<ActivityType>(dto.ActivityType, out var activityType))
            return BadRequest($"Invalid activity type: {dto.ActivityType}");

        var activity = new LeadActivity
        {
            LeadId = leadId,
            UserId = userId,
            ActivityType = activityType,
            Note = dto.Note,
            CreatedAt = DateTime.UtcNow
        };

        _db.Activities.Add(activity);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetActivities), new { leadId }, new LeadActivityDto(
            activity.Id,
            activity.LeadId,
            activity.UserId,
            activity.ActivityType.ToString(),
            activity.Note,
            activity.CreatedAt));
    }
}

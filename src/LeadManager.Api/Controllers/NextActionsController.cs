using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>"What do I do next" per lead or client, plus the daily call list.</summary>
public class NextActionsController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;

    public NextActionsController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static NextActionDto ToDto(NextAction a) => new(
        a.Id, a.LeadId, a.ClientId, a.ContactId, a.Title, a.DueAt, a.AssignedToUserId, a.DoneAt, a.CreatedAt);

    // GET /api/leads/{id}/next-actions?includeDone=false
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/next-actions")]
    public async Task<IActionResult> List(string parentType, Guid parentId, [FromQuery] bool includeDone = false)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        var query = _db.NextActions.Where(a => a.LeadId == parent.Value.LeadId && a.ClientId == parent.Value.ClientId);
        if (!includeDone) query = query.Where(a => a.DoneAt == null);

        var items = await query.OrderBy(a => a.DueAt).ToListAsync();
        return Ok(items.Select(ToDto));
    }

    // POST /api/leads/{id}/next-actions
    [HttpPost($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/next-actions")]
    public async Task<IActionResult> Create(string parentType, Guid parentId, [FromBody] CreateNextActionDto dto)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest("Title is required");

        if (dto.ContactId.HasValue)
        {
            var contactOk = await _db.Contacts.AnyAsync(c =>
                c.Id == dto.ContactId && c.LeadId == parent.Value.LeadId && c.ClientId == parent.Value.ClientId);
            if (!contactOk) return BadRequest("Contact does not belong to this lead or client");
        }

        var action = new NextAction
        {
            LeadId = parent.Value.LeadId,
            ClientId = parent.Value.ClientId,
            ContactId = dto.ContactId,
            Title = dto.Title.Trim(),
            DueAt = dto.DueAt.ToUniversalTime(),
            AssignedToUserId = string.IsNullOrWhiteSpace(dto.AssignedToUserId) ? GetCurrentUserId() : dto.AssignedToUserId,
            CreatedByUserId = GetCurrentUserId(),
        };
        _db.NextActions.Add(action);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), new { parentType, parentId }, ToDto(action));
    }

    // PUT /api/next-actions/{id}
    [HttpPut("api/next-actions/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNextActionDto dto)
    {
        var action = await _db.NextActions.FirstOrDefaultAsync(a => a.Id == id);
        if (action == null || await ResolveOwnerAsync(action.LeadId, action.ClientId) == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Title)) action.Title = dto.Title.Trim();
        if (dto.DueAt.HasValue) action.DueAt = dto.DueAt.Value.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(dto.AssignedToUserId)) action.AssignedToUserId = dto.AssignedToUserId;

        await _db.SaveChangesAsync();
        return Ok(ToDto(action));
    }

    // POST /api/next-actions/{id}/complete
    [HttpPost("api/next-actions/{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id)
    {
        var action = await _db.NextActions.FirstOrDefaultAsync(a => a.Id == id);
        if (action == null || await ResolveOwnerAsync(action.LeadId, action.ClientId) == null) return NotFound();

        action.DoneAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(action));
    }

    // DELETE /api/next-actions/{id}
    [HttpDelete("api/next-actions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var action = await _db.NextActions.FirstOrDefaultAsync(a => a.Id == id);
        if (action == null || await ResolveOwnerAsync(action.LeadId, action.ClientId) == null) return NotFound();

        _db.NextActions.Remove(action);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/next-actions/today
    // Open actions assigned to me that are due by the end of today (Dutch time), including overdue.
    // Each row carries name and phone so the call can start without opening the lead.
    [HttpGet("api/next-actions/today")]
    public async Task<IActionResult> Today([FromQuery] DateTime? now = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var utcNow = (now ?? DateTime.UtcNow).ToUniversalTime();
        var endOfToday = EndOfDayNl(utcNow);

        var actions = await _db.NextActions
            .Where(a => a.AssignedToUserId == userId && a.DoneAt == null && a.DueAt <= endOfToday)
            .OrderBy(a => a.DueAt)
            .ToListAsync();

        var leadIds = actions.Where(a => a.LeadId.HasValue).Select(a => a.LeadId!.Value).Distinct().ToList();
        var clientIds = actions.Where(a => a.ClientId.HasValue).Select(a => a.ClientId!.Value).Distinct().ToList();

        // Only parents the caller owns: same rule as every other CRM endpoint.
        var leads = await _db.Leads
            .Where(l => leadIds.Contains(l.Id) && l.ImportedByUserId == userId)
            .Select(l => new { l.Id, l.Name, l.Phone, l.OwnerMobile })
            .ToDictionaryAsync(l => l.Id);
        var clients = await _db.Clients
            .Where(c => clientIds.Contains(c.Id) && c.CreatedByUserId == userId)
            .Select(c => new { c.Id, c.Name, c.PrimaryContactPhone })
            .ToDictionaryAsync(c => c.Id);

        var result = new List<TodayActionDto>();
        foreach (var a in actions)
        {
            if (a.LeadId.HasValue && leads.TryGetValue(a.LeadId.Value, out var l))
                result.Add(new TodayActionDto(a.Id, "leads", l.Id, l.Name,
                    string.IsNullOrWhiteSpace(l.OwnerMobile) ? l.Phone : l.OwnerMobile,
                    a.Title, a.DueAt, a.DueAt < utcNow));
            else if (a.ClientId.HasValue && clients.TryGetValue(a.ClientId.Value, out var c))
                result.Add(new TodayActionDto(a.Id, "clients", c.Id, c.Name, c.PrimaryContactPhone,
                    a.Title, a.DueAt, a.DueAt < utcNow));
        }
        return Ok(result);
    }

    /// <summary>End of the current calendar day in the Netherlands, as UTC.</summary>
    public static DateTime EndOfDayNl(DateTime utcNow)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var endLocal = local.Date.AddDays(1).AddTicks(-1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), tz);
    }
}

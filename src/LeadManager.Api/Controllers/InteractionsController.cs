using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

/// <summary>Conversation history (calls, mails, WhatsApp, meetings, notes) per lead or client.</summary>
public class InteractionsController : CrmControllerBase
{
    private readonly LeadManagerDbContext _db;

    public InteractionsController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static InteractionDto ToDto(Interaction i) => new(
        i.Id, i.LeadId, i.ClientId, i.ContactId, i.Kind.ToString(), i.Direction.ToString(),
        i.OccurredAt, i.Summary, i.Outcome, i.SourceRef, i.CreatedByUserId, i.CreatedAt);

    // GET /api/leads/{id}/interactions  |  /api/clients/{id}/interactions
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/interactions")]
    public async Task<IActionResult> List(string parentType, Guid parentId)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        var items = await _db.Interactions
            .Where(i => i.LeadId == parent.Value.LeadId && i.ClientId == parent.Value.ClientId)
            .OrderByDescending(i => i.OccurredAt)
            .ToListAsync();

        return Ok(items.Select(ToDto));
    }

    // POST /api/leads/{id}/interactions  |  /api/clients/{id}/interactions
    [HttpPost($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/interactions")]
    public async Task<IActionResult> Create(string parentType, Guid parentId, [FromBody] CreateInteractionDto dto)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();

        var summary = Clean(dto.Summary);
        var outcome = Clean(dto.Outcome);
        var sourceRef = Clean(dto.SourceRef);

        if (summary == null) return BadRequest("Summary is required");
        if (TooLong(summary, MaxSummary)) return BadRequest($"Summary is too long (max {MaxSummary})");
        if (TooLong(outcome, MaxOutcome)) return BadRequest($"Outcome is too long (max {MaxOutcome})");
        if (TooLong(sourceRef, MaxSourceRef)) return BadRequest($"SourceRef is too long (max {MaxSourceRef})");
        if (!TryParseName<InteractionKind>(dto.Kind, out var kind))
            return BadRequest($"Invalid kind: {dto.Kind}");

        var direction = kind == InteractionKind.Note ? InteractionDirection.Internal : InteractionDirection.Outbound;
        if (!string.IsNullOrWhiteSpace(dto.Direction) && !TryParseName(dto.Direction, out direction))
            return BadRequest($"Invalid direction: {dto.Direction}");

        if (dto.ContactId.HasValue)
        {
            var contactOk = await _db.Contacts.AnyAsync(c =>
                c.Id == dto.ContactId && c.LeadId == parent.Value.LeadId && c.ClientId == parent.Value.ClientId);
            if (!contactOk) return BadRequest("Contact does not belong to this lead or client");
        }

        // Idempotent import: the same source item is returned, not logged twice. The reference is
        // trimmed first, so " abc" and "abc" are the same item.
        if (sourceRef != null)
        {
            var existing = await FindBySourceRef(parent.Value, sourceRef);
            if (existing != null) return Ok(ToDto(existing));
        }

        var interaction = new Interaction
        {
            LeadId = parent.Value.LeadId,
            ClientId = parent.Value.ClientId,
            ContactId = dto.ContactId,
            Kind = kind,
            Direction = direction,
            OccurredAt = UtcDateTimeConverter.Normalize(dto.OccurredAt ?? DateTime.UtcNow),
            Summary = summary,
            Outcome = outcome,
            SourceRef = sourceRef,
            CreatedByUserId = GetCurrentUserId(),
        };
        _db.Interactions.Add(interaction);

        // First outbound touch moves a fresh lead on the pipeline.
        if (parent.Value.Kind == CrmParentKind.Lead
            && interaction.Direction == InteractionDirection.Outbound
            && kind is InteractionKind.Call or InteractionKind.Email or InteractionKind.WhatsApp)
        {
            var lead = await _db.Leads.FirstAsync(l => l.Id == parent.Value.Id);
            if (lead.PipelineStatus == PipelineStatus.New) lead.PipelineStatus = PipelineStatus.Contacted;
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (sourceRef != null)
        {
            // Two retries raced past the lookup above; the unique index let one of them through.
            // Return the row that won instead of a 500.
            _db.ChangeTracker.Clear();
            var winner = await FindBySourceRef(parent.Value, sourceRef);
            if (winner != null) return Ok(ToDto(winner));
            throw;
        }

        return CreatedAtAction(nameof(List), new { parentType, parentId }, ToDto(interaction));
    }

    private Task<Interaction?> FindBySourceRef(CrmParent parent, string sourceRef) =>
        _db.Interactions.FirstOrDefaultAsync(i =>
            i.LeadId == parent.LeadId && i.ClientId == parent.ClientId && i.SourceRef == sourceRef);

    // DELETE /api/interactions/{id}
    [HttpDelete("api/interactions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var interaction = await _db.Interactions.FirstOrDefaultAsync(i => i.Id == id);
        if (interaction == null) return NotFound();
        if (await ResolveOwnerAsync(interaction.LeadId, interaction.ClientId) == null) return NotFound();

        _db.Interactions.Remove(interaction);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/leads/{id}/timeline  |  /api/clients/{id}/timeline
    // One list for the lead screen: interactions plus the older system activities.
    [HttpGet($"api/{{parentType:{CrmParentAccess.RouteConstraint}}}/{{parentId:guid}}/timeline")]
    public async Task<IActionResult> Timeline(string parentType, Guid parentId, [FromQuery] int take = 200)
    {
        var parent = await ResolveParentAsync(parentType, parentId);
        if (parent == null) return NotFound();
        take = Math.Clamp(take, 1, 500);

        var interactions = await _db.Interactions
            .Where(i => i.LeadId == parent.Value.LeadId && i.ClientId == parent.Value.ClientId)
            .OrderByDescending(i => i.OccurredAt).Take(take)
            .ToListAsync();

        var items = interactions
            .Select(i => new TimelineItemDto("interaction", i.Id, i.Kind.ToString(), i.Direction.ToString(),
                i.OccurredAt, i.Summary, i.Outcome, i.SourceRef))
            .ToList();

        if (parent.Value.Kind == CrmParentKind.Lead)
        {
            var activities = await _db.Activities
                .Where(a => a.LeadId == parent.Value.Id)
                .OrderByDescending(a => a.CreatedAt).Take(take)
                .ToListAsync();
            items.AddRange(activities.Select(a => new TimelineItemDto("activity", a.Id,
                a.ActivityType.ToString(), null, a.CreatedAt, a.Note ?? "", null, null)));
        }

        return Ok(items.OrderByDescending(i => i.At).Take(take));
    }
}

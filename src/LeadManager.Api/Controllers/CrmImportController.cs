using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Bulk import of leads with their contacts, history and next actions, e.g. a cold-call list
/// built from mail. Safe to run twice: leads are matched on website domain, interactions on
/// SourceRef (or kind + time + summary), contacts on email, next actions on title and due date.
/// dryRun=true reports what would happen and saves nothing.
/// </summary>
[Route("api/crm/import")]
public class CrmImportController : CrmControllerBase
{
    private const int MaxLeads = 500;
    private const int MaxContactsPerLead = 50;
    private const int MaxInteractionsPerLead = 500;
    private const int MaxActionsPerLead = 50;
    private const int MaxChildrenInTotal = 10_000;

    private readonly LeadManagerDbContext _db;

    public CrmImportController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    /// <summary>What is already known about one lead during this import: database rows plus rows added so far.</summary>
    private sealed class LeadState
    {
        public required Lead Lead { get; init; }
        public required List<Contact> Contacts { get; init; }
        public required List<Interaction> Interactions { get; init; }
        public required List<NextAction> Actions { get; init; }
        public string Action { get; set; } = "matched";
        public int ContactsCreated, InteractionsCreated, InteractionsSkipped, ActionsCreated;
        public List<string> Problems { get; } = new();
    }

    /// <summary>"hogenkamp.nl" becomes "https://hogenkamp.nl"; anything but http(s) is refused.</summary>
    internal static bool TryNormalizeWebsite(string? input, out string? website)
    {
        website = null;
        var s = Clean(input);
        if (s == null) return true; // no website is fine
        if (!s.Contains("://") && !s.Contains(':')) s = "https://" + s;
        if (!IsSafeHttpUrl(s)) return false;
        website = s;
        return true;
    }

    [HttpPost]
    public async Task<IActionResult> Import([FromBody] ImportRequestDto request, [FromQuery] bool dryRun = true)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (request?.Leads == null || request.Leads.Count == 0) return BadRequest("No leads supplied");
        if (request.Leads.Count > MaxLeads) return BadRequest($"At most {MaxLeads} leads per import");

        var children = request.Leads.Sum(l => (l.Contacts?.Count ?? 0) + (l.Interactions?.Count ?? 0) + (l.NextActions?.Count ?? 0));
        if (children > MaxChildrenInTotal) return BadRequest($"At most {MaxChildrenInTotal} contacts, interactions and actions per import");

        var existingLeads = await _db.Leads.Where(l => l.ImportedByUserId == userId).ToListAsync();
        var states = new Dictionary<Guid, LeadState>();
        var order = new List<object>(); // either a LeadState or an invalid-row result, in payload order

        foreach (var item in request.Leads)
        {
            var name = Clean(item.Name);
            if (name == null)
            {
                order.Add(new ImportLeadResultDto("", "invalid", null, 0, 0, 0, 0, new List<string> { "Name is required" }));
                continue;
            }
            var rowProblems = new List<string>();
            if (TooLong(name, MaxName)) rowProblems.Add($"Name is too long (max {MaxName})");
            if (!TryNormalizeWebsite(item.Website, out var website))
                rowProblems.Add("Website must be an http or https URL");
            if ((item.Contacts?.Count ?? 0) > MaxContactsPerLead) rowProblems.Add($"At most {MaxContactsPerLead} contacts per lead");
            if ((item.Interactions?.Count ?? 0) > MaxInteractionsPerLead) rowProblems.Add($"At most {MaxInteractionsPerLead} interactions per lead");
            if ((item.NextActions?.Count ?? 0) > MaxActionsPerLead) rowProblems.Add($"At most {MaxActionsPerLead} next actions per lead");
            if (rowProblems.Count > 0)
            {
                order.Add(new ImportLeadResultDto(name, "invalid", null, 0, 0, 0, 0, rowProblems));
                continue;
            }

            var domain = CrmSearchController.NormalizeDomain(website);
            var lead = existingLeads.FirstOrDefault(l =>
                (domain != null && CrmSearchController.NormalizeDomain(l.Website) == domain) ||
                (domain == null && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)));

            var created = false;
            if (lead == null)
            {
                lead = new Lead
                {
                    Name = name,
                    Website = website ?? "",
                    Sector = Clean(item.Sector) ?? "",
                    City = Clean(item.City) ?? "",
                    Phone = Clean(item.Phone) ?? "",
                    CompanyEmail = Clean(item.Email) ?? "",
                    Source = Clean(item.Source) ?? "import",
                    ImportedByUserId = userId,
                };
                _db.Leads.Add(lead);
                existingLeads.Add(lead);
                created = true;
            }

            // A second row for the same lead in this payload continues from the first one's state,
            // instead of looking in the database again and missing what was just staged.
            if (!states.TryGetValue(lead.Id, out var state))
            {
                state = new LeadState
                {
                    Lead = lead,
                    Action = created ? "created" : "matched",
                    // Existing leads: everything already stored, including completed actions.
                    Contacts = created ? new() : await _db.Contacts.Where(c => c.LeadId == lead.Id).ToListAsync(),
                    Interactions = created ? new() : await _db.Interactions.Where(i => i.LeadId == lead.Id).ToListAsync(),
                    Actions = created ? new() : await _db.NextActions.Where(a => a.LeadId == lead.Id).ToListAsync(),
                };
                states[lead.Id] = state;
                order.Add(state);
            }
            else
            {
                // Same lead again: report the extra row as its own line so nothing is hidden.
                state = new LeadState
                {
                    Lead = lead, Action = "matched",
                    Contacts = states[lead.Id].Contacts,
                    Interactions = states[lead.Id].Interactions,
                    Actions = states[lead.Id].Actions,
                };
                state.Problems.Add("Same lead appeared earlier in this import; rows were merged");
                order.Add(state);
            }

            ImportContacts(item, state, userId);
            ImportInteractions(item, state, userId);
            ImportActions(item, state, userId);
        }

        if (dryRun)
        {
            // Nothing is saved: drop everything that was staged.
            _db.ChangeTracker.Clear();
        }
        else
        {
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                return Conflict("The import clashed with a concurrent change and nothing was saved. Run it again: it is safe to repeat.");
            }
        }

        var results = order.Select(o => o switch
        {
            ImportLeadResultDto invalid => invalid,
            LeadState s => new ImportLeadResultDto(s.Lead.Name, s.Action,
                dryRun && s.Action == "created" ? null : s.Lead.Id,
                s.ContactsCreated, s.InteractionsCreated, s.InteractionsSkipped, s.ActionsCreated, s.Problems),
            _ => throw new InvalidOperationException(),
        }).ToList();

        return Ok(new ImportReportDto(
            dryRun,
            results.Count(r => r.Action == "created"),
            results.Count(r => r.Action == "matched"),
            results));
    }

    private void ImportContacts(ImportLeadDto item, LeadState state, string userId)
    {
        foreach (var c in item.Contacts ?? new List<ImportContactDto>())
        {
            var name = Clean(c.Name);
            if (name == null) { state.Problems.Add("Contact without name skipped"); continue; }
            if (TooLong(name, MaxName) || TooLong(Clean(c.Role), MaxName) || TooLong(Clean(c.Email), MaxName) ||
                TooLong(Clean(c.Phone), MaxName) || TooLong(Clean(c.Mobile), MaxName))
            { state.Problems.Add($"Contact '{name[..Math.Min(name.Length, 40)]}' skipped: a field is too long"); continue; }

            var email = Clean(c.Email);
            var exists = state.Contacts.Any(k =>
                (email != null && string.Equals(k.Email, email, StringComparison.OrdinalIgnoreCase)) ||
                (email == null && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (exists) continue;

            var contact = new Contact
            {
                LeadId = state.Lead.Id, Name = name, Role = Clean(c.Role), Email = email,
                Phone = Clean(c.Phone), Mobile = Clean(c.Mobile), CreatedByUserId = userId,
            };
            _db.Contacts.Add(contact);
            state.Contacts.Add(contact);
            state.ContactsCreated++;
        }
    }

    private void ImportInteractions(ImportLeadDto item, LeadState state, string userId)
    {
        foreach (var i in item.Interactions ?? new List<ImportInteractionDto>())
        {
            var summary = Clean(i.Summary);
            if (summary == null) { state.Problems.Add("Interaction without summary skipped"); continue; }
            if (TooLong(summary, MaxSummary) || TooLong(Clean(i.Outcome), MaxOutcome) || TooLong(Clean(i.SourceRef), MaxSourceRef))
            { state.Problems.Add("Interaction skipped: a field is too long"); continue; }
            if (!TryParseName<InteractionKind>(i.Kind, out var kind)) { state.Problems.Add($"Unknown kind '{i.Kind}' skipped"); continue; }

            var direction = kind == InteractionKind.Note ? InteractionDirection.Internal : InteractionDirection.Outbound;
            if (!string.IsNullOrWhiteSpace(i.Direction) && !TryParseName(i.Direction, out direction))
            { state.Problems.Add($"Unknown direction '{i.Direction}' skipped"); continue; }

            var sourceRef = Clean(i.SourceRef);
            // Without a SourceRef the time is what identifies the row. Defaulting it to "now" would make
            // every re-run look new and double the history.
            if (sourceRef == null && !i.OccurredAt.HasValue)
            { state.Problems.Add("Interaction skipped: OccurredAt is required when there is no SourceRef"); continue; }

            var at = UtcDateTimeConverter.Normalize(i.OccurredAt ?? DateTime.UtcNow);
            var duplicate = state.Interactions.Any(k => sourceRef != null
                ? k.SourceRef == sourceRef
                : k.Kind == kind && k.OccurredAt == at && k.Summary == summary);
            if (duplicate) { state.InteractionsSkipped++; continue; }

            var interaction = new Interaction
            {
                LeadId = state.Lead.Id, Kind = kind, Direction = direction, OccurredAt = at,
                Summary = summary, Outcome = Clean(i.Outcome), SourceRef = sourceRef, CreatedByUserId = userId,
            };
            _db.Interactions.Add(interaction);
            state.Interactions.Add(interaction);
            state.InteractionsCreated++;
        }
    }

    private void ImportActions(ImportLeadDto item, LeadState state, string userId)
    {
        foreach (var a in item.NextActions ?? new List<ImportNextActionDto>())
        {
            var title = Clean(a.Title);
            if (title == null) { state.Problems.Add("Next action without title skipped"); continue; }
            if (TooLong(title, MaxTitle)) { state.Problems.Add("Next action skipped: title is too long"); continue; }

            var due = UtcDateTimeConverter.Normalize(a.DueAt);
            // Compared with ALL actions including completed ones: ticking an action off must not make
            // the next import bring it back as open.
            if (state.Actions.Any(k => k.Title == title && k.DueAt == due)) continue;

            var next = new NextAction
            {
                LeadId = state.Lead.Id, Title = title, DueAt = due,
                AssignedToUserId = userId, CreatedByUserId = userId,
            };
            _db.NextActions.Add(next);
            state.Actions.Add(next);
            state.ActionsCreated++;
        }
    }
}

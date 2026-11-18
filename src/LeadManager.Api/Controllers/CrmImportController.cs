using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Bulk import of leads with their contacts, history and next actions, e.g. a cold-call list
/// built from mail. Safe to run twice: leads are matched on website domain, interactions on
/// SourceRef, contacts on email, next actions on title and due date. dryRun=true reports what
/// would happen and saves nothing.
/// </summary>
[Route("api/crm/import")]
public class CrmImportController : CrmControllerBase
{
    private const int MaxLeads = 500;

    private readonly LeadManagerDbContext _db;

    public CrmImportController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [HttpPost]
    public async Task<IActionResult> Import([FromBody] ImportRequestDto request, [FromQuery] bool dryRun = true)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (request?.Leads == null || request.Leads.Count == 0) return BadRequest("No leads supplied");
        if (request.Leads.Count > MaxLeads) return BadRequest($"At most {MaxLeads} leads per import");

        var existingLeads = await _db.Leads.Where(l => l.ImportedByUserId == userId).ToListAsync();
        var results = new List<ImportLeadResultDto>();

        foreach (var item in request.Leads)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                results.Add(new ImportLeadResultDto("", "invalid", null, 0, 0, 0, 0, new List<string> { "Name is required" }));
                continue;
            }

            var domain = CrmSearchController.NormalizeDomain(item.Website);
            var lead = existingLeads.FirstOrDefault(l =>
                (domain != null && CrmSearchController.NormalizeDomain(l.Website) == domain) ||
                (domain == null && string.Equals(l.Name, item.Name.Trim(), StringComparison.OrdinalIgnoreCase)));

            var action = "matched";
            if (lead == null)
            {
                lead = new Lead
                {
                    Name = item.Name.Trim(),
                    Website = Clean(item.Website) ?? "",
                    Sector = Clean(item.Sector) ?? "",
                    City = Clean(item.City) ?? "",
                    Phone = Clean(item.Phone) ?? "",
                    CompanyEmail = Clean(item.Email) ?? "",
                    Source = Clean(item.Source) ?? "import",
                    ImportedByUserId = userId,
                };
                _db.Leads.Add(lead);
                existingLeads.Add(lead);
                action = "created";
            }

            // Contacts: matched on email (case-insensitive), else on name.
            var contactsCreated = 0;
            var knownContacts = action == "created"
                ? new List<Contact>()
                : await _db.Contacts.Where(c => c.LeadId == lead.Id).ToListAsync();
            foreach (var c in item.Contacts ?? new List<ImportContactDto>())
            {
                if (string.IsNullOrWhiteSpace(c.Name)) { problems.Add("Contact without name skipped"); continue; }
                var email = Clean(c.Email);
                var exists = knownContacts.Any(k =>
                    (email != null && string.Equals(k.Email, email, StringComparison.OrdinalIgnoreCase)) ||
                    (email == null && string.Equals(k.Name, c.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (exists) continue;

                var contact = new Contact
                {
                    LeadId = lead.Id,
                    Name = c.Name.Trim(), Role = Clean(c.Role), Email = email,
                    Phone = Clean(c.Phone), Mobile = Clean(c.Mobile), CreatedByUserId = userId,
                };
                _db.Contacts.Add(contact);
                knownContacts.Add(contact);
                contactsCreated++;
            }

            // Interactions: idempotent on SourceRef; entries without SourceRef are matched on
            // kind + time + summary so a re-run cannot double them.
            int created = 0, skipped = 0;
            var knownInteractions = action == "created"
                ? new List<Interaction>()
                : await _db.Interactions.Where(i => i.LeadId == lead.Id).ToListAsync();
            foreach (var i in item.Interactions ?? new List<ImportInteractionDto>())
            {
                if (string.IsNullOrWhiteSpace(i.Summary)) { problems.Add("Interaction without summary skipped"); continue; }
                if (!Enum.TryParse<InteractionKind>(i.Kind, true, out var kind)) { problems.Add($"Unknown kind '{i.Kind}' skipped"); continue; }
                var direction = kind == InteractionKind.Note ? InteractionDirection.Internal : InteractionDirection.Outbound;
                if (!string.IsNullOrWhiteSpace(i.Direction) && !Enum.TryParse(i.Direction, true, out direction))
                { problems.Add($"Unknown direction '{i.Direction}' skipped"); continue; }

                var at = (i.OccurredAt ?? DateTime.UtcNow).ToUniversalTime();
                var sourceRef = Clean(i.SourceRef);
                var duplicate = knownInteractions.Any(k => sourceRef != null
                    ? k.SourceRef == sourceRef
                    : k.Kind == kind && k.OccurredAt == at && k.Summary == i.Summary.Trim());
                if (duplicate) { skipped++; continue; }

                var interaction = new Interaction
                {
                    LeadId = lead.Id, Kind = kind, Direction = direction, OccurredAt = at,
                    Summary = i.Summary.Trim(), Outcome = Clean(i.Outcome), SourceRef = sourceRef,
                    CreatedByUserId = userId,
                };
                _db.Interactions.Add(interaction);
                knownInteractions.Add(interaction);
                created++;
            }

            var actionsCreated = 0;
            var knownActions = action == "created"
                ? new List<NextAction>()
                : await _db.NextActions.Where(a => a.LeadId == lead.Id && a.DoneAt == null).ToListAsync();
            foreach (var a in item.NextActions ?? new List<ImportNextActionDto>())
            {
                if (string.IsNullOrWhiteSpace(a.Title)) { problems.Add("Next action without title skipped"); continue; }
                var due = a.DueAt.ToUniversalTime();
                if (knownActions.Any(k => k.Title == a.Title.Trim() && k.DueAt == due)) continue;

                var next = new NextAction
                {
                    LeadId = lead.Id, Title = a.Title.Trim(), DueAt = due,
                    AssignedToUserId = userId, CreatedByUserId = userId,
                };
                _db.NextActions.Add(next);
                knownActions.Add(next);
                actionsCreated++;
            }

            results.Add(new ImportLeadResultDto(lead.Name, action, dryRun && action == "created" ? null : lead.Id,
                contactsCreated, created, skipped, actionsCreated, problems));
        }

        if (dryRun)
        {
            // Nothing is saved: drop everything that was staged.
            _db.ChangeTracker.Clear();
        }
        else
        {
            await _db.SaveChangesAsync();
        }

        return Ok(new ImportReportDto(
            dryRun,
            results.Count(r => r.Action == "created"),
            results.Count(r => r.Action == "matched"),
            results));
    }
}

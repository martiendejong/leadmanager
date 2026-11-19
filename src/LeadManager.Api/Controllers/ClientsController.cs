using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[Route("api/clients")]
[ApiController]
[Authorize]
[AllowApiKey]
public class ClientsController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly DocumentStorage? _storage;

    public ClientsController(LeadManagerDbContext db, DocumentStorage? storage = null)
    {
        _db = db;
        _storage = storage;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/clients
    [HttpGet]
    public async Task<IActionResult> GetClients()
    {
        var userId = GetCurrentUserId();
        var clients = await _db.Clients
            .Include(c => c.Projects)
            .Where(c => c.CreatedByUserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        var ids = clients.Select(c => c.Id).ToList();
        var nextActions = (await _db.NextActions
                .Where(a => a.ClientId != null && ids.Contains(a.ClientId.Value) && a.DoneAt == null)
                .GroupBy(a => a.ClientId!.Value)
                .Select(g => new { g.Key, At = g.Min(a => a.DueAt) })
                .ToListAsync())
            .ToDictionary(x => x.Key, x => x.At);
        var lastContact = (await _db.Interactions
                .Where(i => i.ClientId != null && ids.Contains(i.ClientId.Value))
                .GroupBy(i => i.ClientId!.Value)
                .Select(g => new { g.Key, At = g.Max(i => i.OccurredAt) })
                .ToListAsync())
            .ToDictionary(x => x.Key, x => x.At);

        var result = clients.Select(c => MapClientDto(c) with
        {
            NextActionAt = nextActions.TryGetValue(c.Id, out var n) ? UtcDateTimeConverter.Normalize(n) : null,
            LastContactAt = lastContact.TryGetValue(c.Id, out var l) ? UtcDateTimeConverter.Normalize(l) : null,
        });
        return Ok(result);
    }

    // GET /api/clients/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetClient(Guid id)
    {
        var userId = GetCurrentUserId();
        var client = await _db.Clients
            .Include(c => c.Projects)
            .FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == userId);

        if (client == null) return NotFound();
        return Ok(MapClientDto(client));
    }

    /// <summary>Shared by create and update: the same rules whoever calls (the app, an API key, an agent).</summary>
    private static string? CheckFields(string? rawName, string? rawWebsite, string? rawCategory, string? rawEmail, string? rawNotes,
        string?[] shortFields, out string name, out string? website, out string? category, out string? email, out string? notes)
    {
        name = CrmRules.Clean(rawName) ?? "";
        website = CrmRules.Clean(rawWebsite);
        category = CrmRules.Clean(rawCategory);
        email = CrmRules.Clean(rawEmail);
        notes = CrmRules.Clean(rawNotes);

        if (name == "") return "Naam is verplicht.";
        if (CrmRules.TooLong(name, CrmRules.MaxName)) return "Naam is te lang.";
        if (website != null && !website.Contains("://")) website = "https://" + website;
        if (website != null && !CrmRules.IsSafeHttpUrl(website)) return "Website moet een geldig http(s)-adres zijn.";
        if (category != null && !new[] { "Actief", "Beheercontract", "Reactief" }.Contains(category)) return "Onbekende omgangscategorie.";
        if (email != null && (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _))) return "E-mailadres is niet geldig.";
        if (shortFields.Select(CrmRules.Clean).Any(f => CrmRules.TooLong(f, CrmRules.MaxName)) || CrmRules.TooLong(notes, CrmRules.MaxNotes))
            return "Een veld is te lang.";
        return null;
    }

    // POST /api/clients  (a client that did not come from a lead conversion)
    [HttpPost]
    public async Task<IActionResult> CreateClient([FromBody] CreateClientDto dto)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var error = CheckFields(dto.Name, dto.Website, dto.EngagementCategory, dto.PrimaryContactEmail, dto.Notes,
            new[] { dto.Plan, dto.PrimaryContactName, dto.PrimaryContactPhone, dto.City, dto.Sector },
            out var name, out var website, out var category, out var email, out var notes);
        if (error != null) return BadRequest(new { error });

        // Same name for the same owner is almost always a double click, not a second client.
        var exists = await _db.Clients.AnyAsync(c => c.CreatedByUserId == userId && c.Name.ToLower() == name.ToLower());
        if (exists) return Conflict(new { error = "Je hebt al een klant met deze naam." });

        var projects = new List<Project>();
        foreach (var p in dto.Projects ?? new List<CreateClientProjectDto>())
        {
            var pn = CrmRules.Clean(p.Name);
            if (pn == null || CrmRules.TooLong(pn, CrmRules.MaxName)) return BadRequest(new { error = "Projectnaam ontbreekt of is te lang." });
            var pd = CrmRules.Clean(p.Description);
            if (CrmRules.TooLong(pd, CrmRules.MaxNotes)) return BadRequest(new { error = "Projectomschrijving is te lang." });
            var ps = CrmRules.Clean(p.Status) ?? "Active";
            if (!new[] { "Active", "Completed", "On Hold" }.Contains(ps)) return BadRequest(new { error = "Onbekende projectstatus." });
            projects.Add(new Project { Name = pn, Description = pd, Status = ps });
        }

        var client = new Client
        {
            Projects = projects,
            Name = name,
            Plan = CrmRules.Clean(dto.Plan),
            PrimaryContactName = CrmRules.Clean(dto.PrimaryContactName),
            PrimaryContactEmail = email,
            PrimaryContactPhone = CrmRules.Clean(dto.PrimaryContactPhone),
            City = CrmRules.Clean(dto.City),
            Sector = CrmRules.Clean(dto.Sector),
            Website = website,
            Notes = notes,
            EngagementCategory = category,
            CreatedByUserId = userId,
        };
        _db.Clients.Add(client);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetClient), new { id = client.Id }, MapClientDto(client));
    }

    // PUT /api/clients/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateClient(Guid id, [FromBody] UpdateClientDto dto)
    {
        var userId = GetCurrentUserId();
        var client = await _db.Clients
            .Include(c => c.Projects)
            .FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == userId);

        if (client == null) return NotFound();

        var error = CheckFields(dto.Name, dto.Website, dto.EngagementCategory, dto.PrimaryContactEmail, dto.Notes,
            new[] { dto.Plan, dto.PrimaryContactName, dto.PrimaryContactPhone, dto.City, dto.Sector },
            out var name, out var website, out var category, out var email, out var notes);
        if (error != null) return BadRequest(new { error });

        client.Name = name;
        client.Plan = CrmRules.Clean(dto.Plan);
        client.PrimaryContactName = CrmRules.Clean(dto.PrimaryContactName);
        client.PrimaryContactEmail = email;
        client.PrimaryContactPhone = CrmRules.Clean(dto.PrimaryContactPhone);
        client.City = CrmRules.Clean(dto.City);
        client.Sector = CrmRules.Clean(dto.Sector);
        client.Website = website;
        client.Notes = notes;
        client.EngagementCategory = category;

        await _db.SaveChangesAsync();
        return Ok(MapClientDto(client));
    }

    // PUT /api/clients/{id}/priority  (own endpoint, so the full-record PUT above cannot wipe the scores)
    [HttpPut("{id:guid}/priority")]
    public async Task<IActionResult> UpdatePriority(Guid id, [FromBody] UpdatePriorityDto dto)
    {
        var userId = GetCurrentUserId();
        var client = await _db.Clients.Include(c => c.Projects)
            .FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == userId);
        if (client == null) return NotFound();
        if (!ClientPriority.InRange(dto.Value) || !ClientPriority.InRange(dto.Urgency) || !ClientPriority.InRange(dto.Effort))
            return BadRequest(new { error = "Scores liggen tussen 1 en 144." });

        client.PriorityValue = ClientPriority.Snap(dto.Value);
        client.PriorityUrgency = ClientPriority.Snap(dto.Urgency);
        client.PriorityEffort = ClientPriority.Snap(dto.Effort);
        await _db.SaveChangesAsync();
        return Ok(MapClientDto(client));
    }

    // DELETE /api/clients/{id}
    [HttpDelete("{id:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> DeleteClient(Guid id)
    {
        var userId = GetCurrentUserId();
        var client = await _db.Clients
            .FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == userId);

        if (client == null) return NotFound();

        var files = await _db.ClientDocumentFiles
            .Where(f => _db.ClientDocuments.Any(d => d.Id == f.DocumentId && d.ClientId == id))
            .Select(f => f.StoragePath).ToListAsync();
        _db.Clients.Remove(client);
        await _db.SaveChangesAsync();
        // The rows cascade away with the client; the files on disk are removed here.
        if (_storage != null) foreach (var path in files) _storage.TryDelete(path);
        return NoContent();
    }

    internal static ClientDto MapClientDto(Client c) => new(
        c.Id,
        c.Name,
        c.Plan,
        c.PrimaryContactName,
        c.PrimaryContactEmail,
        c.PrimaryContactPhone,
        c.City,
        c.Sector,
        c.Website,
        c.Notes,
        c.EngagementCategory,
        c.SourceLeadId,
        c.CreatedByUserId,
        c.CreatedAt,
        c.IsActive,
        c.Projects.OrderBy(p => p.CreatedAt).Select(p => new ProjectDto(p.Id, p.Name, p.Description, p.Status, p.CreatedAt)).ToList(),
        c.PriorityValue, c.PriorityUrgency, c.PriorityEffort,
        ClientPriority.Score(c.PriorityValue, c.PriorityUrgency, c.PriorityEffort)
    );
}

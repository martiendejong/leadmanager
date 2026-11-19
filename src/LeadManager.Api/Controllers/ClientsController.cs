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
public class ClientsController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ClientsController(LeadManagerDbContext db)
    {
        _db = db;
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
            .Select(c => MapClientDto(c))
            .ToListAsync();

        return Ok(clients);
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

    // POST /api/clients  (a client that did not come from a lead conversion)
    [HttpPost]
    public async Task<IActionResult> CreateClient([FromBody] CreateClientDto dto)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var name = CrmRules.Clean(dto.Name);
        if (name == null) return BadRequest(new { error = "Naam is verplicht." });
        if (CrmRules.TooLong(name, CrmRules.MaxName)) return BadRequest(new { error = "Naam is te lang." });

        var website = CrmRules.Clean(dto.Website);
        if (website != null && !website.Contains("://")) website = "https://" + website;
        if (website != null && !CrmRules.IsSafeHttpUrl(website))
            return BadRequest(new { error = "Website moet een geldig http(s)-adres zijn." });

        var category = CrmRules.Clean(dto.EngagementCategory);
        if (category != null && !new[] { "Actief", "Beheercontract", "Reactief" }.Contains(category))
            return BadRequest(new { error = "Onbekende omgangscategorie." });

        var email = CrmRules.Clean(dto.PrimaryContactEmail);
        if (email != null && (!email.Contains('@') || email.Length > 320))
            return BadRequest(new { error = "E-mailadres is niet geldig." });

        var notes = CrmRules.Clean(dto.Notes);
        var fields = new[] { dto.Plan, dto.PrimaryContactName, dto.PrimaryContactPhone, dto.City, dto.Sector };
        if (fields.Any(f => CrmRules.TooLong(f, CrmRules.MaxName)) || CrmRules.TooLong(notes, CrmRules.MaxNotes))
            return BadRequest(new { error = "Een veld is te lang." });

        // Same name for the same owner is almost always a double click, not a second client.
        var exists = await _db.Clients.AnyAsync(c => c.CreatedByUserId == userId && c.Name.ToLower() == name.ToLower());
        if (exists) return Conflict(new { error = "Je hebt al een klant met deze naam." });

        var client = new Client
        {
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

        client.Name = dto.Name;
        client.Plan = dto.Plan;
        client.PrimaryContactName = dto.PrimaryContactName;
        client.PrimaryContactEmail = dto.PrimaryContactEmail;
        client.PrimaryContactPhone = dto.PrimaryContactPhone;
        client.City = dto.City;
        client.Sector = dto.Sector;
        client.Website = dto.Website;
        client.Notes = dto.Notes;
        client.EngagementCategory = dto.EngagementCategory;

        await _db.SaveChangesAsync();
        return Ok(MapClientDto(client));
    }

    // DELETE /api/clients/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteClient(Guid id)
    {
        var userId = GetCurrentUserId();
        var client = await _db.Clients
            .FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == userId);

        if (client == null) return NotFound();

        _db.Clients.Remove(client);
        await _db.SaveChangesAsync();
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
        c.Projects.Select(p => new ProjectDto(p.Id, p.Name, p.Description, p.Status, p.CreatedAt)).ToList()
    );
}

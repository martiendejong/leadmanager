using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
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
        c.SourceLeadId,
        c.CreatedByUserId,
        c.CreatedAt,
        c.IsActive,
        c.Projects.Select(p => new ProjectDto(p.Id, p.Name, p.Description, p.Status, p.CreatedAt)).ToList()
    );
}

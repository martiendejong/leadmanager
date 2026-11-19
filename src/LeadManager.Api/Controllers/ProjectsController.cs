using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LeadManager.Api.Services.CrmRules;

namespace LeadManager.Api.Controllers;

public record UpsertProjectDto(string Name, string? Description, string? Status);

/// <summary>Projects of a client. Their history is the client's interactions filtered by ProjectId.</summary>
[ApiController]
[Authorize]
[AllowApiKey]
public class ProjectsController : ControllerBase
{
    private static readonly string[] Statuses = ProjectStatuses.All;
    private readonly LeadManagerDbContext _db;

    public ProjectsController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static ProjectDto ToDto(Project p) => new(p.Id, p.Name, p.Description, p.Status, p.CreatedAt);

    private static string? Validate(UpsertProjectDto dto, out string name, out string? description, out string status)
    {
        name = Clean(dto.Name) ?? "";
        description = Clean(dto.Description);
        status = Clean(dto.Status) ?? "Active";
        if (name == "") return "Name is required.";
        if (TooLong(name, MaxName)) return "Name is too long.";
        if (TooLong(description, MaxNotes)) return "Description is too long.";
        if (!Statuses.Contains(status)) return "Onbekende status.";
        return null;
    }

    // POST /api/clients/{clientId}/projects
    [HttpPost("api/clients/{clientId:guid}/projects")]
    public async Task<IActionResult> Create(Guid clientId, [FromBody] UpsertProjectDto dto)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (!await _db.Clients.AnyAsync(c => c.Id == clientId && c.CreatedByUserId == userId)) return NotFound();

        var error = Validate(dto, out var name, out var description, out var status);
        if (error != null) return BadRequest(new { error });

        var project = new Project { ClientId = clientId, Name = name, Description = description, Status = status };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return Created($"api/projects/{project.Id}", ToDto(project));
    }

    private async Task<Project?> FindOwned(Guid id)
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return null;
        return await _db.Projects.FirstOrDefaultAsync(p => p.Id == id
            && _db.Clients.Any(c => c.Id == p.ClientId && c.CreatedByUserId == userId));
    }

    // PUT /api/projects/{id}
    [HttpPut("api/projects/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertProjectDto dto)
    {
        var project = await FindOwned(id);
        if (project == null) return NotFound();

        var error = Validate(dto, out var name, out var description, out var status);
        if (error != null) return BadRequest(new { error });

        project.Name = name;
        project.Description = description;
        project.Status = status;
        await _db.SaveChangesAsync();
        return Ok(ToDto(project));
    }

    // DELETE /api/projects/{id}  (its history stays on the client, without the project link)
    [HttpDelete("api/projects/{id:guid}")]
    [DenyApiKey]
    public async Task<IActionResult> Delete(Guid id)
    {
        var project = await FindOwned(id);
        if (project == null) return NotFound();
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

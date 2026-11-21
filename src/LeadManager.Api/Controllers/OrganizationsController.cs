using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

public record OrganizationDto(Guid Id, string Name, string Slug, string Role, int MemberCount, int LeadCount);
public record OrganizationMemberDto(Guid Id, string Email, string Role, bool Pending, string? Name, DateTime CreatedAt);
public record CreateOrganizationDto(string? Name);
public record AddMemberDto(string? Email, string? Role);
public record UpdateMemberDto(string? Role);

/// <summary>
/// The organisations of the caller and their members. Anyone can see the organisations they are in and who else is in them;
/// only an organisation admin changes the members. A person outside an organisation gets 404 for it, never its data.
/// </summary>
[ApiController]
[Route("api/organizations")]
[Authorize]
[AllowApiKey]
public class OrganizationsController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public OrganizationsController(LeadManagerDbContext db) => _db = db;

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/organizations : the organisations I am a member of
    [HttpGet]
    public async Task<IActionResult> Mine()
    {
        var userId = UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var mine = await _db.OrganizationMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => new { m.OrganizationId, m.Role, m.Organization!.Name, m.Organization.Slug }).ToListAsync();
        var ids = mine.Select(m => m.OrganizationId).ToList();
        var members = await _db.OrganizationMembers.AsNoTracking().Where(m => ids.Contains(m.OrganizationId))
            .GroupBy(m => m.OrganizationId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        var leads = await _db.Leads.AsNoTracking().Where(l => ids.Contains(l.OrganizationId))
            .GroupBy(l => l.OrganizationId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        return Ok(mine.OrderBy(m => m.Name).Select(m => new OrganizationDto(m.OrganizationId, m.Name, m.Slug, m.Role,
            members.GetValueOrDefault(m.OrganizationId), leads.GetValueOrDefault(m.OrganizationId))));
    }

    // POST /api/organizations : a new organisation (application admins only); the creator becomes its admin
    [DenyApiKey]
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrganizationDto dto)
    {
        var userId = UserId;
        var name = dto.Name?.Trim() ?? "";
        if (name.Length < 2 || name.Length > 100) return BadRequest(new { error = "The name is 2 to 100 characters." });
        var slug = Organizations.Slugify(name);
        if (await _db.Organizations.AnyAsync(o => o.Slug == slug || o.Name == name))
            return Conflict(new { error = "An organisation with this name already exists." });

        var org = new Organization { Name = name, Slug = slug };
        _db.Organizations.Add(org);
        var email = await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
        _db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = org.Id, UserId = userId, Email = Organizations.NormalizeEmail(email), Role = OrganizationRoles.Admin });
        await _db.SaveChangesAsync();
        return Created($"api/organizations/{org.Id}", new OrganizationDto(org.Id, org.Name, org.Slug, OrganizationRoles.Admin, 1, 0));
    }

    // DELETE /api/organizations/{id} : only an empty organisation (no leads, no hunts), by an application admin who is its admin
    [DenyApiKey]
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var denied = await RequireAdminAsync(id);
        if (denied != null) return denied;
        if (await _db.Leads.AnyAsync(l => l.OrganizationId == id) || await _db.HuntJobs.AnyAsync(j => j.OrganizationId == id))
            return Conflict(new { error = "Only an organisation without leads can be deleted. Move its leads first." });
        var org = await _db.Organizations.FirstAsync(o => o.Id == id);
        _db.Organizations.Remove(org); // its members go with it
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/organizations/{id}/members
    [HttpGet("{id:guid}/members")]
    public async Task<IActionResult> Members(Guid id)
    {
        if (!await Organizations.IsMemberAsync(_db, UserId, id)) return NotFound();
        var rows = await _db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == id)
            .OrderBy(m => m.Email).ToListAsync();
        var userIds = rows.Where(m => m.UserId != null).Select(m => m.UserId!).ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName }).ToDictionaryAsync(u => u.Id, u => (u.FirstName + " " + u.LastName).Trim());
        return Ok(rows.Select(m => new OrganizationMemberDto(m.Id, m.Email, m.Role, m.UserId == null,
            m.UserId != null && names.TryGetValue(m.UserId, out var n) && n.Length > 0 ? n : null, m.CreatedAt)));
    }

    // POST /api/organizations/{id}/members : add by e-mail (organisation admins). Without an account yet the membership is pending
    // and is attached when that person first logs in.
    [DenyApiKey]
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberDto dto)
    {
        var denied = await RequireAdminAsync(id);
        if (denied != null) return denied;
        var email = Organizations.NormalizeEmail(dto.Email);
        if (!Organizations.LooksLikeEmail(email)) return BadRequest(new { error = "Enter a valid e-mail address." });
        var role = string.IsNullOrWhiteSpace(dto.Role) ? OrganizationRoles.Member : dto.Role.Trim().ToLowerInvariant();
        if (!OrganizationRoles.IsValid(role)) return BadRequest(new { error = "Role is admin or member." });

        var existed = await _db.OrganizationMembers.AnyAsync(m => m.OrganizationId == id && m.Email == email);
        var member = await Organizations.AddOrUpdateMemberAsync(_db, id, email, role);
        var dtoOut = new OrganizationMemberDto(member.Id, member.Email, member.Role, member.UserId == null, null, member.CreatedAt);
        return existed ? Ok(dtoOut) : Created($"api/organizations/{id}/members/{member.Id}", dtoOut);
    }

    // PUT /api/organizations/{id}/members/{memberId} : change the role
    [DenyApiKey]
    [HttpPut("{id:guid}/members/{memberId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid id, Guid memberId, [FromBody] UpdateMemberDto dto)
    {
        var denied = await RequireAdminAsync(id);
        if (denied != null) return denied;
        var role = dto.Role?.Trim().ToLowerInvariant();
        if (!OrganizationRoles.IsValid(role)) return BadRequest(new { error = "Role is admin or member." });
        var member = await _db.OrganizationMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == id);
        if (member == null) return NotFound();
        if (member.Role == OrganizationRoles.Admin && role != OrganizationRoles.Admin && await IsLastAdminAsync(id, member))
            return Conflict(new { error = "An organisation needs at least one admin." });
        member.Role = role!;
        await _db.SaveChangesAsync();
        return Ok(new OrganizationMemberDto(member.Id, member.Email, member.Role, member.UserId == null, null, member.CreatedAt));
    }

    // DELETE /api/organizations/{id}/members/{memberId}
    [DenyApiKey]
    [HttpDelete("{id:guid}/members/{memberId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid memberId)
    {
        var denied = await RequireAdminAsync(id);
        if (denied != null) return denied;
        var member = await _db.OrganizationMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.OrganizationId == id);
        if (member == null) return NotFound();
        if (member.Role == OrganizationRoles.Admin && await IsLastAdminAsync(id, member))
            return Conflict(new { error = "An organisation needs at least one admin." });
        _db.OrganizationMembers.Remove(member);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>404 for a person outside the organisation (it does not exist for them), 403 for a member who is not an admin.</summary>
    private async Task<IActionResult?> RequireAdminAsync(Guid organizationId)
    {
        var userId = UserId;
        if (!await Organizations.IsMemberAsync(_db, userId, organizationId)) return NotFound();
        if (!await Organizations.IsAdminAsync(_db, userId, organizationId))
            return StatusCode(403, new { error = "Only an organisation admin can change the members." });
        return null;
    }

    private async Task<bool> IsLastAdminAsync(Guid organizationId, OrganizationMember member)
        => !await _db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organizationId && m.Role == OrganizationRoles.Admin && m.Id != member.Id);
}

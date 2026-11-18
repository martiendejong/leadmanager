using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// Manage API keys for agents and other Jengo tools. Deliberately JWT only: an API key
/// can never mint or revoke keys, so a leaked key cannot create a persistent backdoor.
/// </summary>
[Route("api/api-keys")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ApiKeysController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ApiKeysController(LeadManagerDbContext db) => _db = db;

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/api-keys
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = GetCurrentUserId();
        var keys = await _db.ApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
        return Ok(keys.Select(k => new ApiKeyDto(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt)));
    }

    // POST /api/api-keys  -> returns the key ONCE
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyDto dto)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required");

        var key = ApiKeyDefaults.Generate();
        var record = new ApiKey
        {
            Name = dto.Name.Trim(),
            KeyHash = ApiKeyDefaults.Hash(key),
            Prefix = key[..Math.Min(key.Length, 8)],
            UserId = userId,
        };
        _db.ApiKeys.Add(record);
        await _db.SaveChangesAsync();

        return Ok(new CreatedApiKeyDto(record.Id, record.Name, record.Prefix, key));
    }

    // DELETE /api/api-keys/{id}  -> revoke
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var userId = GetCurrentUserId();
        var record = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
        if (record == null) return NotFound();

        record.RevokedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

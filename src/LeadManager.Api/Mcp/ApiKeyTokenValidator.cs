using JengoComponents.McpConnector;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Mcp;

/// <summary>
/// The access token of the /mcp endpoint IS one of the user's API keys (the "API-key table" backend of the
/// shared connector). Same rules as X-Api-Key: only the hash is compared, a revoked key or a deactivated
/// user is refused, and the key acts as the user who created it.
/// </summary>
public sealed class ApiKeyTokenValidator : IMcpTokenValidator
{
    private readonly LeadManagerDbContext _db;

    public ApiKeyTokenValidator(LeadManagerDbContext db) => _db = db;

    public async ValueTask<McpPrincipal?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (!token.StartsWith(ApiKeyDefaults.KeyPrefix, StringComparison.Ordinal)) return null;

        var hash = ApiKeyDefaults.Hash(token);
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key == null) return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == key.UserId && u.IsActive, ct);
        if (user == null) return null;

        var now = DateTime.UtcNow;
        if (key.LastUsedAt == null || now - key.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            key.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return new McpPrincipal
        {
            Subject = user.Id,
            Name = $"{user.FirstName} {user.LastName}".Trim(),
            Email = user.Email,
            RawToken = token,
            Attachment = key,
        };
    }
}

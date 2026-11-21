using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services;

/// <summary>
/// Puts the people of deploy/org_members.json (embedded in the app; Organizations:MembersFile points at another file) into their
/// organisations at start. Each (organisation, e-mail) pair is applied once: it is remembered in OrganizationSeedLog, so a member
/// that an organisation admin removed in the app is not added back by the next start.
/// A person without an account yet gets a pending membership by e-mail, attached at their first login.
/// </summary>
public static class OrganizationSeeder
{
    public sealed record SeedMember(string? Name, string? Email, string? Role, List<string>? Orgs);
    public sealed record SeedFile(List<SeedMember>? Members);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public static string? ReadSource(IConfiguration config)
    {
        var path = config["Organizations:MembersFile"];
        if (!string.IsNullOrWhiteSpace(path)) return File.Exists(path) ? File.ReadAllText(path) : null;
        using var stream = typeof(OrganizationSeeder).Assembly.GetManifestResourceStream("org_members.json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static async Task<int> SeedAsync(LeadManagerDbContext db, string? json, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        SeedFile? file;
        try { file = JsonSerializer.Deserialize<SeedFile>(json, Options); }
        catch (JsonException ex)
        {
            logger.LogError(ex, "org_members.json is not valid JSON: no memberships were seeded.");
            return 0;
        }

        var orgs = await db.Organizations.AsNoTracking().ToListAsync();
        var applied = (await db.OrganizationSeedLog.AsNoTracking().Select(x => x.Key).ToListAsync()).ToHashSet();
        var count = 0;

        foreach (var person in file?.Members ?? new())
        {
            var email = Organizations.NormalizeEmail(person.Email);
            if (!Organizations.LooksLikeEmail(email))
            {
                logger.LogWarning("org_members.json: skipped an entry without a valid e-mail address ({Name}).", person.Name);
                continue;
            }
            var role = string.IsNullOrWhiteSpace(person.Role) ? OrganizationRoles.Member : person.Role.Trim().ToLowerInvariant();
            if (!OrganizationRoles.IsValid(role))
            {
                logger.LogWarning("org_members.json: {Email} has role '{Role}' (admin or member): skipped.", email, person.Role);
                continue;
            }
            foreach (var orgName in person.Orgs ?? new())
            {
                var org = orgs.FirstOrDefault(o => string.Equals(o.Name, orgName, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(o.Slug, orgName, StringComparison.OrdinalIgnoreCase));
                if (org == null)
                {
                    logger.LogWarning("org_members.json: organisation '{Org}' does not exist ({Email} skipped for it).", orgName, email);
                    continue;
                }
                var key = $"member:{org.Id}:{email}";
                if (applied.Contains(key)) continue;

                // An existing membership (added in the app) keeps its role; the seed only fills what is missing.
                var exists = await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == org.Id && m.Email == email);
                if (!exists) await Organizations.AddOrUpdateMemberAsync(db, org.Id, email, role);
                db.OrganizationSeedLog.Add(new OrganizationSeedLog { Key = key });
                await db.SaveChangesAsync();
                applied.Add(key);
                count++;
            }
        }
        if (count > 0) logger.LogInformation("Organisation memberships seeded: {Count}.", count);
        return count;
    }
}

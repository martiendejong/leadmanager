using System.Text.RegularExpressions;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services;

/// <summary>
/// The single place that decides which leads a person may see: the leads of the organisations they are a member of.
/// Every lead query goes through <see cref="VisibleTo"/>, so a person outside an organisation never gets its data
/// (a lookup by id answers 404, a list is empty). API keys act as their user and follow the same rules.
/// </summary>
public static class Organizations
{
    /// <summary>Leads of the organisations <paramref name="userId"/> is a member of. No user = nothing.</summary>
    public static IQueryable<Lead> VisibleTo(this IQueryable<Lead> leads, LeadManagerDbContext db, string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return leads.Where(_ => false);
        return leads.Where(l => db.OrganizationMembers.Any(m => m.UserId == userId && m.OrganizationId == l.OrganizationId));
    }

    /// <summary>Narrows to the given organisations. Null or empty = no narrowing (all that the person may see).</summary>
    public static IQueryable<Lead> InOrganizations(this IQueryable<Lead> leads, IReadOnlyCollection<Guid>? organizationIds)
        => organizationIds == null || organizationIds.Count == 0 ? leads : leads.Where(l => organizationIds.Contains(l.OrganizationId));

    public static Task<List<Guid>> MyOrganizationIdsAsync(LeadManagerDbContext db, string? userId, CancellationToken ct = default)
        => string.IsNullOrEmpty(userId)
            ? Task.FromResult(new List<Guid>())
            : db.OrganizationMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.OrganizationId).Distinct().ToListAsync(ct);

    public static Task<bool> IsMemberAsync(LeadManagerDbContext db, string? userId, Guid organizationId)
        => string.IsNullOrEmpty(userId)
            ? Task.FromResult(false)
            : db.OrganizationMembers.AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId);

    public static Task<bool> IsAdminAsync(LeadManagerDbContext db, string? userId, Guid organizationId)
        => string.IsNullOrEmpty(userId)
            ? Task.FromResult(false)
            : db.OrganizationMembers.AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.Role == OrganizationRoles.Admin);

    /// <summary>
    /// Parses the organisation filter of a request: comma separated ids or slugs (or names), only organisations the person is
    /// a member of. An entry that matches no organisation of theirs yields <see cref="Guid.Empty"/>, so the filter then matches
    /// nothing instead of silently widening to everything.
    /// </summary>
    public static async Task<List<Guid>?> ParseFilterAsync(LeadManagerDbContext db, string? userId, string? raw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var mine = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == userId && userId != null)
            .Select(m => m.Organization!).ToListAsync(ct);
        var result = new List<Guid>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = mine.FirstOrDefault(o =>
                (Guid.TryParse(part, out var g) && o.Id == g)
                || string.Equals(o.Slug, part, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Name, part, StringComparison.OrdinalIgnoreCase));
            result.Add(match?.Id ?? Guid.Empty);
        }
        return result.Count == 0 ? null : result.Distinct().ToList();
    }

    /// <summary>
    /// The organisation a new lead or hunt goes to. Explicit choice: the person must be a member. No choice: the only
    /// organisation of the person; with several, the caller has to choose.
    /// </summary>
    public static async Task<(Guid? Id, string? Error)> ResolveTargetAsync(LeadManagerDbContext db, string? userId, Guid? requested)
    {
        var mine = await MyOrganizationIdsAsync(db, userId);
        if (mine.Count == 0)
            return (null, "You are not a member of any organisation yet. Ask an organisation admin to add you.");
        if (requested.HasValue && requested.Value != Guid.Empty)
            return mine.Contains(requested.Value)
                ? (requested.Value, null)
                : (null, "You are not a member of that organisation.");
        if (mine.Count == 1) return (mine[0], null);
        return (null, "Choose an organisation (organizationId): you are a member of several.");
    }

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    public static bool LooksLikeEmail(string email) => email.Length <= 254 && EmailShape.IsMatch(email);

    public static string Slugify(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "organisation" : slug;
    }

    /// <summary>
    /// Memberships added by e-mail before the person had an account are attached to the user at their first login.
    /// Returns how many were attached. Safe to call on every login.
    /// </summary>
    public static async Task<int> AttachPendingAsync(LeadManagerDbContext db, string userId, string? email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0) return 0;
        var pending = await db.OrganizationMembers.Where(m => m.UserId == null && m.Email == normalized).ToListAsync();
        foreach (var m in pending) m.UserId = userId;
        if (pending.Count > 0) await db.SaveChangesAsync();
        return pending.Count;
    }

    /// <summary>Adds (or updates the role of) a member by e-mail. Resolves the user at once when they already have an account.</summary>
    public static async Task<OrganizationMember> AddOrUpdateMemberAsync(LeadManagerDbContext db, Guid organizationId, string email, string role)
    {
        var normalized = NormalizeEmail(email);
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.Email == normalized);
        var userId = await db.Users.Where(u => u.NormalizedEmail == normalized.ToUpperInvariant()).Select(u => u.Id).FirstOrDefaultAsync();
        if (member == null)
        {
            member = new OrganizationMember { OrganizationId = organizationId, Email = normalized, Role = role, UserId = userId };
            db.OrganizationMembers.Add(member);
        }
        else
        {
            member.Role = role;
            member.UserId ??= userId;
        }
        await db.SaveChangesAsync();
        return member;
    }
}

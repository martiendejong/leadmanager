namespace LeadManager.Api.Models;

/// <summary>
/// One of our own organisations (brands, projects, companies). Leads belong to exactly one organisation and are
/// visible to its members only. See docs/ORGANISATIONS.md.
/// </summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Lower case, url safe, unique. Used by agents and the API as a stable name ("port-of-giethoorn").</summary>
    public string Slug { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class OrganizationRoles
{
    public const string Admin = "admin";
    public const string Member = "member";

    public static bool IsValid(string? role) => role == Admin || role == Member;
}

/// <summary>
/// A person in an organisation, identified by e-mail so the membership can exist before that person has logged in
/// once (UserId is empty until then; it is filled when the user first logs in, see OrganizationService.AttachPendingAsync).
/// </summary>
public class OrganizationMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    /// <summary>The local user, once known. Null = pending membership by e-mail.</summary>
    public string? UserId { get; set; }
    /// <summary>Lower case, trimmed. Unique per organisation.</summary>
    public string Email { get; set; } = "";
    /// <summary>"admin" (manages members) or "member".</summary>
    public string Role { get; set; } = OrganizationRoles.Member;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Remembers which seeded memberships were applied once, so a member an admin removed in the app is not put back
/// by the next start of the seeder.
/// </summary>
public class OrganizationSeedLog
{
    public string Key { get; set; } = "";
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}

using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

/// <summary>
/// "Who is this?" lookup for other Jengo tools: JengoMail resolves a sender, Meet resolves an
/// attendee, an agent resolves a company name. Returns the leads and clients the caller owns.
/// </summary>
[Route("api/crm")]
public class CrmSearchController : CrmControllerBase
{
    private const int MaxResults = 25;
    private const int Candidates = 200;

    // Domains shared by many unrelated people; never match a company website on these.
    private static readonly HashSet<string> FreeMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.nl", "live.nl", "live.com",
        "yahoo.com", "icloud.com", "me.com", "ziggo.nl", "kpnmail.nl", "home.nl", "planet.nl", "xs4all.nl",
        "proton.me", "protonmail.com",
    };

    private readonly LeadManagerDbContext _db;

    public CrmSearchController(LeadManagerDbContext db, CrmParentAccess access) : base(access) => _db = db;

    /// <summary>"https://www.Example.nl/pad" or "info@example.nl" to "example.nl".</summary>
    public static string? NormalizeDomain(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant();
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var cut = s.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (cut >= 0) s = s[..cut];
        if (s.StartsWith("www.")) s = s[4..];
        return s.Contains('.') ? s : null;
    }

    // GET /api/crm/find?email=&domain=&q=
    // The filtering happens in SQL on a narrow projection: a lead row carries large AI text fields and
    // this endpoint is called per incoming mail, so it must not load every lead of the user.
    [HttpGet("find")]
    public async Task<IActionResult> Find([FromQuery] string? email, [FromQuery] string? domain, [FromQuery] string? q)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToLowerInvariant();
        if (email == null && string.IsNullOrWhiteSpace(domain) && q == null)
            return BadRequest("Provide at least one of: email, domain, q");

        var domainFromEmail = NormalizeDomain(email);
        var dom = NormalizeDomain(domain)
                  ?? (domainFromEmail != null && !FreeMailDomains.Contains(domainFromEmail) ? domainFromEmail : null);
        if (email == null && dom == null && q == null) return Ok(Array.Empty<CrmMatchDto>());

        var matches = new List<CrmMatchDto>();

        var leads = await _db.Leads
            .Where(l => l.ImportedByUserId == userId)
            .Where(l => (email != null && (l.CompanyEmail.ToLower() == email || l.PersonalEmail.ToLower() == email))
                        || (dom != null && l.Website.ToLower().Contains(dom))
                        || (q != null && l.Name.ToLower().Contains(q)))
            .Select(l => new { l.Id, l.Name, l.Website, l.CompanyEmail, l.PersonalEmail, l.Phone })
            .Take(Candidates).ToListAsync();

        foreach (var l in leads)
        {
            string? why = null;
            if (email != null && (string.Equals(l.CompanyEmail, email, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(l.PersonalEmail, email, StringComparison.OrdinalIgnoreCase)))
                why = "email";
            else if (dom != null && NormalizeDomain(l.Website) == dom) why = "domain";
            else if (q != null && l.Name.ToLowerInvariant().Contains(q)) why = "name";

            if (why != null)
                matches.Add(new CrmMatchDto("leads", l.Id, l.Name, l.Website,
                    string.IsNullOrWhiteSpace(l.PersonalEmail) ? l.CompanyEmail : l.PersonalEmail, l.Phone, why));
        }

        var clients = await _db.Clients
            .Where(c => c.CreatedByUserId == userId)
            .Where(c => (email != null && c.PrimaryContactEmail != null && c.PrimaryContactEmail.ToLower() == email)
                        || (dom != null && c.Website != null && c.Website.ToLower().Contains(dom))
                        || (q != null && c.Name.ToLower().Contains(q)))
            .Select(c => new { c.Id, c.Name, c.Website, c.PrimaryContactEmail, c.PrimaryContactPhone })
            .Take(Candidates).ToListAsync();

        foreach (var c in clients)
        {
            string? why = null;
            if (email != null && string.Equals(c.PrimaryContactEmail, email, StringComparison.OrdinalIgnoreCase)) why = "email";
            else if (dom != null && NormalizeDomain(c.Website) == dom) why = "domain";
            else if (q != null && c.Name.ToLowerInvariant().Contains(q)) why = "name";

            if (why != null)
                matches.Add(new CrmMatchDto("clients", c.Id, c.Name, c.Website, c.PrimaryContactEmail,
                    c.PrimaryContactPhone, why));
        }

        // Contacts: a person's email resolves to the lead or client they belong to. Ownership is part of
        // the query, so another user's contacts with the same address can never crowd out the caller's.
        if (email != null)
        {
            var contacts = await _db.Contacts
                .Where(c => c.Email != null && c.Email.ToLower() == email)
                .Where(c => _db.Leads.Any(l => l.Id == c.LeadId && l.ImportedByUserId == userId)
                            || _db.Clients.Any(x => x.Id == c.ClientId && x.CreatedByUserId == userId))
                .Take(MaxResults).ToListAsync();

            var leadIds = contacts.Where(c => c.LeadId.HasValue).Select(c => c.LeadId!.Value).Distinct().ToList();
            var clientIds = contacts.Where(c => c.ClientId.HasValue).Select(c => c.ClientId!.Value).Distinct().ToList();
            var leadRows = await _db.Leads.Where(l => leadIds.Contains(l.Id))
                .Select(l => new { l.Id, l.Name, l.Website, l.Phone }).ToDictionaryAsync(l => l.Id);
            var clientRows = await _db.Clients.Where(c => clientIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name, c.Website, c.PrimaryContactPhone }).ToDictionaryAsync(c => c.Id);

            foreach (var contact in contacts)
            {
                if (contact.LeadId.HasValue && leadRows.TryGetValue(contact.LeadId.Value, out var l))
                {
                    if (matches.Any(m => m.ParentId == l.Id)) continue;
                    matches.Add(new CrmMatchDto("leads", l.Id, l.Name, l.Website, contact.Email, contact.Phone ?? l.Phone, "contact"));
                }
                else if (contact.ClientId.HasValue && clientRows.TryGetValue(contact.ClientId.Value, out var c))
                {
                    if (matches.Any(m => m.ParentId == c.Id)) continue;
                    matches.Add(new CrmMatchDto("clients", c.Id, c.Name, c.Website, contact.Email, contact.Phone ?? c.PrimaryContactPhone, "contact"));
                }
            }
        }

        var rank = new Dictionary<string, int> { ["email"] = 0, ["contact"] = 1, ["domain"] = 2, ["name"] = 3 };
        return Ok(matches.OrderBy(m => rank[m.MatchedOn]).ThenBy(m => m.Name).Take(MaxResults));
    }
}

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
    [HttpGet("find")]
    public async Task<IActionResult> Find([FromQuery] string? email, [FromQuery] string? domain, [FromQuery] string? q)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToLowerInvariant();
        if (email == null && domain == null && q == null)
            return BadRequest("Provide at least one of: email, domain, q");

        var domainFromEmail = NormalizeDomain(email);
        var dom = NormalizeDomain(domain)
                  ?? (domainFromEmail != null && !FreeMailDomains.Contains(domainFromEmail) ? domainFromEmail : null);

        var matches = new List<CrmMatchDto>();

        var leads = await _db.Leads.Where(l => l.ImportedByUserId == userId).ToListAsync();
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

        var clients = await _db.Clients.Where(c => c.CreatedByUserId == userId).ToListAsync();
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

        // Contacts: a person's email resolves to the lead or client they belong to.
        if (email != null)
        {
            var contacts = await _db.Contacts
                .Where(c => c.Email != null && c.Email.ToLower() == email)
                .Take(MaxResults).ToListAsync();
            foreach (var contact in contacts)
            {
                var parent = await ResolveOwnerAsync(contact.LeadId, contact.ClientId);
                if (parent == null) continue;
                if (matches.Any(m => m.ParentId == parent.Value.Id)) continue;

                if (parent.Value.Kind == CrmParentKind.Lead)
                {
                    var l = await _db.Leads.FirstAsync(x => x.Id == parent.Value.Id);
                    matches.Add(new CrmMatchDto("leads", l.Id, l.Name, l.Website, contact.Email, contact.Phone ?? l.Phone, "contact"));
                }
                else
                {
                    var c = await _db.Clients.FirstAsync(x => x.Id == parent.Value.Id);
                    matches.Add(new CrmMatchDto("clients", c.Id, c.Name, c.Website, contact.Email, contact.Phone ?? c.PrimaryContactPhone, "contact"));
                }
            }
        }

        var rank = new Dictionary<string, int> { ["email"] = 0, ["contact"] = 1, ["domain"] = 2, ["name"] = 3 };
        return Ok(matches.OrderBy(m => rank[m.MatchedOn]).ThenBy(m => m.Name).Take(MaxResults));
    }
}

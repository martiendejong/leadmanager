using CsvHelper;
using CsvHelper.Configuration;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using LeadManager.Api.Services.Enrichment;
using LeadManager.Api.Services.Hunt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using System.Globalization;

namespace LeadManager.Api.Controllers;

[Route("api/leads")]
[ApiController]
[Authorize]
public class LeadsController : ControllerBase
{
    private const int CsvImportMaxBytes = 5 * 1024 * 1024; // 5 MB

    private readonly LeadManagerDbContext _db;
    private readonly SearchService _search;
    private readonly IConfiguration _configuration;
    private readonly EnrichmentChannel _enrichmentChannel;

    private readonly SalesScoreService _scoring;

    public LeadsController(LeadManagerDbContext db, SearchService search, IConfiguration configuration, EnrichmentChannel enrichmentChannel,
        SalesScoreService? scoring = null)
    {
        _scoring = scoring ?? new SalesScoreService();
        _db = db;
        _search = search;
        _configuration = configuration;
        _enrichmentChannel = enrichmentChannel;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var u = url.Trim();
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "https://" + u;
            var host = new Uri(u).Host.ToLower();
            return host.StartsWith("www.") ? host[4..] : host;
        }
        catch { return url.ToLower(); }
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    private static string? NormalizeWebsiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url;
    }

    // GET /api/leads
    [HttpGet]
    public async Task<IActionResult> GetLeads([FromQuery] LeadFilterParams filters)
    {
        var userId = GetCurrentUserId();
        var query = _db.Leads.Where(l => l.ImportedByUserId == userId);

        if (filters.Enriched.HasValue)
            query = query.Where(l => l.IsEnriched == filters.Enriched.Value);

        if (filters.EnrichedAfter.HasValue)
            query = query.Where(l => l.EnrichedAt >= filters.EnrichedAfter.Value);

        if (filters.EnrichedBefore.HasValue)
            query = query.Where(l => l.EnrichedAt <= filters.EnrichedBefore.Value);

        if (!string.IsNullOrEmpty(filters.AssignedToUserId))
            query = query.Where(l => l.AssignedToUserId == filters.AssignedToUserId);

        if (filters.HasOwner.HasValue)
            query = filters.HasOwner.Value
                ? query.Where(l => l.OwnerName != null && l.OwnerName != "")
                : query.Where(l => l.OwnerName == null || l.OwnerName == "");

        if (filters.HasLinkedIn.HasValue)
            query = filters.HasLinkedIn.Value
                ? query.Where(l => (l.LinkedInUrl != null && l.LinkedInUrl != "") || (l.OwnerLinkedInUrl != null && l.OwnerLinkedInUrl != ""))
                : query.Where(l => (l.LinkedInUrl == null || l.LinkedInUrl == "") && (l.OwnerLinkedInUrl == null || l.OwnerLinkedInUrl == ""));

        if (!string.IsNullOrWhiteSpace(filters.PriorityLabel))
            query = query.Where(l => l.SalesPriorityLabel == filters.PriorityLabel);

        // Sorting
        query = filters.SortBy?.ToLower() switch
        {
            "website"             => filters.SortDesc ? query.OrderByDescending(l => l.Website)            : query.OrderBy(l => l.Website),
            "sector"              => filters.SortDesc ? query.OrderByDescending(l => l.Sector)             : query.OrderBy(l => l.Sector),
            "city"                => filters.SortDesc ? query.OrderByDescending(l => l.City)               : query.OrderBy(l => l.City),
            "source"              => filters.SortDesc ? query.OrderByDescending(l => l.Source)             : query.OrderBy(l => l.Source),
            "createdat"           => filters.SortDesc ? query.OrderByDescending(l => l.CreatedAt)          : query.OrderBy(l => l.CreatedAt),
            "updatedat"           => filters.SortDesc ? query.OrderByDescending(l => l.UpdatedAt)          : query.OrderBy(l => l.UpdatedAt),
            "enrichedat"          => filters.SortDesc ? query.OrderByDescending(l => l.EnrichedAt)         : query.OrderBy(l => l.EnrichedAt),
            "ownername"           => filters.SortDesc ? query.OrderByDescending(l => l.OwnerName)          : query.OrderBy(l => l.OwnerName),
            "isenriched"          => filters.SortDesc ? query.OrderByDescending(l => l.IsEnriched)         : query.OrderBy(l => l.IsEnriched),
            "importedat"          => filters.SortDesc ? query.OrderByDescending(l => l.ImportedAt)         : query.OrderBy(l => l.ImportedAt),
            "salespriorityscore"  => filters.SortDesc ? query.OrderByDescending(l => l.SalesPriorityScore) : query.OrderBy(l => l.SalesPriorityScore),
            _                     => filters.SortDesc ? query.OrderByDescending(l => l.Name)               : query.OrderBy(l => l.Name),
        };
        // Stable paging: rows with equal sort values must come in the same order on every page.
        query = ((IOrderedQueryable<Lead>)query).ThenBy(l => l.Id);

        var total = await query.CountAsync();
        var items = await query
            .Skip((filters.Page - 1) * filters.PageSize)
            .Take(filters.PageSize)
            .Select(l => ToDto(l))
            .ToListAsync();

        return Ok(new { items, total, page = filters.Page, pageSize = filters.PageSize });
    }

    // GET /api/leads/stats
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var userId = GetCurrentUserId();
        var userLeads = _db.Leads.Where(l => l.ImportedByUserId == userId);
        var total = await userLeads.CountAsync();
        var enriched = await userLeads.CountAsync(l => l.IsEnriched);
        return Ok(new LeadStatsDto(total, enriched, total - enriched));
    }

    // GET /api/leads/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetLead(Guid id)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();
        return Ok(ToDto(lead));
    }

    // PUT /api/leads/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateLead(Guid id, [FromBody] UpdateLeadDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        lead.Name = dto.Name;
        lead.Website = NormalizeWebsiteUrl(dto.Website) ?? lead.Website;
        lead.Sector = dto.Sector;
        lead.City = dto.City;
        lead.Phone = dto.Phone;
        lead.CompanyEmail = dto.CompanyEmail;
        lead.Source = dto.Source;
        lead.OwnerName = dto.OwnerName;
        lead.OwnerFirstName = dto.OwnerFirstName;
        lead.OwnerLastName = dto.OwnerLastName;
        lead.PersonalEmail = dto.PersonalEmail;
        lead.LinkedInUrl = dto.LinkedInUrl;
        Rescore(lead);

        await _db.SaveChangesAsync();
        return Ok(ToDto(lead));
    }

    private static readonly HashSet<string> ClearableNumericFields = new(StringComparer.OrdinalIgnoreCase) { "foundingYear", "googleRating", "googleReviewCount", "whyLead", "needs", "productFit" };

    // PATCH /api/leads/{id}/profile
    // Updates the researched profile (owner, LinkedIn, mobile, size, rating, ...) and recalculates the sales priority score
    // from it. Only the fields that are sent change; the score is always derived, never set by hand.
    [HttpPatch("{id:guid}/profile")]
    public async Task<IActionResult> PatchProfile(Guid id, [FromBody] PatchLeadProfileDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        string? error = null;
        string? Text(string? v, string field, int max = 2000)
        {
            if (v == null) return null;
            v = v.Trim();
            if (v.Length > max) { error ??= $"{field} is too long (max {max})."; return null; }
            return v;
        }
        string? Url(string? v, string field)
        {
            if (v == null) return null;
            v = v.Trim();
            if (v == "") return "";
            // A bare address gets https://; an address with any other scheme (ftp:, javascript:, ...) is refused, not rewritten.
            if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:")) v = "https://" + v;
            if (!CrmRules.IsSafeHttpUrl(v)) { error ??= $"{field} must be a valid http(s) address."; return null; }
            return v;
        }
        string? Phone(string? v, string field)
        {
            if (v == null) return null;
            v = v.Trim();
            if (v != "" && !System.Text.RegularExpressions.Regex.IsMatch(v, @"^[0-9 +().\-/]{5,25}$")) { error ??= $"{field} is not a valid phone number."; return null; }
            return v;
        }
        string? Mail(string? v, string field)
        {
            if (v == null) return null;
            v = v.Trim();
            if (v != "" && (v.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(v, out var addr) || addr.Address != v || !v.Contains('.', StringComparison.Ordinal))) { error ??= $"{field} is not a valid e-mail address."; return null; }
            return v;
        }

        var name = Text(dto.Name, "Name", 300);
        var website = dto.Website == null ? null : Url(dto.Website, "Website");
        var sector = Text(dto.Sector, "Sector", 200);
        var city = Text(dto.City, "City", 200);
        var phone = Phone(dto.Phone, "Phone");
        var companyEmail = Mail(dto.CompanyEmail, "Company e-mail");
        var ownerName = Text(dto.OwnerName, "Owner name", 200);
        var ownerFirst = Text(dto.OwnerFirstName, "Owner first name", 100);
        var ownerLast = Text(dto.OwnerLastName, "Owner last name", 100);
        var ownerTitle = Text(dto.OwnerTitle, "Owner title", 200);
        var ownerLinkedIn = Url(dto.OwnerLinkedInUrl, "Owner LinkedIn");
        var ownerMobile = Phone(dto.OwnerMobile, "Owner mobile");
        var personalEmail = Mail(dto.PersonalEmail, "Personal e-mail");
        var linkedIn = Url(dto.LinkedInUrl, "Company LinkedIn");
        var street = Text(dto.Street, "Street", 200);
        var zip = Text(dto.ZipCode, "Zip code", 20);
        var kvk = Text(dto.KvkNumber, "KvK number", 20);
        var legalForm = Text(dto.LegalForm, "Legal form", 100);
        var employees = Text(dto.EmployeeCount, "Employee count", 50);
        var mapsUrl = Url(dto.GoogleMapsUrl, "Google Maps URL");
        var facebook = Url(dto.FacebookUrl, "Facebook URL");
        var instagram = Url(dto.InstagramUrl, "Instagram URL");
        var twitter = Url(dto.TwitterUrl, "Twitter URL");
        var description = Text(dto.Description, "Description", 8000);
        var services = Text(dto.Services, "Services", 8000);
        var audience = Text(dto.TargetAudience, "Target audience", 4000);
        var summary = Text(dto.AiSummary, "Summary", 8000);
        var pitch = Text(dto.SalesPitch, "Sales pitch", 8000);
        var area = Text(dto.WorkingArea, "Working area", 500);
        var certs = Text(dto.Certifications, "Certifications", 500);
        var pricing = Text(dto.PricingInfo, "Pricing info", 1000);
        var hours = Text(dto.OpeningHours, "Opening hours", 500);
        var clients = Text(dto.NotableClients, "Notable clients", 2000);
        var whyLead = Text(dto.WhyLead, "Why this lead", 1000);
        var needsText = Text(dto.Needs, "Needs", 1000);
        string? productFitJson = null;
        if (dto.ProductFit != null)
        {
            // Only products of ours, each with a reason; at most four, each product once. An empty list means "assessed, no clear fit".
            var entries = new List<ProductFitEntry>();
            foreach (var f in dto.ProductFit)
            {
                var key = ProductCatalog.Canonical(f.Product);
                var reason = (f.Reason ?? "").Trim();
                var quote = (f.EvidenceQuote ?? "").Trim();
                var source = Url(f.SourceUrl, "Product fit source");
                if (key == null) { error ??= $"'{f.Product}' is not one of our products. Known: {string.Join(", ", ProductCatalog.Keys)}."; continue; }
                if (reason.Length < 3 || reason.Length > 300) { error ??= "A product fit needs a reason of 3 to 300 characters."; continue; }
                if (quote.Length > 400) { error ??= "A product fit quote is at most 400 characters."; continue; }
                if (entries.Any(e => e.Product == key)) { error ??= $"Product {key} is listed twice."; continue; }
                entries.Add(new ProductFitEntry(key, TextSafety.Clean(reason, 300), quote, source ?? ""));
            }
            if (entries.Count > LeadInsight.MaxFitEntries) error ??= $"At most {LeadInsight.MaxFitEntries} products per lead.";
            productFitJson = LeadInsight.SerializeFit(entries);
        }

        if (dto.FoundingYear is < 1800 || dto.FoundingYear > DateTime.UtcNow.Year) error ??= "Founding year is not valid.";
        if (dto.GoogleRating.HasValue && !(dto.GoogleRating.Value >= 0 && dto.GoogleRating.Value <= 5)) error ??= "Google rating is between 0 and 5.";
        if (dto.GoogleReviewCount is < 0) error ??= "Review count cannot be negative.";
        if (name == "") error ??= "Name cannot be empty.";
        if (website == "") error ??= "Website cannot be empty.";

        var clear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in dto.ClearFields ?? Array.Empty<string>())
        {
            var key = (f ?? "").Trim();
            if (!ClearableNumericFields.Contains(key)) { error ??= $"Field '{f}' cannot be cleared. Allowed: foundingYear, googleRating, googleReviewCount, whyLead, needs, productFit."; continue; }
            clear.Add(key);
        }
        if (clear.Contains("foundingYear") && dto.FoundingYear.HasValue) error ??= "foundingYear is both set and cleared.";
        if (clear.Contains("googleRating") && dto.GoogleRating.HasValue) error ??= "googleRating is both set and cleared.";
        if (clear.Contains("googleReviewCount") && dto.GoogleReviewCount.HasValue) error ??= "googleReviewCount is both set and cleared.";
        if (clear.Contains("whyLead") && whyLead != null) error ??= "whyLead is both set and cleared.";
        if (clear.Contains("needs") && needsText != null) error ??= "needs is both set and cleared.";
        if (clear.Contains("productFit") && productFitJson != null) error ??= "productFit is both set and cleared.";
        if (error != null) return BadRequest(new { error });

        if (name != null) lead.Name = name;
        if (website != null && website != "") lead.Website = website;
        if (sector != null) lead.Sector = sector;
        if (city != null) lead.City = city;
        if (phone != null) lead.Phone = phone;
        if (companyEmail != null) lead.CompanyEmail = companyEmail;
        if (ownerName != null) lead.OwnerName = ownerName;
        if (ownerFirst != null) lead.OwnerFirstName = ownerFirst;
        if (ownerLast != null) lead.OwnerLastName = ownerLast;
        if (ownerTitle != null) lead.OwnerTitle = ownerTitle;
        if (ownerLinkedIn != null) lead.OwnerLinkedInUrl = ownerLinkedIn;
        if (ownerMobile != null) lead.OwnerMobile = ownerMobile;
        if (personalEmail != null) lead.PersonalEmail = personalEmail;
        if (linkedIn != null) lead.LinkedInUrl = linkedIn;
        if (street != null) lead.Street = street;
        if (zip != null) lead.ZipCode = zip;
        if (kvk != null) lead.KvkNumber = kvk;
        if (legalForm != null) lead.LegalForm = legalForm;
        if (employees != null) lead.EmployeeCount = employees;
        if (dto.FoundingYear.HasValue) lead.FoundingYear = dto.FoundingYear;
        if (dto.GoogleRating.HasValue) lead.GoogleRating = dto.GoogleRating;
        if (dto.GoogleReviewCount.HasValue) lead.GoogleReviewCount = dto.GoogleReviewCount;
        if (clear.Contains("foundingYear")) lead.FoundingYear = null;
        if (clear.Contains("googleRating")) lead.GoogleRating = null;
        if (clear.Contains("googleReviewCount")) lead.GoogleReviewCount = null;
        if (mapsUrl != null) lead.GoogleMapsUrl = mapsUrl;
        if (facebook != null) lead.FacebookUrl = facebook;
        if (instagram != null) lead.InstagramUrl = instagram;
        if (twitter != null) lead.TwitterUrl = twitter;
        if (description != null) lead.Description = description;
        if (services != null) lead.Services = services;
        if (audience != null) lead.TargetAudience = audience;
        if (summary != null) lead.AiSummary = summary;
        if (pitch != null) lead.SalesPitch = pitch;
        if (area != null) lead.WorkingArea = area;
        if (certs != null) lead.Certifications = certs;
        if (pricing != null) lead.PricingInfo = pricing;
        if (hours != null) lead.OpeningHours = hours;
        if (clients != null) lead.NotableClients = clients;
        if (whyLead != null) lead.WhyLead = whyLead == "" ? null : whyLead;
        if (needsText != null) lead.Needs = needsText == "" ? null : needsText;
        if (productFitJson != null) lead.ProductFitJson = productFitJson;
        if (clear.Contains("whyLead")) lead.WhyLead = null;
        if (clear.Contains("needs")) lead.Needs = null;
        if (clear.Contains("productFit")) lead.ProductFitJson = null;

        Rescore(lead);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Name + website are unique per user: another lead already has that combination.
            return Conflict(new { error = "Another lead already has this name and website." });
        }
        return Ok(ToDto(lead));
    }

    /// <summary>The score is always derived from the profile; every edit path keeps it in step.</summary>
    private void Rescore(Lead lead)
    {
        var scored = _scoring.CalculateScore(lead);
        lead.SalesPriorityScore = scored.Score;
        lead.SalesPriorityLabel = scored.Label;
        lead.SalesPriorityReasoning = scored.Reasoning;
    }

    // POST /api/leads/{id}/status - Update lead status (forward transitions only)
    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> UpdateLeadStatus(Guid id, [FromBody] UpdateLeadStatusDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound("Lead not found");

        // Parse target status
        if (!Enum.TryParse<LeadStatus>(dto.Status, out var targetStatus))
            return BadRequest($"Invalid status: {dto.Status}");

        // Only allow forward transitions: Lead -> Prospect
        if (lead.Status == LeadStatus.Prospect && targetStatus == LeadStatus.Lead)
            return BadRequest("Cannot move prospect back to lead status");

        // No-op if already at target status
        if (lead.Status == targetStatus)
            return Ok(ToDto(lead));

        lead.Status = targetStatus;
        await _db.SaveChangesAsync();
        return Ok(ToDto(lead));
    }

    // DELETE /api/leads/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteLead(Guid id)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        _db.Leads.Remove(lead);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // PUT /api/leads/{id}/pipeline - Update pipeline status (869ck3j46)
    [HttpPut("{id:guid}/pipeline")]
    public async Task<IActionResult> UpdatePipelineStatus(Guid id, [FromBody] UpdatePipelineStatusDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        if (!Enum.TryParse<PipelineStatus>(dto.PipelineStatus, out var status))
            return BadRequest($"Invalid pipeline status: {dto.PipelineStatus}");

        lead.PipelineStatus = status;
        await _db.SaveChangesAsync();
        return Ok(ToDto(lead));
    }

    // PATCH /api/leads/{id}/workflow - Update workflow step and data
    [HttpPatch("{id:guid}/workflow")]
    public async Task<IActionResult> UpdateWorkflow(Guid id, [FromBody] UpdateWorkflowDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        if (dto.Step.HasValue)
            lead.WorkflowStep = Math.Clamp(dto.Step.Value, 1, 10);
        if (dto.WorkflowDataJson != null)
            lead.WorkflowDataJson = dto.WorkflowDataJson;

        await _db.SaveChangesAsync();
        return Ok(new { workflowStep = lead.WorkflowStep, workflowDataJson = lead.WorkflowDataJson });
    }

    // POST /api/leads - Create single lead (Task #3, #4)
    [HttpPost]
    public async Task<IActionResult> CreateLead([FromBody] CreateLeadDto dto, [FromQuery] bool force = false)
    {
        // Task #4: Validate at least one input type is provided
        var hasWebsite = !string.IsNullOrWhiteSpace(dto.Website);
        var hasManualInput = !string.IsNullOrWhiteSpace(dto.ManualInput);
        // hasDocuments will be checked separately via document upload endpoint

        if (!hasWebsite && !hasManualInput)
        {
            return BadRequest("Fill in at least one field: Website URL or free-text input.");
        }

        // Validate ManualInput length (Task #3)
        if (dto.ManualInput != null && dto.ManualInput.Length > 5000)
        {
            return BadRequest("Vrije tekst mag maximaal 5000 tekens bevatten.");
        }

        var userId = GetCurrentUserId();

        // Duplicate detection (869ck3j4y) — skip when ?force=true
        if (!force)
        {
            var normalizedWebsite = NormalizeWebsiteUrl(dto.Website);
            var incomingDomain = ExtractDomain(normalizedWebsite);
            var incomingNameLower = dto.Name?.ToLower() ?? "";
            var incomingEmailLower = dto.CompanyEmail?.ToLower() ?? "";

            var userLeads = await _db.Leads
                .Where(l => l.ImportedByUserId == userId)
                .ToListAsync();

            var duplicates = userLeads.Where(l =>
            {
                // Same domain
                var existingDomain = ExtractDomain(l.Website);
                if (!string.IsNullOrEmpty(incomingDomain) && !string.IsNullOrEmpty(existingDomain) &&
                    existingDomain.Equals(incomingDomain, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Same company email
                if (!string.IsNullOrEmpty(incomingEmailLower) &&
                    !string.IsNullOrEmpty(l.CompanyEmail) &&
                    l.CompanyEmail.ToLower() == incomingEmailLower)
                    return true;

                // Fuzzy name match: contains or Levenshtein distance <= 2
                var existingNameLower = l.Name?.ToLower() ?? "";
                if (!string.IsNullOrEmpty(incomingNameLower) && !string.IsNullOrEmpty(existingNameLower))
                {
                    if (existingNameLower.Contains(incomingNameLower) || incomingNameLower.Contains(existingNameLower))
                        return true;
                    if (LevenshteinDistance(incomingNameLower, existingNameLower) <= 2)
                        return true;
                }

                return false;
            }).Select(l => new DuplicateLeadDto(l.Id, l.Name, l.Website, l.City, l.Sector, l.SalesPriorityScore))
              .ToList();

            if (duplicates.Count > 0)
            {
                return Conflict(new DuplicateCheckResultDto(duplicates));
            }
        }

        var lead = new Lead
        {
            Name = dto.Name,
            Website = NormalizeWebsiteUrl(dto.Website) ?? "",
            Sector = dto.Sector,
            City = dto.City,
            Phone = dto.Phone,
            CompanyEmail = dto.CompanyEmail,
            Source = dto.Source,
            ManualInput = dto.ManualInput,
            ImportedByUserId = userId,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();

        _db.Activities.Add(new LeadActivity
        {
            LeadId = lead.Id,
            UserId = userId,
            ActivityType = ActivityType.Created,
            Note = null,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetLead), new { id = lead.Id }, ToDto(lead));
    }

    // POST /api/leads/{id}/merge - Merge two leads (869ck3j4y)
    [HttpPost("{id:guid}/merge")]
    public async Task<IActionResult> MergeLead(Guid id, [FromBody] MergeLeadDto dto)
    {
        var userId = GetCurrentUserId();

        var target = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (target == null) return NotFound("Target lead not found.");

        var source = await _db.Leads.FirstOrDefaultAsync(l => l.Id == dto.SourceLeadId && l.ImportedByUserId == userId);
        if (source == null) return NotFound("Source lead not found.");

        // Merge: prefer non-null/non-empty existing value, else use source value
        static string MergeStr(string existing, string source) =>
            !string.IsNullOrWhiteSpace(existing) ? existing : source;
        static string? MergeNullStr(string? existing, string? source) =>
            !string.IsNullOrWhiteSpace(existing) ? existing : source;

        target.Website = MergeStr(target.Website, source.Website);
        target.Sector = MergeStr(target.Sector, source.Sector);
        target.City = MergeStr(target.City, source.City);
        target.Phone = MergeStr(target.Phone, source.Phone);
        target.CompanyEmail = MergeStr(target.CompanyEmail, source.CompanyEmail);
        target.OwnerName = MergeStr(target.OwnerName, source.OwnerName);
        target.OwnerFirstName = MergeStr(target.OwnerFirstName, source.OwnerFirstName);
        target.OwnerLastName = MergeStr(target.OwnerLastName, source.OwnerLastName);
        target.PersonalEmail = MergeStr(target.PersonalEmail, source.PersonalEmail);
        target.LinkedInUrl = MergeStr(target.LinkedInUrl, source.LinkedInUrl);
        target.Source = MergeStr(target.Source, source.Source);
        target.OwnerTitle = MergeNullStr(target.OwnerTitle, source.OwnerTitle);
        target.Description = MergeNullStr(target.Description, source.Description);
        target.Services = MergeNullStr(target.Services, source.Services);
        target.TargetAudience = MergeNullStr(target.TargetAudience, source.TargetAudience);
        target.AiSummary = MergeNullStr(target.AiSummary, source.AiSummary);
        target.SalesPitch = MergeNullStr(target.SalesPitch, source.SalesPitch);
        target.KvkNumber = MergeNullStr(target.KvkNumber, source.KvkNumber);
        target.VatNumber = MergeNullStr(target.VatNumber, source.VatNumber);
        target.Street = MergeNullStr(target.Street, source.Street);
        target.ZipCode = MergeNullStr(target.ZipCode, source.ZipCode);
        target.EmployeeCount = MergeNullStr(target.EmployeeCount, source.EmployeeCount);
        target.BranchCount ??= source.BranchCount;
        target.FoundingYear ??= source.FoundingYear;
        target.LegalForm = MergeNullStr(target.LegalForm, source.LegalForm);
        target.GoogleRating ??= source.GoogleRating;
        target.GoogleReviewCount ??= source.GoogleReviewCount;
        target.GoogleMapsUrl = MergeNullStr(target.GoogleMapsUrl, source.GoogleMapsUrl);
        target.FacebookUrl = MergeNullStr(target.FacebookUrl, source.FacebookUrl);
        target.InstagramUrl = MergeNullStr(target.InstagramUrl, source.InstagramUrl);
        target.TwitterUrl = MergeNullStr(target.TwitterUrl, source.TwitterUrl);
        target.GroupName = MergeNullStr(target.GroupName, source.GroupName);
        target.NotableClients = MergeNullStr(target.NotableClients, source.NotableClients);
        target.SalesPriorityScore ??= source.SalesPriorityScore;
        target.ManualInput = MergeNullStr(target.ManualInput, source.ManualInput);
        target.SalesApproach = MergeNullStr(target.SalesApproach, source.SalesApproach);
        target.OwnerLinkedInUrl = MergeNullStr(target.OwnerLinkedInUrl, source.OwnerLinkedInUrl);
        target.OwnerMobile = MergeNullStr(target.OwnerMobile, source.OwnerMobile);
        target.WorkingArea = MergeNullStr(target.WorkingArea, source.WorkingArea);
        target.Certifications = MergeNullStr(target.Certifications, source.Certifications);
        target.PricingInfo = MergeNullStr(target.PricingInfo, source.PricingInfo);
        target.OpeningHours = MergeNullStr(target.OpeningHours, source.OpeningHours);
        target.SalesPriorityLabel = MergeNullStr(target.SalesPriorityLabel, source.SalesPriorityLabel);
        target.SalesPriorityReasoning = MergeNullStr(target.SalesPriorityReasoning, source.SalesPriorityReasoning);
        target.Signals = MergeNullStr(target.Signals, source.Signals);

        if (source.IsEnriched && !target.IsEnriched)
        {
            target.IsEnriched = true;
            target.EnrichedAt = source.EnrichedAt;
        }
        if (source.HasUploadedDocuments) target.HasUploadedDocuments = true;

        // TODO: Add LeadActivity "Samengevoegd met [source.Name]" once LeadActivity entity is merged (PR #30)

        _db.Leads.Remove(source);
        await _db.SaveChangesAsync();

        return Ok(ToDto(target));
    }

    // PUT /api/leads/{id}/assign - Assign lead to user (869ck3j4u)
    [HttpPut("{id:guid}/assign")]
    public async Task<IActionResult> AssignLead(Guid id, [FromBody] AssignLeadDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        lead.AssignedToUserId = dto.UserId;
        await _db.SaveChangesAsync();

        return Ok(ToDto(lead));
    }

    // GET /api/leads/assignees - Get users with assigned leads (869ck3j4u)
    [HttpGet("assignees")]
    public async Task<IActionResult> GetAssignees()
    {
        var userId = GetCurrentUserId();

        var assigneeCounts = await _db.Leads
            .Where(l => l.ImportedByUserId == userId && l.AssignedToUserId != null)
            .GroupBy(l => l.AssignedToUserId!)
            .Select(g => new { UserId = g.Key, LeadCount = g.Count() })
            .ToListAsync();

        var userManager = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser>>();

        var result = new List<AssigneeDto>();
        foreach (var item in assigneeCounts)
        {
            var user = await userManager.FindByIdAsync(item.UserId);
            var displayName = user != null
                ? $"{user.FirstName} {user.LastName}".Trim()
                : item.UserId;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = user?.Email ?? item.UserId;
            result.Add(new AssigneeDto(item.UserId, displayName, item.LeadCount));
        }

        return Ok(result);
    }

    // POST /api/leads/{id}/documents - Upload documents for lead (Task #2)
    [HttpPost("{id}/documents")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadDocuments(Guid id, List<IFormFile> files)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);

        if (lead == null)
            return NotFound();

        // Validate file count
        if (files == null || files.Count == 0)
            return BadRequest("No files provided.");

        if (files.Count > 5)
            return BadRequest("Maximum 5 files allowed.");

        // Validate file types and sizes
        var invalidFiles = files.Where(f =>
            !Services.Enrichment.DocumentParserService.IsValidFileType(f.FileName) ||
            !Services.Enrichment.DocumentParserService.IsValidFileSize(f.Length)
        ).ToList();

        if (invalidFiles.Any())
        {
            return BadRequest($"Invalid files detected. Allowed types: PDF, DOCX, TXT. Max size: 10MB per file.");
        }

        // Parse documents
        var parserLogger = HttpContext.RequestServices.GetRequiredService<ILogger<Services.Enrichment.DocumentParserService>>();
        var parser = new Services.Enrichment.DocumentParserService(parserLogger);
        var parsedTexts = await parser.ParseDocumentsAsync(_db, id, files);

        // Update lead
        lead.HasUploadedDocuments = true;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            filesProcessed = parsedTexts.Count,
            totalFiles = files.Count,
            message = $"{parsedTexts.Count} documents parsed successfully"
        });
    }

    // POST /api/leads/{id}/regenerate-sales-approach - Generate AI sales approach (Task #7)
    [HttpPost("{id}/regenerate-sales-approach")]
    public async Task<IActionResult> RegenerateSalesApproach(Guid id)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);

        if (lead == null)
            return NotFound();

        // Generate sales approach (typed client via DI — no per-request HttpClient, #844)
        var service = HttpContext.RequestServices.GetRequiredService<Services.Enrichment.AiSalesApproachService>();

        var result = await service.GenerateAsync(lead);

        if (result == null)
        {
            return BadRequest("Sales approach could not be generated. Check that the owner name and contact details are known.");
        }

        // Save to lead
        lead.SalesApproach = System.Text.Json.JsonSerializer.Serialize(result);
        await _db.SaveChangesAsync();

        return Ok(result);
    }

    // POST /api/leads/import — CSV bulk import (max 500 rows, max 5 MB)
    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CsvImportMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvImportMaxBytes)]
    public async Task<IActionResult> ImportLeadsCsv(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");

        if (file.Length > CsvImportMaxBytes)
            return BadRequest($"CSV file exceeds the {CsvImportMaxBytes / (1024 * 1024)} MB limit.");

        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only .csv files are supported. For Excel (.xlsx), use the Excel import button.");

        var userId = GetCurrentUserId();
        var leadsToInsert = new List<Lead>();
        var rowErrors = new List<CsvImportRowError>();
        int skipped = 0;

        // Load existing website domains for dedup — scoped to this user
        var existingWebsites = await _db.Leads
            .Where(l => l.ImportedByUserId == userId && l.Website != null && l.Website != "")
            .Select(l => l.Website.ToLower())
            .ToListAsync();
        var existingWebsiteSet = existingWebsites.ToHashSet();

        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };
        using var csv = new CsvReader(reader, csvConfig);

        // Read all records as dynamic to allow flexible column mapping
        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        // Build alias map: normalized header -> field name
        string? FindHeader(params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var match = headers.FirstOrDefault(h =>
                    string.Equals(h?.Trim(), alias, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return null;
        }

        var nameHeader    = FindHeader("Name", "Naam", "Bedrijfsnaam", "Company", "company_name");
        var websiteHeader = FindHeader("Website", "website", "url", "URL");
        var emailHeader   = FindHeader("Email", "CompanyEmail", "Bedrijfsemail", "email");
        var sectorHeader  = FindHeader("Sector", "Industry", "Industrie", "sector", "industry");
        var cityHeader    = FindHeader("City", "Stad", "Gemeente", "city", "plaats");
        var phoneHeader   = FindHeader("Phone", "Telefoon", "Tel", "phone", "telefoon");

        if (nameHeader == null)
            return BadRequest("CSV must contain a name column (Name, Naam, Bedrijfsnaam, or Company).");

        int rowNumber = 1; // header is row 1
        while (await csv.ReadAsync())
        {
            rowNumber++;
            if (rowNumber > 501) // max 500 data rows
                break;

            try
            {
                var name    = nameHeader != null    ? csv.GetField(nameHeader)?.Trim()    ?? "" : "";
                var website = websiteHeader != null ? csv.GetField(websiteHeader)?.Trim() ?? "" : "";
                var email   = emailHeader != null   ? csv.GetField(emailHeader)?.Trim()   ?? "" : "";
                var sector  = sectorHeader != null  ? csv.GetField(sectorHeader)?.Trim()  ?? "" : "";
                var city    = cityHeader != null    ? csv.GetField(cityHeader)?.Trim()    ?? "" : "";
                var phone   = phoneHeader != null   ? csv.GetField(phoneHeader)?.Trim()   ?? "" : "";

                if (string.IsNullOrWhiteSpace(name))
                {
                    rowErrors.Add(new CsvImportRowError(rowNumber, "Name is required."));
                    continue;
                }

                // Dedup by website domain
                var normalizedWebsite = NormalizeWebsiteUrl(website) ?? "";
                var websiteLower = normalizedWebsite.ToLower();
                if (!string.IsNullOrWhiteSpace(websiteLower) && existingWebsiteSet.Contains(websiteLower))
                {
                    skipped++;
                    continue;
                }

                var lead = new Lead
                {
                    Name = name,
                    Website = normalizedWebsite,
                    CompanyEmail = email,
                    Sector = sector,
                    City = city,
                    Phone = phone,
                    Source = "csv_import",
                    ImportedByUserId = userId,
                    ImportedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };

                leadsToInsert.Add(lead);
                if (!string.IsNullOrWhiteSpace(websiteLower))
                    existingWebsiteSet.Add(websiteLower);
            }
            catch (Exception ex)
            {
                rowErrors.Add(new CsvImportRowError(rowNumber, ex.Message));
            }
        }

        Guid? enrichmentJobId = null;
        if (leadsToInsert.Count > 0)
        {
            _db.Leads.AddRange(leadsToInsert);
            await _db.SaveChangesAsync();

            var enrichableIds = leadsToInsert
                .Where(l => !string.IsNullOrWhiteSpace(l.Website))
                .Select(l => l.Id)
                .ToList();

            if (enrichableIds.Count > 0)
            {
                var job = new EnrichmentJob
                {
                    LeadIds = enrichableIds,
                    TotalLeads = enrichableIds.Count,
                    RequestedByUserId = userId
                };
                _db.EnrichmentJobs.Add(job);
                await _db.SaveChangesAsync();
                await _enrichmentChannel.Writer.WriteAsync(job.Id);
                enrichmentJobId = job.Id;
            }
        }

        return Ok(new CsvImportResultDto(leadsToInsert.Count, skipped, rowErrors, enrichmentJobId));
    }

    // GET /api/leads/export?format=csv|xlsx&[filter params]
    [HttpGet("export")]
    public async Task<IActionResult> ExportLeads([FromQuery] LeadFilterParams filters, [FromQuery] string format = "csv")
    {
        var userId = GetCurrentUserId();
        var query = _db.Leads.Where(l => l.ImportedByUserId == userId);

        if (filters.Enriched.HasValue)
            query = query.Where(l => l.IsEnriched == filters.Enriched.Value);
        if (filters.EnrichedAfter.HasValue)
            query = query.Where(l => l.EnrichedAt >= filters.EnrichedAfter.Value);
        if (filters.EnrichedBefore.HasValue)
            query = query.Where(l => l.EnrichedAt <= filters.EnrichedBefore.Value);

        // Apply same sort as list (no paging — export all matching)
        query = filters.SortBy?.ToLower() switch
        {
            "website"    => filters.SortDesc ? query.OrderByDescending(l => l.Website)    : query.OrderBy(l => l.Website),
            "sector"     => filters.SortDesc ? query.OrderByDescending(l => l.Sector)     : query.OrderBy(l => l.Sector),
            "city"       => filters.SortDesc ? query.OrderByDescending(l => l.City)       : query.OrderBy(l => l.City),
            "source"     => filters.SortDesc ? query.OrderByDescending(l => l.Source)     : query.OrderBy(l => l.Source),
            "createdat"  => filters.SortDesc ? query.OrderByDescending(l => l.CreatedAt)  : query.OrderBy(l => l.CreatedAt),
            "importedat" => filters.SortDesc ? query.OrderByDescending(l => l.ImportedAt) : query.OrderBy(l => l.ImportedAt),
            _            => filters.SortDesc ? query.OrderByDescending(l => l.Name)       : query.OrderBy(l => l.Name),
        };

        var leads = await query.ToListAsync();
        var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

        if (format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Leads");

            // Header row
            var columns = new[]
            {
                "Name", "Email", "Phone", "Contact person", "City", "Sector",
                "KvK", "Employees", "Google Rating", "Sales Score",
                "Enriched", "Created"
            };
            for (int i = 0; i < columns.Length; i++)
                ws.Cells[1, i + 1].Value = columns[i];

            // Data rows
            for (int r = 0; r < leads.Count; r++)
            {
                var l = leads[r];
                int row = r + 2;
                ws.Cells[row, 1].Value  = l.Name;
                ws.Cells[row, 2].Value  = l.CompanyEmail;
                ws.Cells[row, 3].Value  = l.Phone;
                ws.Cells[row, 4].Value  = l.OwnerName;
                ws.Cells[row, 5].Value  = l.City;
                ws.Cells[row, 6].Value  = l.Sector;
                ws.Cells[row, 7].Value  = l.KvkNumber;
                ws.Cells[row, 8].Value  = l.EmployeeCount;
                ws.Cells[row, 9].Value  = l.GoogleRating.HasValue ? (object)l.GoogleRating.Value : "";
                ws.Cells[row, 10].Value = l.SalesPriorityScore.HasValue ? (object)l.SalesPriorityScore.Value : "";
                ws.Cells[row, 11].Value = l.IsEnriched ? "Yes" : "No";
                ws.Cells[row, 12].Value = l.CreatedAt.ToString("yyyy-MM-dd");
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            var bytes = await package.GetAsByteArrayAsync();
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"leads-{dateStr}.xlsx");
        }
        else
        {
            // CSV export
            var sb = new System.Text.StringBuilder();
            using var writer = new System.IO.StringWriter(sb);
            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
            using var csvWriter = new CsvWriter(writer, csvConfig);

            // Write header
            foreach (var col in new[] {
                "name", "email", "phone", "contact_person", "city", "sector",
                "kvk", "employees", "google_rating", "google_reviews",
                "sales_score", "enriched", "enrichment_status", "created"
            })
                csvWriter.WriteField(col);
            csvWriter.NextRecord();

            // Write rows
            foreach (var l in leads)
            {
                csvWriter.WriteField(l.Name);
                csvWriter.WriteField(l.CompanyEmail);
                csvWriter.WriteField(l.Phone);
                csvWriter.WriteField(l.OwnerName);
                csvWriter.WriteField(l.City);
                csvWriter.WriteField(l.Sector);
                csvWriter.WriteField(l.KvkNumber ?? "");
                csvWriter.WriteField(l.EmployeeCount ?? "");
                csvWriter.WriteField(l.GoogleRating.HasValue ? l.GoogleRating.Value.ToString("F1") : "");
                csvWriter.WriteField(l.GoogleReviewCount.HasValue ? l.GoogleReviewCount.Value.ToString() : "");
                csvWriter.WriteField(l.SalesPriorityScore.HasValue ? l.SalesPriorityScore.Value.ToString() : "");
                csvWriter.WriteField(l.IsEnriched ? "yes" : "no");
                csvWriter.WriteField(l.IsEnriched ? "enriched" : "not enriched");
                csvWriter.WriteField(l.CreatedAt.ToString("yyyy-MM-dd"));
                csvWriter.NextRecord();
            }

            await writer.FlushAsync();
            var csvBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(csvBytes, "text/csv; charset=utf-8", $"leads-{dateStr}.csv");
        }
    }

    // POST /api/leads/{id}/generate-outreach
    [HttpPost("{id:guid}/generate-outreach")]
    public async Task<IActionResult> GenerateOutreachEmail(Guid id)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);

        if (lead == null)
            return NotFound();

        var service = HttpContext.RequestServices.GetRequiredService<Services.Enrichment.OutreachEmailService>();

        var result = await service.GenerateAsync(lead);

        if (result == null)
        {
            return BadRequest("Outreach email could not be generated. Try again.");
        }

        return Ok(result);
    }

    // GET /api/leads/analytics
    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics([FromQuery] string? from, [FromQuery] string? to)
    {
        var userId = GetCurrentUserId();
        var query = _db.Leads.Where(l => l.ImportedByUserId == userId);

        // Parse date range
        DateTime? fromDate = null;
        DateTime? toDate = null;

        if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var parsedFrom))
            fromDate = parsedFrom.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(to) && DateTime.TryParse(to, out var parsedTo))
            toDate = parsedTo.ToUniversalTime().AddDays(1); // inclusive

        // Default: last 30 days for time series
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        if (fromDate.HasValue)
            query = query.Where(l => l.CreatedAt >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(l => l.CreatedAt < toDate.Value);

        var allLeads = await query.ToListAsync();

        var totalLeads = allLeads.Count;
        var enrichedLeads = allLeads.Count(l => l.IsEnriched);
        var avgSalesScore = totalLeads > 0 && allLeads.Any(l => l.SalesPriorityScore.HasValue)
            ? Math.Round(allLeads.Where(l => l.SalesPriorityScore.HasValue).Average(l => (double)l.SalesPriorityScore!.Value), 1)
            : 0.0;

        var leadsThisMonth = allLeads.Count(l => l.CreatedAt >= DateTime.UtcNow.AddDays(-30));
        var conversionRate = totalLeads > 0 ? Math.Round((double)enrichedLeads / totalLeads, 2) : 0.0;

        var leadsByStatus = allLeads
            .GroupBy(l => l.IsEnriched ? "Verrijkt" : "Nieuw")
            .Select(g => new { status = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var leadsByIndustry = allLeads
            .Where(l => !string.IsNullOrWhiteSpace(l.Sector))
            .GroupBy(l => l.Sector)
            .Select(g => new { industry = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(8)
            .ToList();

        // Time series: group by date, cover last 30 days (or filtered range)
        var timeSeriesStart = fromDate ?? thirtyDaysAgo;
        var timeSeriesLeads = await _db.Leads
            .Where(l => l.ImportedByUserId == userId && l.CreatedAt >= timeSeriesStart)
            .ToListAsync();

        var leadsOverTime = timeSeriesLeads
            .GroupBy(l => l.CreatedAt.Date)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), count = g.Count() })
            .OrderBy(x => x.date)
            .ToList();

        var topSources = allLeads
            .Where(l => !string.IsNullOrWhiteSpace(l.Source))
            .GroupBy(l => l.Source)
            .Select(g => new { source = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(10)
            .ToList();

        var avgScoreByIndustry = allLeads
            .Where(l => !string.IsNullOrWhiteSpace(l.Sector) && l.SalesPriorityScore.HasValue)
            .GroupBy(l => l.Sector)
            .Select(g => new
            {
                industry = g.Key,
                avgScore = Math.Round(g.Average(l => (double)l.SalesPriorityScore!.Value), 1)
            })
            .OrderByDescending(x => x.avgScore)
            .Take(8)
            .ToList();

        // Team performance: leads grouped by assigned user
        var assignedLeadGroups = allLeads
            .Where(l => !string.IsNullOrWhiteSpace(l.AssignedToUserId))
            .GroupBy(l => l.AssignedToUserId!)
            .ToList();

        var userIds = assignedLeadGroups.Select(g => g.Key).ToList();
        var users = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToListAsync();
        var userLookup = users.ToDictionary(u => u.Id);

        var teamPerformance = assignedLeadGroups
            .Select(g =>
            {
                var user = userLookup.GetValueOrDefault(g.Key);
                var name = user != null && (!string.IsNullOrWhiteSpace(user.FirstName) || !string.IsNullOrWhiteSpace(user.LastName))
                    ? $"{user.FirstName} {user.LastName}".Trim()
                    : user?.Email ?? g.Key;
                var leadsInGroup = g.ToList();
                return new
                {
                    userName = name,
                    totalLeads = leadsInGroup.Count,
                    enrichedLeads = leadsInGroup.Count(l => l.IsEnriched),
                    avgScore = leadsInGroup.Any(l => l.SalesPriorityScore.HasValue)
                        ? Math.Round(leadsInGroup.Where(l => l.SalesPriorityScore.HasValue).Average(l => (double)l.SalesPriorityScore!.Value), 1)
                        : 0.0,
                    wonLeads = leadsInGroup.Count(l => l.PipelineStatus == PipelineStatus.Won)
                };
            })
            .OrderByDescending(x => x.totalLeads)
            .ToList();

        return Ok(new
        {
            totalLeads,
            enrichedLeads,
            avgSalesScore,
            leadsThisMonth,
            conversionRate,
            leadsByStatus,
            leadsByIndustry,
            leadsOverTime,
            topSources,
            avgScoreByIndustry,
            teamPerformance
        });
    }

    // POST /api/leads/search
    [HttpPost("search")]
    public async Task<IActionResult> SearchLeads([FromBody] LeadSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sector))
            return BadRequest("Sector is required.");

        var location = string.IsNullOrWhiteSpace(request.Location) ? "nederland" : request.Location;
        var query = $"{request.Sector} {location} eigenaar directeur";

        var results = await _search.SearchAsync(query, request.Sector, request.Limit);
        return Ok(results);
    }

    // POST /api/leads/search/import
    [HttpPost("search/import")]
    public async Task<IActionResult> ImportSearchResults([FromBody] LeadSearchImportRequest request)
    {
        if (request.Leads == null || request.Leads.Count == 0)
            return BadRequest("No leads provided.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        // Load existing pairs for dedup — scoped to this user
        var existingList = await _db.Leads
            .Where(l => l.ImportedByUserId == userId)
            .Select(l => new { NameLower = l.Name.ToLower(), WebsiteLower = l.Website.ToLower() })
            .ToListAsync();
        var existing = existingList.ToHashSet();

        var toInsert = new List<Lead>();
        int skipped = 0;

        foreach (var item in request.Leads)
        {
            var key = new { NameLower = item.Name.ToLower(), WebsiteLower = item.Website.ToLower() };
            if (existing.Any(e => e.NameLower == key.NameLower && e.WebsiteLower == key.WebsiteLower))
            {
                skipped++;
                continue;
            }

            toInsert.Add(new Lead
            {
                Name = item.Name,
                Website = NormalizeWebsiteUrl(item.Website) ?? "",
                City = item.City,
                Sector = item.Sector,
                Phone = item.Phone,
                CompanyEmail = item.Email,
                Source = item.Source,
                OwnerName = item.OwnerName ?? "",
                Description = item.Description ?? "",
                Services = item.Services ?? "",
                TargetAudience = item.TargetAudience ?? "",
                ImportedByUserId = userId,
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });

            existing.Add(key);
        }

        if (toInsert.Count > 0)
        {
            _db.Leads.AddRange(toInsert);
            await _db.SaveChangesAsync();
        }

        return Ok(new ImportResultDto(toInsert.Count, skipped, 0, []));
    }

    // PUT /api/leads/{id}/reminder — set or clear stale-lead reminder (869ck3j52/869ck3j58)
    [HttpPut("{id:guid}/reminder")]
    public async Task<IActionResult> SetReminder(Guid id, [FromBody] SetReminderDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();

        lead.ReminderDate = dto.ReminderDate;
        await _db.SaveChangesAsync();

        return Ok(ToDto(lead));
    }

    // POST /api/leads/{id}/convert
    [HttpPost("{id:guid}/convert")]
    public async Task<IActionResult> ConvertToClient(Guid id, [FromBody] ConvertLeadDto dto)
    {
        var userId = GetCurrentUserId();
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == userId);
        if (lead == null) return NotFound();
        if (lead.ConvertedToClientId.HasValue)
            return Conflict("Lead has already been converted to a client.");

        var client = new Client
        {
            Name = dto.Name,
            Plan = dto.Plan,
            PrimaryContactName = !string.IsNullOrWhiteSpace(dto.PrimaryContactName) ? dto.PrimaryContactName : lead.OwnerName,
            PrimaryContactEmail = !string.IsNullOrWhiteSpace(dto.PrimaryContactEmail) ? dto.PrimaryContactEmail : (!string.IsNullOrWhiteSpace(lead.PersonalEmail) ? lead.PersonalEmail : lead.CompanyEmail),
            PrimaryContactPhone = !string.IsNullOrWhiteSpace(dto.PrimaryContactPhone) ? dto.PrimaryContactPhone : lead.Phone,
            Website = lead.Website,
            City = lead.City,
            Sector = lead.Sector,
            Notes = dto.Notes,
            SourceLeadId = lead.Id,
            CreatedByUserId = userId,
        };

        var project = new Project
        {
            ClientId = client.Id,
            Name = $"Project - {client.Name}",
            Description = "Default project created on conversion"
        };
        client.Projects.Add(project);

        lead.ConvertedToClientId = client.Id;

        _db.Clients.Add(client);
        await _db.SaveChangesAsync();

        return CreatedAtAction("GetClient", "Clients", new { id = client.Id }, ClientsController.MapClientDto(client));
    }

    private static LeadDto ToDto(Lead l) => new(
        l.Id,
        l.Name,
        l.Website,
        l.Sector,
        l.City,
        l.Phone,
        l.CompanyEmail,
        l.OwnerName,
        l.OwnerFirstName,
        l.OwnerLastName,
        l.PersonalEmail,
        l.AnymailfinderResult,
        l.LinkedInUrl,
        l.Source,
        l.Status.ToString(),
        l.IsEnriched,
        l.EnrichedAt,
        l.ImportedAt,
        l.CreatedAt,
        l.ImportedByUserId,
        l.OwnerTitle,
        l.Description,
        l.Services,
        l.TargetAudience,
        l.WebsiteStatus.ToString(),
        l.ResolvedUrl,
        l.CrawledAt,
        l.EnrichmentVersion,
        l.PagesCrawled,
        l.ChunksIndexed,
        l.AiSummary,
        l.SalesPitch,
        // KvK enrichment fields
        l.KvkNumber,
        l.VatNumber,
        l.Street,
        l.ZipCode,
        l.EmployeeCount,
        l.BranchCount,
        l.FoundingYear,
        l.LegalForm,
        // Google enrichment fields
        l.GoogleRating,
        l.GoogleReviewCount,
        l.GoogleMapsUrl,
        // Social media fields
        l.FacebookUrl,
        l.InstagramUrl,
        l.TwitterUrl,
        // Business intelligence fields
        l.IsPartOfGroup,
        l.GroupName,
        l.NotableClients,
        l.SalesPriorityScore,
        // Multi-input support fields
        l.ManualInput,
        l.HasUploadedDocuments,
        l.EnrichmentSources,
        // AI Sales Approach
        l.SalesApproach,
        // Owner identity
        l.OwnerLinkedInUrl,
        l.OwnerMobile,
        l.InternalContactName,
        l.InternalContactRole,
        // Operational fields
        l.WorkingArea,
        l.Certifications,
        l.PricingInfo,
        l.OpeningHours,
        // Sales priority label + reasoning
        l.SalesPriorityLabel,
        l.SalesPriorityReasoning,
        // Signals
        l.Signals,
        // Assignment
        l.AssignedToUserId,
        // Pipeline status
        l.PipelineStatus.ToString(),
        // Workflow
        l.WorkflowStep,
        l.WorkflowDataJson,
        // Stale-lead reminder
        l.ReminderDate,
        // Lead-to-client conversion
        l.ConvertedToClientId,
        l.UpdatedAt,
        l.WhyLead,
        l.Needs,
        LeadInsight.ParseFit(l.ProductFitJson)?.Select(e => new ProductFitDto(e.Product, e.Reason, e.EvidenceQuote, e.SourceUrl)).ToList());
}

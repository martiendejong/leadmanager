using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;

namespace LeadManager.Api.Controllers;

[Route("api/leads")]
[ApiController]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly SearchService _search;
    private readonly IConfiguration _configuration;

    public LeadsController(LeadManagerDbContext db, SearchService search, IConfiguration configuration)
    {
        _db = db;
        _search = search;
        _configuration = configuration;
    }

    private string? GetCurrentUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

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
            "importedat"          => filters.SortDesc ? query.OrderByDescending(l => l.ImportedAt)         : query.OrderBy(l => l.ImportedAt),
            "salespriorityscore"  => filters.SortDesc ? query.OrderByDescending(l => l.SalesPriorityScore) : query.OrderBy(l => l.SalesPriorityScore),
            _                     => filters.SortDesc ? query.OrderByDescending(l => l.Name)               : query.OrderBy(l => l.Name),
        };

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

    // POST /api/leads - Create single lead (Task #3, #4)
    [HttpPost]
    public async Task<IActionResult> CreateLead([FromBody] CreateLeadDto dto)
    {
        // Task #4: Validate at least one input type is provided
        var hasWebsite = !string.IsNullOrWhiteSpace(dto.Website);
        var hasManualInput = !string.IsNullOrWhiteSpace(dto.ManualInput);
        // hasDocuments will be checked separately via document upload endpoint

        if (!hasWebsite && !hasManualInput)
        {
            return BadRequest("Vul minimaal een veld in: Website URL of vrije tekst invoer.");
        }

        // Validate ManualInput length (Task #3)
        if (dto.ManualInput != null && dto.ManualInput.Length > 5000)
        {
            return BadRequest("Vrije tekst mag maximaal 5000 tekens bevatten.");
        }

        var userId = GetCurrentUserId();
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

        return CreatedAtAction(nameof(GetLead), new { id = lead.Id }, ToDto(lead));
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

        // Generate sales approach
        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<Services.Enrichment.AiSalesApproachService>>();
        var service = new Services.Enrichment.AiSalesApproachService(_configuration, logger);

        var result = await service.GenerateAsync(lead);

        if (result == null)
        {
            return BadRequest("Sales approach kon niet worden gegenereerd. Controleer of de eigenaarsnaam en contactgegevens bekend zijn.");
        }

        // Save to lead
        lead.SalesApproach = System.Text.Json.JsonSerializer.Serialize(result);
        await _db.SaveChangesAsync();

        return Ok(result);
    }

    // POST /api/leads/import
    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportLeads(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only .xlsx files are supported.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        int imported = 0, skipped = 0, errors = 0;
        var errorDetails = new List<string>();

        using var stream = file.OpenReadStream();
        using var package = new ExcelPackage(stream);

        var worksheet = package.Workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
            return BadRequest("The Excel file contains no worksheets.");

        // Build header map (case-insensitive column name -> column index)
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int col = 1; col <= worksheet.Dimension?.Columns; col++)
        {
            var header = worksheet.Cells[1, col].Text?.Trim();
            if (!string.IsNullOrEmpty(header))
                headerMap[header] = col;
        }

        string GetCell(int row, string[] aliases)
        {
            foreach (var alias in aliases)
                if (headerMap.TryGetValue(alias, out var col))
                    return worksheet.Cells[row, col].Text?.Trim() ?? "";
            return "";
        }

        int totalRows = worksheet.Dimension?.Rows ?? 1;
        var leadsToInsert = new List<Lead>();

        // Load existing (name, website) pairs for dedup check — scoped to this user
        var existingList = await _db.Leads
            .Where(l => l.ImportedByUserId == userId)
            .Select(l => new { NameLower = l.Name.ToLower(), WebsiteLower = l.Website.ToLower() })
            .ToListAsync();
        var existing = existingList.ToHashSet();

        for (int row = 2; row <= totalRows; row++)
        {
            try
            {
                var name = GetCell(row, new[] { "Bedrijfsnaam", "Name", "Company", "Naam" });
                var website = GetCell(row, new[] { "Website" });

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(website))
                    continue; // skip empty rows

                var key = new { NameLower = name.ToLower(), WebsiteLower = website.ToLower() };
                if (existing.Any(e => e.NameLower == key.NameLower && e.WebsiteLower == key.WebsiteLower))
                {
                    skipped++;
                    continue;
                }

                var lead = new Lead
                {
                    Name = name,
                    Website = website,
                    Sector = GetCell(row, new[] { "Sector" }),
                    City = GetCell(row, new[] { "Stad", "City", "Gemeente" }),
                    Phone = GetCell(row, new[] { "Telefoon", "Phone", "Tel" }),
                    CompanyEmail = GetCell(row, new[] { "Email", "CompanyEmail", "Bedrijfsemail" }),
                    Source = GetCell(row, new[] { "Bron", "Source" }),
                    OwnerName = GetCell(row, new[] { "Eigenaar", "OwnerName" }),
                    PersonalEmail = GetCell(row, new[] { "Persoonlijk Email", "PersonalEmail", "Persoonlijke Email" }),
                    ImportedByUserId = userId,
                    ImportedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };

                leadsToInsert.Add(lead);
                existing.Add(new { NameLower = name.ToLower(), WebsiteLower = website.ToLower() });
            }
            catch (Exception ex)
            {
                errors++;
                errorDetails.Add($"Row {row}: {ex.Message}");
            }
        }

        if (leadsToInsert.Count > 0)
        {
            _db.Leads.AddRange(leadsToInsert);
            await _db.SaveChangesAsync();
            imported = leadsToInsert.Count;
        }

        return Ok(new ImportResultDto(imported, skipped, errors, errorDetails));
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
        l.Signals);
}

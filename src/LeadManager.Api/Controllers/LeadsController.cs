using CsvHelper;
using CsvHelper.Configuration;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
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

        // Sorting
        query = filters.SortBy?.ToLower() switch
        {
            "website"   => filters.SortDesc ? query.OrderByDescending(l => l.Website)   : query.OrderBy(l => l.Website),
            "sector"    => filters.SortDesc ? query.OrderByDescending(l => l.Sector)    : query.OrderBy(l => l.Sector),
            "city"      => filters.SortDesc ? query.OrderByDescending(l => l.City)      : query.OrderBy(l => l.City),
            "source"    => filters.SortDesc ? query.OrderByDescending(l => l.Source)    : query.OrderBy(l => l.Source),
            "createdat" => filters.SortDesc ? query.OrderByDescending(l => l.CreatedAt) : query.OrderBy(l => l.CreatedAt),
            "importedat"=> filters.SortDesc ? query.OrderByDescending(l => l.ImportedAt): query.OrderBy(l => l.ImportedAt),
            _           => filters.SortDesc ? query.OrderByDescending(l => l.Name)      : query.OrderBy(l => l.Name),
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

    // POST /api/leads/import — CSV bulk import (max 500 rows)
    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ImportLeadsCsv(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");

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

        if (leadsToInsert.Count > 0)
        {
            _db.Leads.AddRange(leadsToInsert);
            await _db.SaveChangesAsync();
        }

        return Ok(new CsvImportResultDto(leadsToInsert.Count, skipped, rowErrors));
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
                "Naam", "Email", "Telefoon", "Contactpersoon", "Stad", "Sector",
                "KvK", "Medewerkers", "Google Rating", "Sales Score",
                "Verrijkt", "Aangemaakt"
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
                ws.Cells[row, 11].Value = l.IsEnriched ? "Ja" : "Nee";
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
                "naam", "email", "telefoon", "contactpersoon", "stad", "sector",
                "kvk", "medewerkers", "google_rating", "google_reviews",
                "sales_score", "verrijkt", "verrijkingsstatus", "aangemaakt"
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
                csvWriter.WriteField(l.IsEnriched ? "ja" : "nee");
                csvWriter.WriteField(l.IsEnriched ? "verrijkt" : "niet verrijkt");
                csvWriter.WriteField(l.CreatedAt.ToString("yyyy-MM-dd"));
                csvWriter.NextRecord();
            }

            await writer.FlushAsync();
            var csvBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return File(csvBytes, "text/csv; charset=utf-8", $"leads-{dateStr}.csv");
        }
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
        l.Signals,
        // Pipeline status
        l.PipelineStatus.ToString());
}

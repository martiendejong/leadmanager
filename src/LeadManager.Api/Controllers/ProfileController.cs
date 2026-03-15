using System.Security.Claims;
using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly CompanyProfileService _profileService;
    private readonly SmartSearchService _smartSearch;

    public ProfileController(LeadManagerDbContext db, CompanyProfileService profileService, SmartSearchService smartSearch)
    {
        _db = db;
        _profileService = profileService;
        _smartSearch = smartSearch;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // GET /api/profile
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var profile = await _db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == GetUserId());
        if (profile == null) return NotFound();
        return Ok(ToDto(profile));
    }

    // POST /api/profile/generate
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WebsiteUrl))
            return BadRequest("WebsiteUrl is required.");

        var userId = GetUserId();

        try
        {
            var profile = await _profileService.GenerateProfileAsync(request.WebsiteUrl, userId);

            // Upsert
            var existing = await _db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (existing != null)
            {
                existing.WebsiteUrl = profile.WebsiteUrl;
                existing.CompanyName = profile.CompanyName;
                existing.Description = profile.Description;
                existing.WhatTheyDo = profile.WhatTheyDo;
                existing.IdealCustomerProfile = profile.IdealCustomerProfile;
                existing.ToneOfVoice = profile.ToneOfVoice;
                existing.TargetSectorsJson = profile.TargetSectorsJson;
                existing.TargetRegionsJson = profile.TargetRegionsJson;
                existing.KeywordsJson = profile.KeywordsJson;
                existing.UspsJson = profile.UspsJson;
                existing.CrawledAt = profile.CrawledAt;
                existing.ProfileVersion++;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.CompanyProfiles.Add(profile);
            }

            await _db.SaveChangesAsync();
            return Ok(ToDto(existing ?? profile));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // PUT /api/profile
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateProfileRequest request)
    {
        var userId = GetUserId();
        var profile = await _db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null) return NotFound();

        profile.CompanyName = request.CompanyName;
        profile.Description = request.Description;
        profile.WhatTheyDo = request.WhatTheyDo;
        profile.IdealCustomerProfile = request.IdealCustomerProfile;
        profile.ToneOfVoice = request.ToneOfVoice;
        profile.TargetSectorsJson = JsonSerializer.Serialize(request.TargetSectors);
        profile.TargetRegionsJson = JsonSerializer.Serialize(request.TargetRegions);
        profile.KeywordsJson = JsonSerializer.Serialize(request.Keywords);
        profile.UspsJson = JsonSerializer.Serialize(request.Usps);
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(ToDto(profile));
    }

    // POST /api/profile/search
    [HttpPost("search")]
    public async Task<IActionResult> Search()
    {
        var userId = GetUserId();
        var profile = await _db.CompanyProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null)
            return BadRequest(new { message = "Maak eerst een bedrijfsprofiel aan." });

        // Build dedup set from existing user leads (name|domain)
        var existingLeads = await _db.Leads
            .Where(l => l.ImportedByUserId == userId)
            .Select(l => new { l.Name, l.Website })
            .ToListAsync();
        var existingKeys = existingLeads
            .Select(l => SmartSearchService.MakeKey(l.Name, l.Website))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = await _smartSearch.SearchAndQualifyAsync(profile, existingKeys);

        return Ok(results.Select(r => new
        {
            name = r.Name,
            website = r.Website,
            city = r.City,
            sector = r.Sector,
            phone = r.Phone,
            email = r.Email,
            source = r.Source,
            confidenceScore = r.ConfidenceScore,
            qualificationReason = r.QualificationReason,
            ownerName = r.OwnerName,
            description = r.Description,
            services = r.Services,
            targetAudience = r.TargetAudience
        }));
    }

    private static object ToDto(CompanyProfile p) => new
    {
        id = p.Id,
        websiteUrl = p.WebsiteUrl,
        companyName = p.CompanyName,
        description = p.Description,
        whatTheyDo = p.WhatTheyDo,
        idealCustomerProfile = p.IdealCustomerProfile,
        toneOfVoice = p.ToneOfVoice,
        targetSectors = JsonSerializer.Deserialize<string[]>(p.TargetSectorsJson) ?? [],
        targetRegions = JsonSerializer.Deserialize<string[]>(p.TargetRegionsJson) ?? [],
        keywords = JsonSerializer.Deserialize<string[]>(p.KeywordsJson) ?? [],
        usps = JsonSerializer.Deserialize<string[]>(p.UspsJson) ?? [],
        crawledAt = p.CrawledAt,
        profileVersion = p.ProfileVersion,
        updatedAt = p.UpdatedAt
    };
}

public record GenerateProfileRequest(string WebsiteUrl);
public record UpdateProfileRequest(
    string CompanyName,
    string Description,
    string WhatTheyDo,
    string IdealCustomerProfile,
    string ToneOfVoice,
    string[] TargetSectors,
    string[] TargetRegions,
    string[] Keywords,
    string[] Usps);

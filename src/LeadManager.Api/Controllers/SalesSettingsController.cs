using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/sales-settings")]
public class SalesSettingsController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly ScriptGeneratorService _scriptGenerator;

    public SalesSettingsController(LeadManagerDbContext db, ScriptGeneratorService scriptGenerator)
    {
        _db = db;
        _scriptGenerator = scriptGenerator;
    }

    [HttpGet]
    public async Task<ActionResult<SalesSettingsDto>> Get()
    {
        var userId = GetUserId();
        var settings = await GetOrCreateSettings(userId);
        return Ok(MapToDto(settings));
    }

    [HttpPut]
    public async Task<ActionResult<SalesSettingsDto>> Update([FromBody] UpdateSalesSettingsDto dto)
    {
        var userId = GetUserId();
        var settings = await GetOrCreateSettings(userId);

        settings.CompanyName = dto.CompanyName;
        settings.CompanyAddress = dto.CompanyAddress;
        settings.CompanyCity = dto.CompanyCity;
        settings.CompanyZipCode = dto.CompanyZipCode;
        settings.CompanyKvk = dto.CompanyKvk;
        settings.CompanyVat = dto.CompanyVat;
        settings.CompanyIban = dto.CompanyIban;
        settings.CompanyEmail = dto.CompanyEmail;
        settings.CompanyPhone = dto.CompanyPhone;
        settings.CompanyWebsite = dto.CompanyWebsite;

        settings.StarterHours = dto.StarterHours;
        settings.StarterMonthlyPrice = dto.StarterMonthlyPrice;
        settings.TeamHours = dto.TeamHours;
        settings.TeamMonthlyPrice = dto.TeamMonthlyPrice;
        settings.BundleHourlyRate = dto.BundleHourlyRate;
        settings.LooseHourlyRate = dto.LooseHourlyRate;
        settings.OverageHourlyRate = dto.OverageHourlyRate;

        settings.QuoteNumberPrefix = dto.QuoteNumberPrefix;
        settings.QuoteNumberCurrent = dto.QuoteNumberCurrent;
        settings.QuoteValidityDays = dto.QuoteValidityDays;
        settings.QuoteIntroText = dto.QuoteIntroText;
        settings.QuoteTerms = dto.QuoteTerms;
        settings.QuoteFooterText = dto.QuoteFooterText;

        settings.CallScriptSectionsJson = dto.CallScriptSectionsJson;
        settings.EmailSubjectTemplate = dto.EmailSubjectTemplate;
        settings.EmailBodyTemplate = dto.EmailBodyTemplate;
        settings.ClientEngagementRules = dto.ClientEngagementRules;

        settings.FedhaBaseUrl = dto.FedhaBaseUrl;
        settings.FedhaApiKey = dto.FedhaApiKey;
        settings.FedhaDefaultProjectId = dto.FedhaDefaultProjectId;

        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(MapToDto(settings));
    }

    [HttpPost("generate-script")]
    public async Task<ActionResult<GenerateScriptResponse>> GenerateScript([FromBody] GenerateScriptRequest req)
    {
        var userId = GetUserId();
        var lead = await _db.Leads.FindAsync(req.LeadId);
        if (lead == null) return NotFound("Lead niet gevonden");

        var settings = await GetOrCreateSettings(userId);
        var script = await _scriptGenerator.GenerateAsync(lead, settings, req.ExtraContext);

        return Ok(new GenerateScriptResponse(script, lead.Name));
    }

    private async Task<SalesSettings> GetOrCreateSettings(string userId)
    {
        var settings = await _db.SalesSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings == null)
        {
            settings = new SalesSettings
            {
                UserId = userId,
                CompanyName = "Prospergenics",
                ClientEngagementRules = SalesSettings.DefaultClientEngagementRules
            };
            _db.SalesSettings.Add(settings);
            await _db.SaveChangesAsync();
        }
        return settings;
    }

    private static SalesSettingsDto MapToDto(SalesSettings s) => new(
        s.Id, s.CompanyName, s.CompanyAddress, s.CompanyCity, s.CompanyZipCode,
        s.CompanyKvk, s.CompanyVat, s.CompanyIban, s.CompanyEmail, s.CompanyPhone, s.CompanyWebsite,
        s.StarterHours, s.StarterMonthlyPrice, s.TeamHours, s.TeamMonthlyPrice,
        s.BundleHourlyRate, s.LooseHourlyRate, s.OverageHourlyRate,
        s.QuoteNumberPrefix, s.QuoteNumberCurrent, s.QuoteValidityDays,
        s.QuoteIntroText, s.QuoteTerms, s.QuoteFooterText,
        s.CallScriptSectionsJson, s.EmailSubjectTemplate, s.EmailBodyTemplate,
        s.ClientEngagementRules ?? SalesSettings.DefaultClientEngagementRules,
        s.FedhaBaseUrl, s.FedhaApiKey, s.FedhaDefaultProjectId
    );

    private string GetUserId() =>
        User.Claims.First(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier).Value;
}

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
[Route("api/offerte")]
public class OfferteController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly OfferteService _offerte;

    public OfferteController(LeadManagerDbContext db, OfferteService offerte)
    {
        _db = db;
        _offerte = offerte;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateQuoteRequest req)
    {
        var userId = GetUserId();

        var lead = await _db.Leads.FindAsync(req.LeadId);
        if (lead == null) return NotFound("Lead niet gevonden");

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.SourceLeadId == req.LeadId)
            ?? new Client { Name = lead.Name, City = lead.City ?? "", PrimaryContactName = lead.OwnerName };

        var settings = await _db.SalesSettings.FirstOrDefaultAsync(s => s.UserId == userId)
            ?? new SalesSettings { UserId = userId, CompanyName = "Prospergenics" };

        var productType = Enum.TryParse<IntakeProductType>(req.ProductType, true, out var pt) ? pt : IntakeProductType.Custom;
        var bundleType = Enum.TryParse<IntakeBundleType>(req.BundleType, true, out var bt) ? bt : IntakeBundleType.Starter;

        var lineItems = BuildLineItems(productType, bundleType, settings, req);

        var quoteNumber = $"{settings.QuoteNumberPrefix}-{settings.QuoteNumberCurrent:D4}";
        settings.QuoteNumberCurrent++;
        await _db.SaveChangesAsync();

        var data = new OfferteData
        {
            QuoteNumber = quoteNumber,
            Date = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(settings.QuoteValidityDays),
            Client = client,
            Lead = lead,
            Settings = settings,
            LineItems = lineItems,
            CustomNote = req.CustomDescription
        };

        var pdf = _offerte.Generate(data);

        return File(pdf, "application/pdf", $"Offerte-{lead.Name.Replace(" ", "_")}-{quoteNumber}.pdf");
    }

    private static List<OfferteLineItem> BuildLineItems(
        IntakeProductType product, IntakeBundleType bundle,
        SalesSettings s, GenerateQuoteRequest req)
    {
        var items = new List<OfferteLineItem>();

        if (req.CustomHours.HasValue && req.CustomPrice.HasValue)
        {
            items.Add(new OfferteLineItem
            {
                Description = req.CustomDescription ?? "Maatwerkoplossing",
                Hours = req.CustomHours.Value,
                UnitPrice = req.CustomPrice.Value / req.CustomHours.Value
            });
            return items;
        }

        var productLabel = product switch
        {
            IntakeProductType.Website => "Website",
            IntakeProductType.AIEmployee => "AI Medewerker",
            IntakeProductType.AITeam => "AI Team",
            IntakeProductType.Custom => "Maatwerkoplossing",
            _ => "Maatwerkoplossing"
        };

        if (bundle == IntakeBundleType.Starter)
        {
            items.Add(new OfferteLineItem
            {
                Description = $"{productLabel} — Starter Bundle",
                SubDescription = $"{s.StarterHours}u/maand · onboarding, configuratie en ondersteuning inbegrepen",
                Hours = s.StarterHours,
                UnitPrice = s.BundleHourlyRate
            });
        }
        else if (bundle == IntakeBundleType.Team)
        {
            items.Add(new OfferteLineItem
            {
                Description = $"{productLabel} — Team Bundle",
                SubDescription = $"{s.TeamHours}u/maand · volledige integratie, training en prioriteit support",
                Hours = s.TeamHours,
                UnitPrice = s.BundleHourlyRate
            });
        }
        else
        {
            items.Add(new OfferteLineItem
            {
                Description = $"{productLabel} — Pay-per-Hour",
                SubDescription = "Afrekenen per uur, geen maandelijkse verplichting",
                Hours = 20,
                UnitPrice = s.LooseHourlyRate
            });
        }

        return items;
    }

    private string GetUserId() =>
        User.Claims.First(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier).Value;
}

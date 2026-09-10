using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LeadManager.Api.Services;

public class ScriptGeneratorService
{
    private readonly LeadManagerDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ScriptGeneratorService> _logger;
    private readonly HttpClient _http;

    private static readonly string DefaultSections = JsonSerializer.Serialize(new[]
    {
        new { id = "opening",    title = "Opening",              prompt = "Schrijf een korte, directe opening. Stel jezelf voor, noem het bedrijf en zeg in één zin waarom je belt." },
        new { id = "aanleiding", title = "Aanleiding",           prompt = "Benoem een specifieke observatie over dit bedrijf (sector, groei, website, signalen) die je belt aanleiding geeft." },
        new { id = "pijn",       title = "Probleemverkenning",   prompt = "Schrijf 2-3 open vragen om de pijn te achterhalen. Gericht op capaciteit, kosten, tijdgebrek of technologie." },
        new { id = "oplossing",  title = "Oplossing positioneren", prompt = "Positioneer het AI-product als oplossing voor de vermoedelijke pijn. Concreet en kort." },
        new { id = "demo",       title = "Demo CTA",              prompt = "Vraag om een 20-minuten demo. Bied twee tijdsloten aan. Geen druk." },
        new { id = "bezwaar",    title = "Bezwaar-handling",      prompt = "Geef 3 korte antwoorden op typische bezwaren: te duur, geen tijd, werkt niet voor ons." },
        new { id = "afsluiting", title = "Afsluiting",           prompt = "Sluit het gesprek professioneel af, ongeacht of er een afspraak is of niet." },
    });

    public ScriptGeneratorService(
        LeadManagerDbContext db,
        IConfiguration config,
        ILogger<ScriptGeneratorService> logger,
        HttpClient http)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _http = http;
    }

    public async Task<string> GenerateAsync(Lead lead, SalesSettings settings, string? extraContext)
    {
        var sectionsJson = settings.CallScriptSectionsJson ?? DefaultSections;
        var sections = JsonSerializer.Deserialize<JsonElement[]>(sectionsJson) ?? [];

        var leadContext = BuildLeadContext(lead);
        var apiKey = _config["OpenAI:ApiKey"];

        if (string.IsNullOrEmpty(apiKey))
            return GenerateFallback(lead, sections, settings);

        try
        {
            return await GenerateWithOpenAiAsync(apiKey, lead, leadContext, sections, settings, extraContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI script generation failed, using fallback");
            return GenerateFallback(lead, sections, settings);
        }
    }

    private string BuildLeadContext(Lead lead)
    {
        var parts = new List<string>();
        parts.Add($"Bedrijf: {lead.Name}");
        if (!string.IsNullOrEmpty(lead.Sector)) parts.Add($"Sector: {lead.Sector}");
        if (!string.IsNullOrEmpty(lead.City)) parts.Add($"Stad: {lead.City}");
        if (!string.IsNullOrEmpty(lead.OwnerName)) parts.Add($"Contactpersoon: {lead.OwnerName}");
        if (!string.IsNullOrEmpty(lead.OwnerTitle)) parts.Add($"Functie: {lead.OwnerTitle}");
        if (!string.IsNullOrEmpty(lead.Description)) parts.Add($"Over het bedrijf: {lead.Description}");
        if (!string.IsNullOrEmpty(lead.Services)) parts.Add($"Diensten: {lead.Services}");
        if (!string.IsNullOrEmpty(lead.AiSummary)) parts.Add($"AI-samenvatting: {lead.AiSummary}");
        if (!string.IsNullOrEmpty(lead.SalesPitch)) parts.Add($"Sales pitch: {lead.SalesPitch}");
        if (lead.GoogleRating.HasValue) parts.Add($"Google: {lead.GoogleRating}/5 ({lead.GoogleReviewCount} reviews)");
        if (lead.EmployeeCount != null) parts.Add($"Medewerkers: {lead.EmployeeCount}");
        if (lead.FoundingYear.HasValue) parts.Add($"Opgericht: {lead.FoundingYear}");
        return string.Join("\n", parts);
    }

    private async Task<string> GenerateWithOpenAiAsync(
        string apiKey, Lead lead, string leadContext,
        JsonElement[] sections, SalesSettings settings, string? extraContext)
    {
        var sectionPrompts = string.Join("\n", sections.Select((s, i) =>
        {
            var title = s.TryGetProperty("title", out var t) ? t.GetString() : $"Sectie {i + 1}";
            var prompt = s.TryGetProperty("prompt", out var p) ? p.GetString() : "";
            return $"## {title}\n{prompt}";
        }));

        var systemPrompt =
            $"Je bent een ervaren B2B sales consultant voor een AI-bedrijf genaamd {settings.CompanyName}. " +
            "Schrijf een concreet, persoonlijk belscript in het Nederlands. " +
            "Gebruik de bedrijfsinformatie voor specifieke, niet-generieke tekst. " +
            "Toon: direct, vriendelijk, geen verkooppraat. " +
            "Structuur per sectie met ##-koppen. Elke sectie max 4 zinnen. Geen opsommingen tenzij gevraagd.";

        var userPrompt =
            $"Bedrijfsinformatie:\n{leadContext}" +
            (string.IsNullOrEmpty(extraContext) ? "" : $"\n\nExtra context van de verkoper:\n{extraContext}") +
            $"\n\nSchrijf een belscript met deze secties:\n{sectionPrompts}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = JsonContent.Create(new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.7
        });

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static string GenerateFallback(Lead lead, JsonElement[] sections, SalesSettings settings)
    {
        var lines = new List<string>();
        lines.Add($"# Belscript — {lead.Name}");
        lines.Add($"_Contactpersoon: {(string.IsNullOrEmpty(lead.OwnerName) ? "Onbekend" : lead.OwnerName)}_");
        lines.Add($"_Telefoon: {lead.Phone}_");
        lines.Add("");

        foreach (var section in sections)
        {
            var title = section.TryGetProperty("title", out var t) ? t.GetString() : "Sectie";
            lines.Add($"## {title}");
            lines.Add($"_[Vul hier in op basis van: {lead.Name}, {lead.Sector}, {lead.City}]_");
            lines.Add("");
        }

        lines.Add($"---");
        lines.Add($"Aanbieder: {settings.CompanyName} | Starter {settings.StarterHours}u/€{settings.StarterMonthlyPrice}pm · Team {settings.TeamHours}u/€{settings.TeamMonthlyPrice}pm");
        return string.Join("\n", lines);
    }
}

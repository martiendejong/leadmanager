using System.Text;
using System.Text.Json;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Profile;

/// <summary>
/// Generates company leads directly via GPT — no web scraping, no bot detection issues.
/// Each call produces a batch of real companies matching a specific search angle.
/// </summary>
public class GptLeadGeneratorService
{
    private readonly HttpClient _http;
    private readonly ILogger<GptLeadGeneratorService> _logger;

    public GptLeadGeneratorService(IConfiguration configuration, ILogger<GptLeadGeneratorService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _logger = logger;
    }

    /// <summary>
    /// Generate a batch of company leads for the given profile + angle.
    /// An angle is a specific focus (e.g. "juridische bureaus in Noord-Holland", "MKB tech Rotterdam").
    /// </summary>
    public async Task<List<LeadSearchResult>> GenerateBatchAsync(CompanyProfile profile, string angle, int count = 25)
    {
        var sectors = JsonSerializer.Deserialize<string[]>(profile.TargetSectorsJson) ?? [];
        var regions = JsonSerializer.Deserialize<string[]>(profile.TargetRegionsJson) ?? [];

        var prompt = $@"Je bent een lead generation expert voor Nederlandse B2B bedrijven.

Bedrijfsprofiel van de klant die leads zoekt:
Naam: {profile.CompanyName}
Wat ze doen: {profile.WhatTheyDo}
Ideale klant (ICP): {profile.IdealCustomerProfile}
Doelsectoren: {string.Join(", ", sectors)}
Doelregio's: {string.Join(", ", regions)}

Zoekinvalshoek voor deze batch: {angle}

Genereer exact {count} ECHTE Nederlandse bedrijven die potentiële klanten zijn voor bovenstaand bedrijf.

Vereisten:
- Gebruik ALLEEN bestaande, verifieerbare Nederlandse bedrijven
- Bedrijven moeten passen bij de ICP en doelsectoren
- Varieer over regio's (niet allemaal Amsterdam)
- Geef realistische websites (domeinen die echt bestaan)
- Beschrijving gebaseerd op wat je weet over het bedrijf
- Als je het exacte telefoonnummer niet weet, laat dan leeg

JSON antwoord (array van exact {count} bedrijven):
{{""leads"": [
  {{
    ""name"": ""Bedrijfsnaam BV"",
    ""website"": ""https://www.bedrijf.nl"",
    ""city"": ""Amsterdam"",
    ""sector"": ""IT dienstverlening"",
    ""phone"": """",
    ""ownerName"": ""Jan de Vries"",
    ""description"": ""Korte beschrijving van wat dit bedrijf doet"",
    ""services"": ""Hoofddiensten of producten"",
    ""targetAudience"": ""Voor wie werken ze""
  }}
]}}";

        try
        {
            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new[] { new { role = "user", content = prompt } },
                response_format = new { type = "json_object" },
                max_tokens = 4000
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("https://api.openai.com/v1/chat/completions", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var resultDoc = JsonDocument.Parse(text);
            var results = new List<LeadSearchResult>();

            if (resultDoc.RootElement.TryGetProperty("leads", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var website = item.TryGetProperty("website", out var w) ? w.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(website)) continue;

                    var city = item.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                    var sector = item.TryGetProperty("sector", out var s) ? s.GetString() ?? "" : "";
                    var phone = item.TryGetProperty("phone", out var p) ? p.GetString() ?? "" : "";
                    var ownerName = item.TryGetProperty("ownerName", out var ow) && ow.ValueKind == JsonValueKind.String ? ow.GetString() : null;
                    var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    var services = item.TryGetProperty("services", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;
                    var targetAudience = item.TryGetProperty("targetAudience", out var ta) && ta.ValueKind == JsonValueKind.String ? ta.GetString() : null;

                    results.Add(new LeadSearchResult(name, website, city, sector, phone, "", "ai-generated",
                        "", ownerName, description, services, targetAudience));
                }
            }

            _logger.LogInformation("[GptGenerator] Angle '{Angle}' → {Count} leads", angle, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GptGenerator] Batch failed for angle '{Angle}': {Error}", angle, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Generate diverse search angles for the given profile.
    /// More angles = more variety and more total leads.
    /// </summary>
    public async Task<List<string>> GenerateAnglesAsync(CompanyProfile profile, int count = 20)
    {
        var sectors = JsonSerializer.Deserialize<string[]>(profile.TargetSectorsJson) ?? [];
        var regions = JsonSerializer.Deserialize<string[]>(profile.TargetRegionsJson) ?? [];
        var keywords = JsonSerializer.Deserialize<string[]>(profile.KeywordsJson) ?? [];

        var prompt = $@"Je bent een lead generation strategist.

Bedrijfsprofiel:
Naam: {profile.CompanyName}
Wat ze doen: {profile.WhatTheyDo}
Ideale klant: {profile.IdealCustomerProfile}
Doelsectoren: {string.Join(", ", sectors)}
Doelregio's: {string.Join(", ", regions)}
Keywords: {string.Join(", ", keywords)}

Genereer exact {count} diverse invalshoeken om leads te vinden. Elke invalshoek is een specifieke focus:
- Combineer sector + regio
- Varieer op bedrijfsgrootte (MKB, enterprise, ZZP)
- Varieer op pijnpunt of behoefte
- Varieer op specifieke niche of subsector
- Denk aan aanpalende sectoren die ook baat hebben bij het aanbod

Geef antwoord als JSON:
{{""angles"": [""angle1"", ""angle2"", ...]}}";

        try
        {
            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new[] { new { role = "user", content = prompt } },
                response_format = new { type = "json_object" },
                max_tokens = 800
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("https://api.openai.com/v1/chat/completions", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var resultDoc = JsonDocument.Parse(text);
            if (resultDoc.RootElement.TryGetProperty("angles", out var arr))
                return arr.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GptGenerator] Failed to generate angles: {Error}", ex.Message);
        }

        return [];
    }
}

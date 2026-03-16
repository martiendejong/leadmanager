using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class AiSalesApproachService
{
    private readonly HttpClient _http;
    private readonly ILogger<AiSalesApproachService> _logger;

    public AiSalesApproachService(IConfiguration configuration, ILogger<AiSalesApproachService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _logger = logger;
    }

    public async Task<SalesApproachResult?> GenerateAsync(Lead lead)
    {
        // Only generate if owner name is known AND at least one contact method available
        if (string.IsNullOrWhiteSpace(lead.OwnerName))
        {
            _logger.LogInformation("Skipping sales approach generation - no owner name for lead {LeadId}", lead.Id);
            return null;
        }

        var hasContact = !string.IsNullOrWhiteSpace(lead.Phone) ||
                        !string.IsNullOrWhiteSpace(lead.PersonalEmail) ||
                        !string.IsNullOrWhiteSpace(lead.LinkedInUrl);

        if (!hasContact)
        {
            _logger.LogInformation("Skipping sales approach generation - no contact info for lead {LeadId}", lead.Id);
            return null;
        }

        try
        {
            // Determine language based on city
            var isNL = IsNetherlandsCity(lead.City);
            var language = isNL ? "Nederlands" : "Engels";

            var prompt = BuildPrompt(lead, language);
            var response = await CallClaudeAsync(prompt);

            var result = JsonSerializer.Deserialize<SalesApproachResult>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null)
            {
                _logger.LogInformation("Generated sales approach for lead {LeadId} in {Language}", lead.Id, language);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate sales approach for lead {LeadId}", lead.Id);
            return null;
        }
    }

    private string BuildPrompt(Lead lead, string language)
    {
        return $@"Genereer 3 verkoop-openers voor dit bedrijf in {language}.

BEDRIJFSINFORMATIE:
Naam: {lead.Name}
Eigenaar: {lead.OwnerName}
Functie: {lead.OwnerTitle ?? "Onbekend"}
Sector: {lead.Sector}
Stad: {lead.City}
Beschrijving: {lead.Description ?? "Geen beschrijving"}
Diensten: {lead.Services ?? "Onbekend"}

CONTACTGEGEVENS:
LinkedIn: {(string.IsNullOrWhiteSpace(lead.LinkedInUrl) ? "Niet beschikbaar" : "Beschikbaar")}
Telefoon: {(string.IsNullOrWhiteSpace(lead.Phone) ? "Niet beschikbaar" : "Beschikbaar")}
Email: {(string.IsNullOrWhiteSpace(lead.PersonalEmail) ? "Niet beschikbaar" : "Beschikbaar")}

Maak 3 varianten:
1. LinkedIn bericht (max 300 karakters, persoonlijk, relevant)
2. Telefoon opener (1 zin, direct, vriendelijk)
3. Email intro (2-3 zinnen, professioneel, waardevol)

Gebruik de bedrijfsinformatie om het persoonlijk te maken.
Vermijd generieke zinnen.
Toon begrip van hun sector.

Geef het resultaat als JSON in dit formaat:
{{
  ""linkedinMessage"": ""...(max 300 tekens)"",
  ""phoneOpener"": ""...(1 zin)"",
  ""emailIntro"": ""...(2-3 zinnen)""
}}

Retourneer ALLEEN de JSON, geen extra tekst.";
    }

    private async Task<string> CallClaudeAsync(string prompt)
    {
        var requestBody = new
        {
            model = "claude-3-5-sonnet-20241022",
            max_tokens = 1000,
            temperature = 0.7,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        var response = await _http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var content = result.GetProperty("content")[0].GetProperty("text").GetString();

        return content ?? "{}";
    }

    private static bool IsNetherlandsCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return true; // Default to NL

        var nlCities = new[]
        {
            "amsterdam", "rotterdam", "den haag", "utrecht", "eindhoven", "tilburg", "groningen",
            "almere", "breda", "nijmegen", "enschede", "haarlem", "arnhem", "zaanstad", "amersfoort",
            "apeldoorn", "s-hertogenbosch", "hoofddorp", "maastricht", "leiden", "dordrecht", "zoetermeer"
        };

        return nlCities.Any(c => city.Contains(c, StringComparison.OrdinalIgnoreCase));
    }
}

public class SalesApproachResult
{
    public string? LinkedinMessage { get; set; }
    public string? PhoneOpener { get; set; }
    public string? EmailIntro { get; set; }
}

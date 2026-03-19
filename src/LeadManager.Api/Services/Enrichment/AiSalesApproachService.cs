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
        var hasContact = !string.IsNullOrWhiteSpace(lead.Phone) ||
                        !string.IsNullOrWhiteSpace(lead.OwnerMobile) ||
                        !string.IsNullOrWhiteSpace(lead.PersonalEmail) ||
                        !string.IsNullOrWhiteSpace(lead.LinkedInUrl) ||
                        !string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl);

        if (!hasContact)
        {
            _logger.LogInformation("Skipping sales approach generation - no contact info for lead {LeadId}", lead.Id);
            return null;
        }

        try
        {
            var isNL = IsNetherlandsCity(lead.City);
            var language = isNL ? "Nederlands" : "Engels";
            var prompt = BuildPrompt(lead, language);
            var response = await CallClaudeAsync(prompt);

            var result = JsonSerializer.Deserialize<SalesApproachResult>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null)
                _logger.LogInformation("Generated sales approach for lead {LeadId} in {Language}", lead.Id, language);

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
        var ownerKnown = !string.IsNullOrWhiteSpace(lead.OwnerName);
        var hasLinkedIn = !string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl) || !string.IsNullOrWhiteSpace(lead.LinkedInUrl);
        var hasPhone = !string.IsNullOrWhiteSpace(lead.OwnerMobile) || (!string.IsNullOrWhiteSpace(lead.Phone) && lead.Phone.Contains("06"));
        var hasEmail = !string.IsNullOrWhiteSpace(lead.PersonalEmail);

        var channelPriority = hasLinkedIn ? "linkedin" : hasPhone ? "phone" : "email";

        var warnings = new List<string>();
        if (!hasPhone) warnings.Add("Geen mobiel nummer gevonden");
        if (!hasEmail) warnings.Add("Geen persoonlijk email gevonden");
        if (!ownerKnown) warnings.Add("Eigenaar onbekend — gebruik receptionist/office script");

        return $@"Genereer een gepersonaliseerde sales approach voor dit bedrijf in {language}.

BEDRIJFSINFORMATIE:
Naam: {lead.Name}
Eigenaar: {(ownerKnown ? lead.OwnerName : "Onbekend")}
Functie: {lead.OwnerTitle ?? "Onbekend"}
Sector: {lead.Sector}
Stad: {lead.City}
Beschrijving: {lead.Description ?? "Geen beschrijving"}
Diensten: {lead.Services ?? "Onbekend"}
Werkgebied: {lead.WorkingArea ?? "Onbekend"}

CONTACTGEGEVENS:
LinkedIn eigenaar: {(string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl) ? "Niet beschikbaar" : "Beschikbaar")}
LinkedIn bedrijf: {(string.IsNullOrWhiteSpace(lead.LinkedInUrl) ? "Niet beschikbaar" : "Beschikbaar")}
Mobiel: {(hasPhone ? "Beschikbaar" : "Niet beschikbaar")}
Email: {(hasEmail ? "Beschikbaar" : "Niet beschikbaar")}

Aanbevolen eerste kanaal: {channelPriority}

Genereer een volledig sales approach in dit JSON-formaat:
{{
  ""recommendedChannel"": ""{channelPriority}"",
  ""channelSequence"": [""{channelPriority}""{(channelPriority == "linkedin" && hasPhone ? ", \"phone\"" : channelPriority == "linkedin" && hasEmail ? ", \"email\"" : "")}],
  ""personalHook"": ""...(1 zin met persoonlijke kapstok, bijv. sector-inzicht of bedrijfsdetail)"",
  ""linkedinMessage"": ""...(max 300 tekens, persoonlijk, relevant, geen generieke opener)"",
  ""phoneOpener"": ""...(1 zin, direct, vriendelijk, bij voorkeur met naam eigenaar)"",
  ""emailIntro"": ""...(2-3 zinnen, professioneel, waardegericht)"",
  ""whatsappMessage"": ""...(max 160 tekens, informeel maar zakelijk)"",
  ""unknownOwnerScript"": ""{(ownerKnown ? "N/A — eigenaar is bekend" : "...(telefoonscript voor als je de eigenaar niet kent, vraag naar de juiste persoon)")}"",
  ""callerWarnings"": {JsonSerializer.Serialize(warnings)}
}}

Regels:
- Gebruik de bedrijfsinformatie om het echt persoonlijk te maken
- Vermijd generieke zinnen zoals ""Ik zag uw website""
- Noem sector-specifieke inzichten
- Retourneer ALLEEN de JSON, geen extra tekst";
    }

    private async Task<string> CallClaudeAsync(string prompt)
    {
        var requestBody = new
        {
            model = "claude-3-5-sonnet-20241022",
            max_tokens = 1200,
            temperature = 0.7,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await _http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var content = result.GetProperty("content")[0].GetProperty("text").GetString();
        return content ?? "{}";
    }

    private static bool IsNetherlandsCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return true;
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
    public string? RecommendedChannel { get; set; }
    public List<string>? ChannelSequence { get; set; }
    public string? PersonalHook { get; set; }
    public string? LinkedinMessage { get; set; }
    public string? PhoneOpener { get; set; }
    public string? EmailIntro { get; set; }
    public string? WhatsappMessage { get; set; }
    public string? UnknownOwnerScript { get; set; }
    public List<string>? CallerWarnings { get; set; }
}

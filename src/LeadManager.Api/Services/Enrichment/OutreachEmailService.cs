using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public record OutreachEmailVariant(string Style, string Subject, string Body);
public record OutreachEmailResult(List<OutreachEmailVariant> Variants);

public class OutreachEmailService
{
    private readonly HttpClient _http;
    private readonly ILogger<OutreachEmailService> _logger;

    public OutreachEmailService(IConfiguration configuration, ILogger<OutreachEmailService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _logger = logger;
    }

    public async Task<OutreachEmailResult?> GenerateAsync(Lead lead)
    {
        try
        {
            var prompt = BuildPrompt(lead);
            var response = await CallClaudeAsync(prompt);

            var variants = JsonSerializer.Deserialize<List<OutreachEmailVariant>>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (variants != null && variants.Count > 0)
            {
                _logger.LogInformation("Generated outreach email for lead {LeadId}", lead.Id);
                return new OutreachEmailResult(variants);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate outreach email for lead {LeadId}", lead.Id);
            return null;
        }
    }

    private string BuildPrompt(Lead lead)
    {
        var ownerName = !string.IsNullOrWhiteSpace(lead.OwnerFirstName)
            ? lead.OwnerFirstName
            : !string.IsNullOrWhiteSpace(lead.OwnerName)
                ? lead.OwnerName.Split(' ')[0]
                : null;

        var googleRatingInfo = lead.GoogleRating.HasValue
            ? $"Google rating: {lead.GoogleRating:F1}/5 ({lead.GoogleReviewCount ?? 0} reviews)"
            : "Geen Google rating beschikbaar";

        return $@"Genereer 3 outreach e-mailvarianten voor dit bedrijf in het Nederlands. Elke variant heeft een eigen stijl.

BEDRIJFSINFORMATIE:
Naam: {lead.Name}
{(ownerName != null ? $"Contactpersoon: {ownerName}" : "Contactpersoon: onbekend")}
Sector: {(string.IsNullOrWhiteSpace(lead.Sector) ? "Onbekend" : lead.Sector)}
Stad: {(string.IsNullOrWhiteSpace(lead.City) ? "Onbekend" : lead.City)}
{googleRatingInfo}
Website: {(string.IsNullOrWhiteSpace(lead.Website) ? "Niet beschikbaar" : lead.Website)}
Beschrijving: {(string.IsNullOrWhiteSpace(lead.Description) ? "Geen beschrijving" : lead.Description)}
Diensten: {(string.IsNullOrWhiteSpace(lead.Services) ? "Onbekend" : lead.Services)}
Medewerkers: {(string.IsNullOrWhiteSpace(lead.EmployeeCount) ? "Onbekend" : lead.EmployeeCount)}

INSTRUCTIES:
Genereer precies 3 varianten in dit JSON-formaat (retourneer ALLEEN de JSON array, geen extra tekst):
[
  {{
    ""style"": ""Zakelijk"",
    ""subject"": ""...(professioneel onderwerpregel)"",
    ""body"": ""...(formele email, max 150 woorden, zakelijk en professioneel)""
  }},
  {{
    ""style"": ""Vriendelijk"",
    ""subject"": ""...(warme onderwerpregel)"",
    ""body"": ""...(casual en warm, max 150 woorden, persoonlijk en toegankelijk)""
  }},
  {{
    ""style"": ""Direct"",
    ""subject"": ""...(prikkelende onderwerpregel)"",
    ""body"": ""...(direct en provocatief, max 150 woorden, to the point en uitdagend)""
  }}
]

Regels:
- Schrijf in het Nederlands
- Gebruik de bedrijfsnaam en sector om het persoonlijk te maken
- {(ownerName != null ? $"Spreek de contactpersoon aan met voornaam: {ownerName}" : "Gebruik een algemene aanspreking zoals 'Goedendag'")}
- Vermijd generieke openers zoals 'Ik schrijf u over...'
- Elke email eindigt met een concrete call-to-action
- Max 150 woorden per email body
- Retourneer ALLEEN de JSON array, geen extra tekst of uitleg";
    }

    private async Task<string> CallClaudeAsync(string prompt)
    {
        var requestBody = new
        {
            model = "claude-3-5-sonnet-20241022",
            max_tokens = 1500,
            temperature = 0.7,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await _http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var content = result.GetProperty("content")[0].GetProperty("text").GetString();
        return content ?? "[]";
    }
}

using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class TextInputEnrichmentService
{
    private readonly HttpClient _http;
    private readonly ILogger<TextInputEnrichmentService> _logger;

    public TextInputEnrichmentService(IConfiguration configuration, ILogger<TextInputEnrichmentService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _logger = logger;
    }

    public async Task<TextEnrichmentResult> EnrichFromTextAsync(Lead lead)
    {
        if (string.IsNullOrWhiteSpace(lead.ManualInput))
        {
            return new TextEnrichmentResult();
        }

        try
        {
            var prompt = $@"Analyseer de volgende bedrijfsinformatie en extraheer gestructureerde data.

Bedrijfsnaam: {lead.Name}
Tekst invoer:
{lead.ManualInput}

Extraheer de volgende velden (als beschikbaar):
- ownerName: Volledige naam van eigenaar/CEO/directeur
- email: E-mailadres (zakelijk of persoonlijk)
- phone: Telefoonnummer
- city: Stad/plaats
- sector: Bedrijfssector of branche
- website: Website URL

Geef het resultaat als JSON. Als een veld niet gevonden is, gebruik null.
Retourneer alleen de JSON, geen extra tekst.";

            var response = await CallGptAsync(prompt);
            var result = JsonSerializer.Deserialize<TextEnrichmentResult>(response, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result ?? new TextEnrichmentResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text input enrichment failed for lead {LeadId}", lead.Id);
            return new TextEnrichmentResult();
        }
    }

    private async Task<string> CallGptAsync(string prompt)
    {
        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = "Je bent een data extractie assistent. Retourneer alleen geldige JSON." },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 500
        };

        var response = await _http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var content = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        return content ?? "{}";
    }
}

public class TextEnrichmentResult
{
    public string? OwnerName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? City { get; set; }
    public string? Sector { get; set; }
    public string? Website { get; set; }
}

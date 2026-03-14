using System.Text;
using System.Text.Json;

namespace LeadManager.Api.Services.Enrichment;

public class NameExtractorService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public NameExtractorService(IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey is not configured.");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public async Task<(string firstName, string lastName)> ExtractOwnerNameAsync(string companyName, string scrapedText)
    {
        if (string.IsNullOrWhiteSpace(scrapedText))
            return ("", "");

        var prompt = $"Je bent een data-analist die eigenaarsinformatie uit bedrijfswebsite-tekst extraheert.\n\n" +
            $"Bedrijfsnaam: {companyName}\n\n" +
            $"Website-tekst:\n{scrapedText}\n\n" +
            "Taak: Vind de voornaam en achternaam van de eigenaar, directeur, CEO of founder van dit bedrijf.\n\n" +
            "Regels:\n" +
            "- Geef ALLEEN een JSON object terug, geen uitleg\n" +
            "- Als je geen naam kunt vinden, geef lege strings terug\n" +
            "- Gok niet — alleen namen die duidelijk in de tekst staan\n" +
            "- Geef alleen persoonsnamen (geen bedrijfsnamen)\n\n" +
            "Antwoord alleen met dit JSON formaat:\n" +
            "{\"firstName\": \"Jan\", \"lastName\": \"Jansen\"}";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            response_format = new { type = "json_object" },
            max_tokens = 100
        };

        try
        {
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

            var result = JsonSerializer.Deserialize<NameResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (result?.FirstName ?? "", result?.LastName ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [OpenAI error] {ex.Message}");
            return ("", "");
        }
    }

    private class NameResult
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
    }
}

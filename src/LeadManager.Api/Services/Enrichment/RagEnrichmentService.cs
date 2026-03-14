using System.Text;
using System.Text.Json;
using LeadManager.Api.Data;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class RagEnrichmentService
{
    private readonly HttpClient _http;
    private readonly EmbeddingService _embedding;
    private readonly VectorSearchService _vectorSearch;

    public RagEnrichmentService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _embedding = new EmbeddingService(configuration);
        _vectorSearch = new VectorSearchService();
    }

    private record RagQuestion(string Field, string Question);

    private static readonly RagQuestion[] Questions = new[]
    {
        new RagQuestion("ownerName",     "Wat is de volledige naam van de eigenaar, directeur, CEO of founder van dit bedrijf?"),
        new RagQuestion("ownerTitle",    "Wat is de functietitel van de hoofdcontactpersoon of eigenaar van dit bedrijf?"),
        new RagQuestion("contactEmail",  "Wat is het zakelijke e-mailadres van het bedrijf of de eigenaar?"),
        new RagQuestion("contactPhone",  "Wat is het telefoonnummer van het bedrijf?"),
        new RagQuestion("description",   "Geef een korte omschrijving van dit bedrijf in 2 tot 3 zinnen."),
        new RagQuestion("services",      "Welke diensten of producten biedt dit bedrijf aan?"),
        new RagQuestion("targetAudience","Wie is de doelgroep van dit bedrijf?"),
    };

    public async Task<EnrichmentAnswers> EnrichAsync(LeadManagerDbContext db, Lead lead)
    {
        var answers = new EnrichmentAnswers();

        foreach (var q in Questions)
        {
            try
            {
                var queryEmbedding = await _embedding.EmbedAsync(q.Question);
                var context = await _vectorSearch.SearchAsync(db, lead.Id, queryEmbedding, topK: 3);

                if (context.Count == 0) continue;

                var contextText = string.Join("\n\n---\n\n", context);
                var answer = await AskGptAsync(lead.Name, q.Question, contextText);

                switch (q.Field)
                {
                    case "ownerName":     answers.OwnerName = answer; break;
                    case "ownerTitle":    answers.OwnerTitle = answer; break;
                    case "contactEmail":  answers.ContactEmail = answer; break;
                    case "contactPhone":  answers.ContactPhone = answer; break;
                    case "description":   answers.Description = answer; break;
                    case "services":      answers.Services = answer; break;
                    case "targetAudience":answers.TargetAudience = answer; break;
                }
            }
            catch
            {
                // Skip this question, continue with others
            }
        }

        // Generate a polished summary using all extracted data + top website chunks
        try
        {
            answers.AiSummary = await GenerateSummaryAsync(db, lead, answers);
        }
        catch { /* non-fatal */ }

        return answers;
    }

    private async Task<string?> GenerateSummaryAsync(LeadManagerDbContext db, Lead lead, EnrichmentAnswers answers)
    {
        // Gather top chunks from the website for broader context
        var overviewEmbedding = await _embedding.EmbedAsync("wat doet dit bedrijf, wie zijn ze, wat zijn hun diensten");
        var chunks = await _vectorSearch.SearchAsync(db, lead.Id, overviewEmbedding, topK: 5);
        if (chunks.Count == 0) return null;

        var websiteContext = string.Join("\n\n---\n\n", chunks);

        var knownFacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(answers.Description))    knownFacts.Add($"Omschrijving: {answers.Description}");
        if (!string.IsNullOrWhiteSpace(answers.Services))       knownFacts.Add($"Diensten: {answers.Services}");
        if (!string.IsNullOrWhiteSpace(answers.TargetAudience)) knownFacts.Add($"Doelgroep: {answers.TargetAudience}");
        if (!string.IsNullOrWhiteSpace(answers.OwnerName))      knownFacts.Add($"Eigenaar: {answers.OwnerName}{(string.IsNullOrWhiteSpace(answers.OwnerTitle) ? "" : $" ({answers.OwnerTitle})")}");

        var factsSection = knownFacts.Count > 0
            ? $"\n\nAl geëxtraheerde feiten:\n{string.Join("\n", knownFacts)}"
            : "";

        var prompt = $"""
Je bent een B2B sales-analist. Schrijf een beknopte, professionele samenvatting van dit bedrijf voor gebruik door een salesteam.

Bedrijf: {lead.Name}
Website: {lead.Website}{factsSection}

Website-inhoud:
{websiteContext}

Schrijf een samenvatting van 3 tot 5 zinnen die antwoord geeft op:
- Wat doet dit bedrijf?
- Voor wie?
- Wat maakt hen interessant als lead?

Schrijf in lopende tekst, zakelijk maar leesbaar. Geen opsomming, geen headers. Alleen de samenvatting.
""";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 300
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
            .GetString()?.Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task<string?> AskGptAsync(string companyName, string question, string context)
    {
        var prompt = $"Je bent een data-analist die informatie extraheert uit website-tekst.\n\n" +
            $"Bedrijf: {companyName}\n\n" +
            $"Relevante website-tekst:\n{context}\n\n" +
            $"Vraag: {question}\n\n" +
            "Regels:\n" +
            "- Beantwoord alleen op basis van de gegeven tekst\n" +
            "- Als het antwoord niet in de tekst staat, antwoord dan met null\n" +
            "- Geef een kort, direct antwoord (max 2-3 zinnen)\n" +
            "- Geen uitleg, alleen het gevraagde\n\n" +
            "Geef je antwoord als JSON: {\"answer\": \"...\"}";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = prompt } },
            response_format = new { type = "json_object" },
            max_tokens = 200
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
        var answer = resultDoc.RootElement.TryGetProperty("answer", out var answerEl)
            ? answerEl.GetString()
            : null;

        return string.IsNullOrWhiteSpace(answer) || answer == "null" ? null : answer;
    }
}

public class EnrichmentAnswers
{
    public string? OwnerName { get; set; }
    public string? OwnerTitle { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Description { get; set; }
    public string? Services { get; set; }
    public string? TargetAudience { get; set; }
    public string? AiSummary { get; set; }
}

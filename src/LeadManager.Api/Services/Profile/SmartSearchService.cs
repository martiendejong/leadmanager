using System.Text;
using System.Text.Json;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;

namespace LeadManager.Api.Services.Profile;

public record QualifiedLead(
    string Name,
    string Website,
    string City,
    string Sector,
    string Phone,
    string Email,
    string Source,
    int ConfidenceScore,
    string QualificationReason
);

public class SmartSearchService
{
    private readonly HttpClient _http;
    private readonly SearchService _searchService;
    private readonly ILogger<SmartSearchService> _logger;

    public SmartSearchService(IConfiguration configuration, SearchService searchService, ILogger<SmartSearchService> logger)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _searchService = searchService;
        _logger = logger;
    }

    public async Task<List<string>> GenerateQueriesAsync(CompanyProfile profile)
    {
        var sectors = JsonSerializer.Deserialize<string[]>(profile.TargetSectorsJson) ?? [];
        var regions = JsonSerializer.Deserialize<string[]>(profile.TargetRegionsJson) ?? [];
        var keywords = JsonSerializer.Deserialize<string[]>(profile.KeywordsJson) ?? [];

        var prompt = $@"Je bent een lead generation expert die gerichte zoekopdrachten maakt voor DuckDuckGo.

Bedrijfsprofiel van de klant:
Naam: {profile.CompanyName}
Wat ze doen: {profile.WhatTheyDo}
Ideale klant: {profile.IdealCustomerProfile}
Doelsectoren: {string.Join(", ", sectors)}
Doelregio's: {string.Join(", ", regions)}
Keywords: {string.Join(", ", keywords)}

Genereer exact 10 specifieke DuckDuckGo zoekopdrachten om potentiële klanten te vinden voor dit bedrijf.

Regels:
- Elke query moet gericht zijn op het vinden van BEDRIJVEN die klant zouden kunnen worden
- Varieer de zoekopdrachten (mix van sectoren, regio's, specifieke behoeften)
- Gebruik Nederlandse en Engelse termen door elkaar
- Denk vanuit de pijn/behoefte van de ideale klant, niet generiek
- Geen directories of sociale media als zoekterm
- Elke query max 6-8 woorden

Geef antwoord als JSON:
{{""queries"": [""query1"", ""query2"", ""query3"", ""query4"", ""query5"", ""query6"", ""query7"", ""query8"", ""query9"", ""query10""]}}";

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = prompt } },
            response_format = new { type = "json_object" },
            max_tokens = 500
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
        if (resultDoc.RootElement.TryGetProperty("queries", out var queriesEl))
            return queriesEl.EnumerateArray().Select(q => q.GetString() ?? "").Where(q => !string.IsNullOrWhiteSpace(q)).ToList();

        return [];
    }

    public async Task<List<QualifiedLead>> SearchAndQualifyAsync(
        CompanyProfile profile,
        IProgress<QualifiedLead>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var queries = await GenerateQueriesAsync(profile);
        _logger.LogInformation("Generated {Count} queries for profile {Company}", queries.Count, profile.CompanyName);

        var allResults = new List<LeadSearchResult>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Search each query
        foreach (var query in queries)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var results = await _searchService.SearchAsync(query, "", maxResults: 10);
                foreach (var r in results)
                {
                    var domain = ExtractDomain(r.Website);
                    if (!string.IsNullOrEmpty(domain) && seenDomains.Add(domain))
                        allResults.Add(r);
                }
                await Task.Delay(1000, cancellationToken); // rate limit
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Query failed: {Query} — {Error}", query, ex.Message);
            }
        }

        _logger.LogInformation("Found {Count} unique results across all queries", allResults.Count);

        // Qualify in batches of 10
        var qualified = new List<QualifiedLead>();
        var batches = allResults.Chunk(10);

        foreach (var batch in batches)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var batchResults = await QualifyBatchAsync(profile, batch);
            foreach (var lead in batchResults)
            {
                qualified.Add(lead);
                progress?.Report(lead);
            }
            await Task.Delay(500, cancellationToken);
        }

        return qualified.OrderByDescending(l => l.ConfidenceScore).ToList();
    }

    private async Task<List<QualifiedLead>> QualifyBatchAsync(CompanyProfile profile, LeadSearchResult[] batch)
    {
        var resultLines = batch.Select((r, i) =>
            $"{i + 1}. Naam: {r.Name} | Website: {r.Website} | Stad: {r.City} | Snippet beschikbaar: ja").ToList();

        var prompt = $@"Je bent een lead kwalificatie expert.

Bedrijfsprofiel van de klant die leads zoekt:
Naam: {profile.CompanyName}
Wat ze doen: {profile.WhatTheyDo}
Ideale klant (ICP): {profile.IdealCustomerProfile}

Beoordeel elk zoekresultaat: is dit een echt bedrijf dat klant zou kunnen worden?

Zoekresultaten:
{string.Join("\n", resultLines)}

Regels:
- Score 0-100 (>= 60 = relevant)
- Geef 0 voor directories, sociale media, nieuwssites, portals
- Geef hoge score alleen als het echt een bedrijf is dat bij het ICP past
- Wees kritisch — kwaliteit boven kwantiteit

JSON antwoord:
{{""results"": [
  {{""index"": 1, ""isCompany"": true, ""confidenceScore"": 75, ""reason"": ""MKB bedrijf in doelregio""}},
  {{""index"": 2, ""isCompany"": false, ""confidenceScore"": 0, ""reason"": ""Directory website""}}
]}}";

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
            var results = new List<QualifiedLead>();

            if (resultDoc.RootElement.TryGetProperty("results", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var idx = item.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() - 1 : -1;
                    if (idx < 0 || idx >= batch.Length) continue;

                    var score = item.TryGetProperty("confidenceScore", out var scoreEl) ? scoreEl.GetInt32() : 0;
                    var reason = item.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";

                    if (score >= 60)
                    {
                        var r = batch[idx];
                        results.Add(new QualifiedLead(r.Name, r.Website, r.City, r.Sector, r.Phone, r.Email, r.Source, score, reason));
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Batch qualification failed: {Error}", ex.Message);
            return [];
        }
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return "";
    }
}

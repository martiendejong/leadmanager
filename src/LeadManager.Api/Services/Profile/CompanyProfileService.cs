using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeadManager.Api.Data;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Services.Profile;

public class CompanyProfileService
{
    private readonly HttpClient _http;
    private readonly UrlNormalizerService _urlNormalizer;
    private readonly SitemapService _sitemapService;
    private readonly PageFetcherService _pageFetcher;

    public CompanyProfileService(IConfiguration configuration)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _urlNormalizer = new UrlNormalizerService();
        _sitemapService = new SitemapService();
        _pageFetcher = new PageFetcherService();
    }

    public async Task<CompanyProfile> GenerateProfileAsync(string websiteUrl, string userId)
    {
        // Step 1: Normalize and check
        var (resolvedUrl, status) = await _urlNormalizer.NormalizeAndCheckAsync(websiteUrl);
        if (status == WebsiteStatus.Unreachable)
            throw new InvalidOperationException($"Website '{websiteUrl}' is not reachable.");

        // Step 2: Multi-tier crawl (SEO God approach: WordPress → Sitemap → Homepage)
        var pageTexts = new List<string>();

        // Tier 1: Try WordPress REST API
        pageTexts = await TryCrawlWordPressAsync(resolvedUrl);

        // Tier 2: Sitemap
        if (pageTexts.Count == 0)
        {
            var urls = await _sitemapService.DiscoverUrlsAsync(resolvedUrl, maxPages: 30);
            foreach (var url in urls.Take(15))
            {
                var (text, httpStatus) = await _pageFetcher.FetchAndStripAsync(url);
                if (!string.IsNullOrWhiteSpace(text))
                    pageTexts.Add(text);
            }
        }

        // Tier 3: Homepage only
        if (pageTexts.Count == 0)
        {
            var (text, _) = await _pageFetcher.FetchAndStripAsync(resolvedUrl);
            if (!string.IsNullOrWhiteSpace(text))
                pageTexts.Add(text);
        }

        // Step 3: Aggregate and truncate (max 12000 chars total for GPT-4o context)
        var combinedText = string.Join("\n\n---\n\n", pageTexts);
        if (combinedText.Length > 12000)
            combinedText = combinedText[..12000];

        // Step 4: Generate profile with GPT-4o
        var profile = await GenerateWithGptAsync(resolvedUrl, combinedText, userId);
        return profile;
    }

    private async Task<List<string>> TryCrawlWordPressAsync(string baseUrl)
    {
        var texts = new List<string>();
        var wpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        wpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");

        foreach (var endpoint in new[] { "/wp-json/wp/v2/pages?per_page=20", "/wp-json/wp/v2/posts?per_page=10" })
        {
            try
            {
                var json = await wpClient.GetStringAsync(baseUrl.TrimEnd('/') + endpoint);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var content = item.TryGetProperty("content", out var c)
                        ? c.TryGetProperty("rendered", out var r) ? r.GetString() ?? "" : ""
                        : "";
                    if (!string.IsNullOrWhiteSpace(content) && content != "No content found")
                    {
                        var stripped = StripHtmlInline(content);
                        if (!string.IsNullOrWhiteSpace(stripped))
                            texts.Add(stripped);
                    }
                }
            }
            catch { }
        }
        return texts;
    }

    private static string StripHtmlInline(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var text = doc.DocumentNode.InnerText;
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 3000 ? text[..3000] : text;
    }

    private async Task<CompanyProfile> GenerateWithGptAsync(string websiteUrl, string websiteText, string userId)
    {
        var prompt = $@"Je bent een business analyst die bedrijfsprofielen opstelt op basis van website-tekst.

Website: {websiteUrl}

Website inhoud:
{websiteText}

Analyseer deze website grondig en maak een gedetailleerd bedrijfsprofiel. Denk na over:
- Wat doet dit bedrijf precies?
- Wie zijn hun ideale klanten? (specificeer sectoren, bedrijfsgroottes, uitdagingen)
- In welke regio's zijn ze actief?
- Wat zijn hun USPs en onderscheidende factoren?
- Welk taalgebruik en toon hanteren ze?

Geef antwoord als JSON (geen markdown, geen uitleg, alleen JSON):
{{
  ""companyName"": ""Naam van het bedrijf"",
  ""description"": ""2-3 zinnen over wat het bedrijf doet"",
  ""whatTheyDo"": ""Gedetailleerde beschrijving van de diensten/producten"",
  ""idealCustomerProfile"": ""Gedetailleerde beschrijving van de ideale klant: sector, bedrijfsgrootte, uitdagingen, behoeften"",
  ""toneOfVoice"": ""Hoe communiceert dit bedrijf: formeel/informeel, technisch/toegankelijk, etc."",
  ""targetSectors"": [""sector1"", ""sector2"", ""sector3""],
  ""targetRegions"": [""regio1"", ""regio2""],
  ""keywords"": [""keyword1"", ""keyword2"", ""keyword3"", ""keyword4"", ""keyword5""],
  ""usps"": [""USP1"", ""USP2"", ""USP3""]
}}";

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[] { new { role = "user", content = prompt } },
            response_format = new { type = "json_object" },
            max_tokens = 1000
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
        var r = resultDoc.RootElement;

        string GetStr(string key) => r.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        string GetArray(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Serialize(v.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToArray())
            : "[]";

        return new CompanyProfile
        {
            UserId = userId,
            WebsiteUrl = websiteUrl,
            CompanyName = GetStr("companyName"),
            Description = GetStr("description"),
            WhatTheyDo = GetStr("whatTheyDo"),
            IdealCustomerProfile = GetStr("idealCustomerProfile"),
            ToneOfVoice = GetStr("toneOfVoice"),
            TargetSectorsJson = GetArray("targetSectors"),
            TargetRegionsJson = GetArray("targetRegions"),
            KeywordsJson = GetArray("keywords"),
            UspsJson = GetArray("usps"),
            CrawledAt = DateTime.UtcNow,
            ProfileVersion = 1,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}

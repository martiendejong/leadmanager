using HtmlAgilityPack;
using LeadManager.Api.DTOs;
using System.Web;

namespace LeadManager.Api.Services;

public class SearchService
{
    private readonly HttpClient _http;
    private readonly ILogger<SearchService> _logger;

    private static readonly string[] _blockedDomains =
    [
        "google.", "facebook.", "instagram.", "linkedin.", "twitter.", "youtube.",
        "wikipedia.", "kvk.nl", "thuisbezorgd.", "yelp.", "tripadvisor.",
        "marktplaats.", "amazon.", "bol.com", "detelefoongids.", "goudengids.",
        "zoekbedrijven.", "bedrijveninformatie.", "openkvk.", "duckduckgo.",
        "bing.", "yahoo.", "startpage.", "yellowpages.", "trustpilot.", "glassdoor."
    ];

    public SearchService(ILogger<SearchService> logger)
    {
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    /// <summary>
    /// Search DuckDuckGo HTML interface for company leads.
    /// </summary>
    public async Task<List<LeadSearchResult>> SearchAsync(string query, string sector, int maxResults = 25)
    {
        var leads = new List<LeadSearchResult>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // DuckDuckGo HTML paginates via 's' param (0, 30, 60, ...)
        int page = 0;
        while (leads.Count < maxResults)
        {
            var encoded = HttpUtility.UrlEncode(query);
            string url = page == 0
                ? $"https://html.duckduckgo.com/html/?q={encoded}&kl=nl-nl"
                : $"https://html.duckduckgo.com/html/?q={encoded}&kl=nl-nl&s={page * 30}&dc={page * 30 + 1}";

            var html = await FetchAsync(url);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("Empty response from DuckDuckGo for query: {Query}, page: {Page}", query, page);
                break;
            }

            var pageLeads = ExtractFromDuckDuckGo(html, sector, seenDomains);
            leads.AddRange(pageLeads);

            if (pageLeads.Count == 0) break;

            page++;
            if (page > 2) break; // Max 3 pages to avoid hammering

            // Small rate-limit delay between pages
            await Task.Delay(1500);
        }

        // Trim to requested limit
        if (leads.Count > maxResults)
            leads = leads.Take(maxResults).ToList();

        _logger.LogInformation("[Search] \"{Query}\" → {Count} leads found", query, leads.Count);
        return leads;
    }

    private List<LeadSearchResult> ExtractFromDuckDuckGo(string html, string sector, HashSet<string> seenDomains)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var leads = new List<LeadSearchResult>();

        // DuckDuckGo HTML results: div.result__body or div.results_links
        var resultNodes = doc.DocumentNode.SelectNodes(
            "//div[contains(@class,'result__body')]|//div[contains(@class,'web-result')]")
            ?? Enumerable.Empty<HtmlNode>();

        foreach (var node in resultNodes)
        {
            var lead = ExtractLeadFromDDGNode(node, sector);
            if (lead == null) continue;

            var domain = ExtractDomain(lead.Website);
            if (string.IsNullOrEmpty(domain)) continue;
            if (seenDomains.Contains(domain)) continue;
            if (_blockedDomains.Any(b => domain.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;

            seenDomains.Add(domain);
            leads.Add(lead);
        }

        // Fallback: scrape all result links
        if (leads.Count == 0)
        {
            var links = doc.DocumentNode.SelectNodes(
                "//a[contains(@class,'result__a')]|//h2[@class='result__title']/a")
                ?? Enumerable.Empty<HtmlNode>();

            foreach (var link in links)
            {
                var href = ResolveRedirect(link.GetAttributeValue("href", ""));
                if (string.IsNullOrEmpty(href) || !href.StartsWith("http")) continue;

                var title = HtmlEntity.DeEntitize(link.InnerText.Trim());
                var domain = ExtractDomain(href);
                if (string.IsNullOrEmpty(domain)) continue;
                if (seenDomains.Contains(domain)) continue;
                if (_blockedDomains.Any(b => domain.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;

                seenDomains.Add(domain);
                leads.Add(new LeadSearchResult(title, href, "", sector, "", "", "duckduckgo-fallback"));
            }
        }

        return leads;
    }

    private static LeadSearchResult? ExtractLeadFromDDGNode(HtmlNode node, string sector)
    {
        // Title link
        var titleNode = node.SelectSingleNode(".//a[contains(@class,'result__a')]")
            ?? node.SelectSingleNode(".//h2/a");
        if (titleNode == null) return null;

        var title = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
        if (string.IsNullOrWhiteSpace(title)) return null;

        // URL from href (DuckDuckGo uses redirect, also check data-href)
        var rawHref = titleNode.GetAttributeValue("href", "");
        var url = ResolveRedirect(rawHref);
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) return null;

        // Displayed URL (snippet below title)
        var urlNode = node.SelectSingleNode(".//a[contains(@class,'result__url')]");
        var displayUrl = urlNode?.InnerText?.Trim() ?? "";

        // Snippet text
        var snippetNode = node.SelectSingleNode(".//*[contains(@class,'result__snippet')]");
        var snippet = HtmlEntity.DeEntitize(snippetNode?.InnerText?.Trim() ?? "");

        // Try to extract phone and city from snippet
        var phone = ExtractPhone(snippet + " " + displayUrl);
        var city = ExtractCity(snippet);

        return new LeadSearchResult(title, url, city, sector, phone, "", "duckduckgo");
    }

    /// <summary>
    /// DuckDuckGo wraps URLs in redirects like //duckduckgo.com/l/?uddg=https%3A%2F%2F...
    /// </summary>
    private static string ResolveRedirect(string href)
    {
        if (string.IsNullOrEmpty(href)) return "";

        // Absolute URL already
        if (href.StartsWith("http://") || href.StartsWith("https://")) return href;

        // DDG redirect: /l/?uddg=...&rut=...
        if (href.Contains("uddg="))
        {
            var start = href.IndexOf("uddg=") + 5;
            var end = href.IndexOf('&', start);
            var encoded = end > start ? href[start..end] : href[start..];
            return HttpUtility.UrlDecode(encoded);
        }

        // Protocol-relative
        if (href.StartsWith("//")) return "https:" + href;

        return "";
    }

    private static string ExtractPhone(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(\+31[\s\-]?[0-9][\s\-]?[0-9]{8}|0[0-9]{1,2}[\s\-]?[0-9]{6,8}|06[\s\-]?[0-9]{8})");
        return match.Success ? match.Value.Trim() : "";
    }

    private static string ExtractCity(string text)
    {
        // Dutch postcode pattern → city name
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"[0-9]{4}\s?[A-Z]{2}\s+([A-Z][a-z]+(?:\s[A-Z][a-z]+)?)");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private async Task<string> FetchAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Fetch failed for {Url}: {Error}", url, ex.Message);
            return "";
        }
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return "";
    }
}

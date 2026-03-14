using HtmlAgilityPack;
using System.Web;

namespace LeadManager.Api.Services.Enrichment;

public class ScraperService
{
    private readonly HttpClient _http;
    private readonly string[] _aboutPaths = ["/about", "/over-ons", "/over", "/team", "/contact", "/management", "/bedrijf", "/wie-zijn-wij", "/about-us", "/ons-team", "/over-ons/team"];

    public ScraperService()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Tries to find owner-related text via:
    /// 1. Company website (homepage + about pages)
    /// 2. Google search fallback: "{company} eigenaar" / "{company} directeur"
    /// 3. LinkedIn company page (basic)
    /// </summary>
    public async Task<string> ScrapeOwnerTextAsync(string companyName, string websiteUrl, string linkedInUrl = "")
    {
        var parts = new List<string>();

        // 1. Company website
        if (!string.IsNullOrWhiteSpace(websiteUrl))
        {
            var siteText = await ScrapeWebsiteAsync(websiteUrl);
            if (!string.IsNullOrEmpty(siteText))
                parts.Add($"[Website]\n{siteText}");
        }

        // 2. Google search fallback — run always, not just when website fails
        //    Small company sites rarely mention owner; Google snippets often do
        var googleText = await GoogleSearchAsync(companyName);
        if (!string.IsNullOrEmpty(googleText))
            parts.Add($"[Google]\n{googleText}");

        // 3. LinkedIn company page (if URL available and we still have no clear name)
        if (!string.IsNullOrWhiteSpace(linkedInUrl))
        {
            var linkedInText = await FetchTextAsync(linkedInUrl);
            if (!string.IsNullOrEmpty(linkedInText))
                parts.Add($"[LinkedIn]\n{linkedInText}");
        }

        return string.Join("\n\n---\n\n", parts);
    }

    private async Task<string> ScrapeWebsiteAsync(string websiteUrl)
    {
        var baseUrl = NormalizeUrl(websiteUrl);
        if (baseUrl == null) return "";

        var results = new List<string>();

        // Homepage
        var homepageText = await FetchTextAsync(baseUrl);
        if (!string.IsNullOrEmpty(homepageText))
            results.Add(homepageText);

        // About/team pages
        foreach (var path in _aboutPaths)
        {
            var url = baseUrl.TrimEnd('/') + path;
            var text = await FetchTextAsync(url);
            if (!string.IsNullOrEmpty(text))
            {
                results.Add(text);
                break;
            }
        }

        return string.Join("\n", results);
    }

    private async Task<string> GoogleSearchAsync(string companyName)
    {
        // Try multiple search queries to maximize hit rate
        var queries = new[]
        {
            $"{companyName} eigenaar",
            $"{companyName} directeur loodgieter",
            $"{companyName} owner plumber",
        };

        foreach (var query in queries)
        {
            var encoded = HttpUtility.UrlEncode(query);
            var url = $"https://www.google.com/search?q={encoded}&hl=nl&num=5";

            var html = await FetchRawHtmlAsync(url);
            if (string.IsNullOrEmpty(html)) continue;

            var text = ExtractGoogleSnippets(html);
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return "";
    }

    private async Task<string> FetchRawHtmlAsync(string url)
    {
        try
        {
            return await _http.GetStringAsync(url);
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractGoogleSnippets(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var parts = new List<string>();

        // Google search result snippets — multiple possible selectors
        var snippetNodes = doc.DocumentNode.SelectNodes(
            "//*[@data-sncf]|//*[contains(@class,'VwiC3b')]|//*[contains(@class,'s3v9rd')]|//*[contains(@class,'aCOpRe')]");

        if (snippetNodes != null)
        {
            foreach (var node in snippetNodes.Take(8))
            {
                var text = node.InnerText.Trim();
                if (text.Length > 20) parts.Add(text);
            }
        }

        // Fallback: grab all result divs
        if (!parts.Any())
        {
            var divs = doc.DocumentNode.SelectNodes("//div[@class='g']//span") ?? Enumerable.Empty<HtmlNode>();
            foreach (var div in divs.Take(15))
            {
                var text = div.InnerText.Trim();
                if (text.Length > 30) parts.Add(text);
            }
        }

        var combined = string.Join("\n", parts);
        return combined.Length > 2000 ? combined[..2000] : combined;
    }

    private async Task<string> FetchTextAsync(string url)
    {
        try
        {
            var html = await _http.GetStringAsync(url);
            return ExtractMeaningfulText(html);
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractMeaningfulText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//nav|//noscript|//iframe|//footer|//header") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();

        var parts = new List<string>();

        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        if (!string.IsNullOrEmpty(title)) parts.Add(title.Trim());

        var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrEmpty(metaDesc)) parts.Add(metaDesc.Trim());

        foreach (var heading in doc.DocumentNode.SelectNodes("//h1|//h2|//h3") ?? Enumerable.Empty<HtmlNode>())
        {
            var text = heading.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }

        var paras = doc.DocumentNode.SelectNodes("//p") ?? Enumerable.Empty<HtmlNode>();
        foreach (var p in paras.Take(20))
        {
            var text = p.InnerText.Trim();
            if (text.Length > 20) parts.Add(text);
        }

        var combined = string.Join("\n", parts);
        return combined.Length > 2000 ? combined[..2000] : combined;
    }

    private static string? NormalizeUrl(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
    }
}

using System.Xml.Linq;

namespace LeadManager.Api.Services.Enrichment;

public class SitemapService
{
    private readonly HttpClient _http;

    public SitemapService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");
    }

    public async Task<List<string>> DiscoverUrlsAsync(string baseUrl, int maxPages = 50)
    {
        var urls = new List<string>();
        var origin = GetOrigin(baseUrl);
        if (origin == null) return urls;

        // Try sitemap candidates
        var sitemapUrl = await FindSitemapUrlAsync(origin);
        if (sitemapUrl != null)
        {
            urls = await ParseSitemapAsync(sitemapUrl, maxPages);
        }

        // Always include homepage
        if (!urls.Contains(origin))
            urls.Insert(0, origin);

        return urls.Take(maxPages).ToList();
    }

    private async Task<string?> FindSitemapUrlAsync(string origin)
    {
        // Check robots.txt first
        try
        {
            var robotsText = await _http.GetStringAsync($"{origin}/robots.txt");
            foreach (var line in robotsText.Split('\n'))
            {
                if (line.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                {
                    var sitemapUrl = line["Sitemap:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(sitemapUrl))
                        return sitemapUrl;
                }
            }
        }
        catch { }

        // Try common sitemap paths
        foreach (var path in new[] { "/sitemap.xml", "/sitemap_index.xml", "/sitemap-index.xml" })
        {
            try
            {
                var url = origin + path;
                var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                    return url;
            }
            catch { }
        }

        return null;
    }

    private async Task<List<string>> ParseSitemapAsync(string sitemapUrl, int maxPages, int depth = 0)
    {
        if (depth > 2) return new();

        var urls = new List<string>();
        try
        {
            var xml = await _http.GetStringAsync(sitemapUrl);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            // Sitemap index — recurse
            var sitemapLocs = doc.Descendants(ns + "sitemap").Select(s => s.Element(ns + "loc")?.Value).Where(u => u != null).ToList();
            if (sitemapLocs.Count > 0)
            {
                foreach (var subSitemapUrl in sitemapLocs.Take(5))
                {
                    var subUrls = await ParseSitemapAsync(subSitemapUrl!, maxPages, depth + 1);
                    urls.AddRange(subUrls);
                    if (urls.Count >= maxPages) break;
                }
                return urls.Take(maxPages).ToList();
            }

            // Regular sitemap
            var pageUrls = doc.Descendants(ns + "url")
                .Select(u => u.Element(ns + "loc")?.Value)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Where(u => IsContentUrl(u!))
                .Take(maxPages)
                .ToList();

            return pageUrls!;
        }
        catch
        {
            return urls;
        }
    }

    private static bool IsContentUrl(string url)
    {
        var lower = url.ToLower();
        // Skip media/feeds/tags
        return !lower.Contains("/tag/") &&
               !lower.Contains("/category/") &&
               !lower.Contains("/feed") &&
               !lower.EndsWith(".jpg") &&
               !lower.EndsWith(".jpeg") &&
               !lower.EndsWith(".png") &&
               !lower.EndsWith(".gif") &&
               !lower.EndsWith(".pdf") &&
               !lower.EndsWith(".xml");
    }

    private static string? GetOrigin(string url)
    {
        try
        {
            var uri = new Uri(url);
            return $"{uri.Scheme}://{uri.Host}";
        }
        catch { return null; }
    }
}

using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;

namespace LeadManager.Api.Services.Enrichment;

public class PageFetcherService
{
    private readonly HttpClient _http;

    public PageFetcherService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");
    }

    public async Task<(string text, int httpStatus)> FetchAndStripAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url);
            var html = await response.Content.ReadAsStringAsync();
            var text = StripHtml(html);
            return (text, (int)response.StatusCode);
        }
        catch
        {
            return ("", 0);
        }
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove noise nodes
        var removeSelectors = new[] { "script", "style", "nav", "footer", "header", "noscript", "iframe", "svg", "form" };
        foreach (var tag in removeSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes != null)
                foreach (var node in nodes.ToList())
                    node.Remove();
        }

        // Extract text
        var text = doc.DocumentNode.InnerText;

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Collapse whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Limit to 10,000 chars per page (plenty for embedding context)
        if (text.Length > 10_000)
            text = text[..10_000];

        return text;
    }
}

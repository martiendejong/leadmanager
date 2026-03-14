using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class UrlNormalizerService
{
    private readonly HttpClient _http;

    public UrlNormalizerService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; LeadManager/1.0)");
    }

    public async Task<(string resolvedUrl, WebsiteStatus status)> NormalizeAndCheckAsync(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return ("", WebsiteStatus.Unreachable);

        var url = rawUrl.Trim();

        // Add protocol if missing
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        // Try https first, fallback to http
        foreach (var candidate in TryUrls(url))
        {
            try
            {
                var response = await _http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode || (int)response.StatusCode < 500)
                {
                    return (response.RequestMessage?.RequestUri?.ToString() ?? candidate, WebsiteStatus.Reachable);
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        return (url, WebsiteStatus.Unreachable);
    }

    private static IEnumerable<string> TryUrls(string url)
    {
        yield return url;
        // If https failed, try http
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            yield return "http://" + url[8..];
    }
}

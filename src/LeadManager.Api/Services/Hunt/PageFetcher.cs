using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

public record FetchedPage(string Url, int Status, string Html, string Text);

public interface IPageFetcher
{
    /// <summary>Fetches one public page; null when it cannot be fetched (blocked address, error, not HTML).</summary>
    Task<FetchedPage?> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Fetches public web pages only. Every connection, including each redirect hop, is checked at connect time against
/// private, loopback and link-local ranges, so DNS tricks and redirects cannot reach internal services.
/// </summary>
public class SafePageFetcher : IPageFetcher
{
    public const int MaxBytes = 1_500_000;
    public const int MaxRedirects = 4;
    private readonly HttpClient _http;

    public SafePageFetcher(HttpClient http) => _http = http;

    /// <summary>The primary handler that enforces the address check; used when registering the typed client.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !UrlGuard.IsPublicAddress(a)))
                throw new HttpRequestException("Blocked: address is not public.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public async Task<FetchedPage?> FetchAsync(string url, CancellationToken ct)
    {
        if (!UrlGuard.TryNormalize(url, out var uri)) return null;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                req.Headers.TryAddWithoutValidation("Accept-Language", "nl,en;q=0.7");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
                {
                    var next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                    if (!UrlGuard.TryNormalize(next.ToString(), out var checkedNext)) return null;
                    uri = checkedNext;
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return new FetchedPage(uri.ToString(), (int)resp.StatusCode, "", "");
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (type != "" && !type.Contains("html") && !type.Contains("xml") && !type.StartsWith("text/")) return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length >= MaxBytes) break;
                }
                var html = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                return new FetchedPage(uri.ToString(), (int)resp.StatusCode, html, HtmlText.ToText(html));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        return null;
    }
}

public static class HtmlText
{
    /// <summary>Visible text of a page, including header and footer (where contact details and legal forms often are).</summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var tag in new[] { "script", "style", "noscript", "iframe", "svg", "template" })
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes == null) continue;
            foreach (var n in nodes.ToList()) n.Remove();
        }
        // Make tel: targets visible in the text, so facts survive the stripping of the markup.
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? new List<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", ""));
            if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                a.AppendChild(doc.CreateTextNode(" " + href[4..] + " "));
        }
        var text = WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

public record FetchedPage(string Url, int Status, string Html, string Text);

public interface IPageFetcher
{
    /// <summary>Fetches one public page; null when it cannot be fetched (blocked address, error, timeout, not HTML, other site).</summary>
    Task<FetchedPage?> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Fetches public web pages only. Every connection, including each redirect hop, is checked at connect time against
/// private, loopback and link-local ranges, so DNS tricks and redirects cannot reach internal services. The whole fetch (headers
/// AND body) has a hard time limit, so a site that sends its page one byte at a time cannot hold a worker.
/// </summary>
public class SafePageFetcher : IPageFetcher
{
    public const int MaxBytes = 1_500_000;
    public const int MaxRedirects = 4;
    public static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    static SafePageFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public SafePageFetcher(HttpClient http) => _http = http;

    /// <summary>The time limit of one whole page fetch.</summary>
    public TimeSpan FetchTimeout { get; set; } = DefaultFetchTimeout;

    /// <summary>The largest body that is read.</summary>
    public long MaxBodyBytes { get; set; } = MaxBytes;

    /// <summary>The primary handler that enforces the address check; used when registering the typed client. It never uses a proxy.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        Proxy = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) => await ConnectAsync(ctx.DnsEndPoint, ct),
    };

    /// <summary>Resolves the host and connects only when every address it resolves to is a public one.</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !UrlGuard.IsPublicAddress(a)))
            throw new HttpRequestException("Blocked: address is not public.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], endPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task<FetchedPage?> FetchAsync(string url, CancellationToken ct)
    {
        if (!UrlGuard.TryNormalize(url, out var uri)) return null;
        var startDomain = UrlGuard.RegistrableDomain(uri.Host);
        // One budget for the whole fetch: connecting, headers, redirects and reading the body.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(FetchTimeout);
        var token = budget.Token;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                req.Headers.TryAddWithoutValidation("Accept-Language", "nl,en;q=0.7");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);

                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
                {
                    var next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                    if (!UrlGuard.TryNormalize(next.ToString(), out var checkedNext)) return null;
                    // A redirect to another site must not make that site's facts count for this business.
                    if (UrlGuard.RegistrableDomain(checkedNext.Host) != startDomain) return null;
                    uri = checkedNext;
                    continue;
                }
                if (!resp.IsSuccessStatusCode) return new FetchedPage(uri.ToString(), (int)resp.StatusCode, "", "");
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (type != "" && !type.Contains("html") && !type.Contains("xml") && !type.StartsWith("text/")) return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(token);
                using var ms = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length >= MaxBodyBytes) break;
                }
                var bytes = ms.ToArray();
                if (bytes.Length > MaxBodyBytes) bytes = bytes[..(int)MaxBodyBytes]; // the cap is exact, not 'about one buffer more'
                var html = Decode(bytes, resp.Content.Headers.ContentType?.CharSet);
                return new FetchedPage(uri.ToString(), (int)resp.StatusCode, html, HtmlText.ToText(html));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // includes the time limit and every network failure
        return null;
    }

    /// <summary>Decodes with the charset of the response, or of a meta tag in the first kilobyte, and falls back to UTF-8.</summary>
    public static string Decode(byte[] bytes, string? headerCharset)
    {
        var name = headerCharset?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(name))
        {
            var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
            var m = Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) name = m.Groups[1].Value;
        }
        if (!string.IsNullOrEmpty(name))
        {
            try { return Encoding.GetEncoding(name).GetString(bytes); } catch (ArgumentException) { /* unknown charset: UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }
}

public static class HtmlText
{
    /// <summary>
    /// Visible text of a page, including header and footer (where contact details and legal forms often are). Scripts, styles, the document
    /// head and hidden elements are not visible, so text in them can never count as something the business says.
    /// </summary>
    public static string ToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var tag in new[] { "script", "style", "noscript", "iframe", "svg", "template", "head" })
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes == null) continue;
            foreach (var n in nodes.ToList()) n.Remove();
        }
        foreach (var xpath in new[] { "//*[@hidden]", "//*[contains(translate(@style,' ',''),'display:none')]", "//*[contains(translate(@style,' ',''),'visibility:hidden')]" })
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
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

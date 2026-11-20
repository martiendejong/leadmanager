using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

public record FetchedPage(string Url, int Status, string Html, string Text)
{
    private HtmlDocument? _dom;
    private bool _domTried;
    private string? _ownerText;

    /// <summary>A page from its markup: parsed ONCE (safely), the visible text computed from that parse and the parse kept for the rules.</summary>
    public static FetchedPage Parse(string url, int status, string html)
    {
        var dom = SafeHtml.TryLoad(html);
        var page = new FetchedPage(url, status, html, dom == null ? "" : HtmlText.ToText(dom, ownerView: false));
        page._dom = dom;
        page._domTried = true;
        return page;
    }

    /// <summary>The parsed page (once), or null when it is empty or nests too deep to be handled safely. See <see cref="SafeHtml"/>.</summary>
    public HtmlDocument? Dom
    {
        get
        {
            if (!_domTried) { _domTried = true; _dom = SafeHtml.TryLoad(Html); }
            return _dom;
        }
    }

    /// <summary>What the site says about itself in its meta description (or og:description) on this page, cleaned; empty when there is none.</summary>
    public string MetaDescription
    {
        get
        {
            var root = Dom?.DocumentNode;
            if (root == null) return "";
            foreach (var xpath in new[] { "//meta[translate(@name,'DESCRIPTION','description')='description']", "//meta[translate(@property,'OGDESCRIPTION','ogdescription')='og:description']" })
            {
                var content = root.SelectSingleNode(xpath)?.GetAttributeValue("content", "");
                if (!string.IsNullOrWhiteSpace(content)) return TextSafety.Clean(HtmlEntity.DeEntitize(content), 400);
            }
            return "";
        }
    }

    /// <summary>The visible text without the parts that quote other people (testimonials, blockquotes): where names and statements are read.</summary>
    public string OwnerText => _ownerText ??= HtmlText.ToOwnerText(this);
}

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
        var startSite = UrlGuard.SiteKey(uri);
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
                    if (UrlGuard.SiteKey(checkedNext) != startSite) return null;
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
                return FetchedPage.Parse(uri.ToString(), (int)resp.StatusCode, html);
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
            var m = HuntRegex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
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
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    { "script", "style", "noscript", "iframe", "svg", "template", "head", "object", "embed" };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "br", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "nav", "header", "footer", "aside", "main", "td", "th", "tr", "ul", "ol",
        "blockquote", "label", "button", "address", "figcaption", "dd", "dt", "table", "form", "dl", "hr",
    };

    /// <summary>Elements that quote somebody else: what stands in them is not the business speaking about itself.</summary>
    private static readonly System.Text.RegularExpressions.Regex TestimonialMarker = HuntRegex.New(
        @"testimonial|review|recensie|quote|referentie|reference|getuigenis|ervaring|klantverhaal|klantreactie|feedback|beoordeling|reactie|aanbeveling|cite",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private const int MaxChars = 3_000_000;

    /// <summary>
    /// Visible text of a page, including header and footer (where contact details and legal forms often are). Scripts, styles, comments, the
    /// document head and hidden elements are not visible, so text in them can never count as something the business says. One linear
    /// pass; a page that nests too deep gives no text.
    /// </summary>
    public static string ToText(string html)
    {
        var doc = SafeHtml.TryLoad(html);
        return doc == null ? "" : ToText(doc, ownerView: false);
    }

    /// <summary>
    /// The text without the parts that quote other people (blockquote, cite, q and elements marked testimonial, review, quote, reference,
    /// feedback, ...). Names and statements are read from this view.
    /// </summary>
    public static string ToOwnerText(FetchedPage page) => page.Dom == null ? "" : ToText(page.Dom, ownerView: true);

    public static string ToText(HtmlDocument doc, bool ownerView)
    {
        var sb = new System.Text.StringBuilder(64 * 1024);
        Walk(doc.DocumentNode, sb, 0, ownerView);
        var text = HuntRegex.Replace(sb.ToString(), @"[^\S\n]+", " ");
        text = HuntRegex.Replace(text, @" ?\n[ \n]*", "\n");
        return text.Trim();
    }

    private static bool Hidden(HtmlNode el)
    {
        if (el.Attributes.Contains("hidden")) return true;
        var style = el.GetAttributeValue("style", "");
        if (style.Length == 0) return false;
        style = style.Replace(" ", "");
        return style.Contains("display:none", StringComparison.OrdinalIgnoreCase) || style.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestimonial(HtmlNode el)
    {
        var tag = el.Name;
        if (tag.Equals("blockquote", StringComparison.OrdinalIgnoreCase) || tag.Equals("cite", StringComparison.OrdinalIgnoreCase) || tag.Equals("q", StringComparison.OrdinalIgnoreCase)) return true;
        var cls = el.GetAttributeValue("class", "");
        var id = el.GetAttributeValue("id", "");
        return (cls.Length > 0 && TestimonialMarker.IsMatch(cls)) || (id.Length > 0 && TestimonialMarker.IsMatch(id));
    }

    private static void Walk(HtmlNode node, System.Text.StringBuilder sb, int depth, bool ownerView)
    {
        if (depth > SafeHtml.WalkGuard || sb.Length > MaxChars) return;
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                sb.Append(WebUtility.HtmlDecode(((HtmlTextNode)node).Text));
                return;
            case HtmlNodeType.Comment:
                return;
        }

        var element = node.NodeType == HtmlNodeType.Element;
        var block = false;
        if (element)
        {
            if (Skipped.Contains(node.Name) || Hidden(node)) return;
            if (ownerView && IsTestimonial(node)) { sb.Append('\n'); return; }
            block = Blocks.Contains(node.Name);
            if (block) sb.Append('\n');
        }
        foreach (var child in node.ChildNodes) Walk(child, sb, depth + 1, ownerView);
        if (element)
        {
            // Make tel: targets visible in the text, so facts survive the stripping of the markup.
            if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                var href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
                if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) sb.Append(' ').Append(href, 4, href.Length - 4).Append(' ');
            }
            if (block) sb.Append('\n');
        }
    }
}

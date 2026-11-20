using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// The only way the hunt parses a web page. HtmlAgilityPack walks a document recursively (InnerText, XPath, descendants): a hostile page
/// of a few hundred thousand nested tags fits in the size cap and overflows the stack, which ends the whole process (a stack overflow
/// cannot be caught). So a page is first scanned in one linear, non-recursive pass that measures the tag depth; a page that is too deep is
/// not parsed at all and gives no facts. The parser itself is also limited, as a second line, and every walk over the result has a depth
/// guard. No code relies on a catch for a stack overflow.
/// </summary>
public static class SafeHtml
{
    /// <summary>The deepest element nesting a page may have. Real pages stay far below this (page builders reach 40 to 70).</summary>
    public const int MaxDepth = 120;

    /// <summary>The walks over a parsed page never go deeper than this.</summary>
    public const int WalkGuard = MaxDepth + 30;

    // Elements without an end tag: they do not nest anything.
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "basefont", "bgsound", "frame", "keygen" };

    // Elements whose end tag may be left out: counting every opening of them would call a long list of unclosed items "deep".
    private static readonly HashSet<string> OptionalEnd = new(StringComparer.OrdinalIgnoreCase)
    { "p", "li", "dt", "dd", "tr", "td", "th", "tbody", "thead", "tfoot", "option", "optgroup", "colgroup", "rt", "rp", "caption" };

    // Elements whose content is raw text, not markup.
    private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "textarea", "title", "xmp", "plaintext" };

    /// <summary>
    /// True when the elements of this page nest deeper than <paramref name="limit"/>. One pass over the characters, a bounded stack, an early
    /// exit: it never recurses and costs time linear in the size of the page.
    /// </summary>
    public static bool IsTooDeep(string html, int limit = MaxDepth)
    {
        if (string.IsNullOrEmpty(html)) return false;
        var stack = new List<string>(64);
        var n = html.Length;
        var i = 0;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) break;
            var c = html[lt + 1];
            if (c == '!')
            {
                // a comment, a doctype or a CDATA section
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    var end = html.IndexOf('>', lt + 2);
                    i = end < 0 ? n : end + 1;
                }
                continue;
            }
            if (c == '?') { var end = html.IndexOf('>', lt + 2); i = end < 0 ? n : end + 1; continue; }

            var closing = c == '/';
            var nameStart = lt + (closing ? 2 : 1);
            var p = nameStart;
            while (p < n && (char.IsLetterOrDigit(html[p]) || html[p] is '-' or ':' or '_')) p++;
            if (p == nameStart || !char.IsLetter(html[nameStart])) { i = lt + 1; continue; } // "a < b" is text
            var name = html.Substring(nameStart, p - nameStart).ToLowerInvariant();

            // To the end of the tag: a '>' inside a quoted attribute value does not end it.
            var q = p;
            char quote = '\0';
            var selfClosing = false;
            while (q < n)
            {
                var ch = html[q];
                if (quote != '\0') { if (ch == quote) quote = '\0'; }
                else if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == '>') { selfClosing = q > p && html[q - 1] == '/'; break; }
                q++;
            }
            i = q < n ? q + 1 : n;

            if (closing)
            {
                for (var s = stack.Count - 1; s >= 0; s--)
                    if (stack[s] == name) { stack.RemoveRange(s, stack.Count - s); break; }
                continue;
            }
            if (RawText.Contains(name) && !selfClosing)
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? n : close; // the closing tag itself is read by the loop
                continue;
            }
            if (selfClosing || Void.Contains(name) || OptionalEnd.Contains(name)) continue;

            stack.Add(name);
            if (stack.Count > limit) return true;
        }
        return false;
    }

    /// <summary>
    /// The page as a document, or null when it is empty or nests too deep to be handled safely. Every HtmlDocument of the hunt comes from here.
    /// </summary>
    public static HtmlDocument? TryLoad(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        if (IsTooDeep(html)) return null;
        var doc = new HtmlDocument { OptionMaxNestedChildNodes = MaxDepth + 20 };
        try
        {
            doc.LoadHtml(html);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // The parser's own nesting guard (a managed exception, not a stack overflow: the pre-scan keeps real overflows from happening at all).
            return null;
        }
        return doc;
    }
}

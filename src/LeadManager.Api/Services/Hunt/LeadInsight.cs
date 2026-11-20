using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Hunt;

public record Product(string Key, string Name, string WhatItIs, string FitsWhen);

/// <summary>
/// Our products (Jengo Products), from one file (Agent/PRODUCTS.md, an embedded resource). The keys are also listed here so that a lead can only
/// name a product that exists; a test keeps the list and the file the same. Edit the file to change the catalog. Internal prices never leave
/// this class: <see cref="PublicText"/> and <see cref="All"/> have every amount removed, so neither the app, the API nor the agent prompt shows one.
/// </summary>
public static class ProductCatalog
{
    public static readonly IReadOnlyList<string> Keys = new[] { "coachos", "siteaudit", "visit", "werkteam", "cloudws", "custom", "mail", "meet" };

    /// <summary>Products that are in build, not for sale: a lead may not name them.</summary>
    public static readonly IReadOnlyList<string> NotForSale = new[] { "seogod", "calendar" };

    private static string? _text;
    private static string? _public;
    private static IReadOnlyList<Product>? _all;

    /// <summary>The catalog file as it is in the repository (for editors; it may contain prices).</summary>
    public static string Text
    {
        get
        {
            if (_text != null) return _text;
            var asm = typeof(ProductCatalog).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("PRODUCTS.md", StringComparison.Ordinal));
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return _text = reader.ReadToEnd();
        }
    }

    // An amount of money with its unit ("starter EUR 125 / team EUR 500 per month", "EUR 2.50/hour").
    private static readonly Regex Amount = HuntRegex.New(
        @"(?:\b(?:starter|team)\s+)?(?:EUR|€)\s*\d[\d.,]*(?:\s*(?:/|per)\s*(?:month|maand|hour|hr|uur|year|jaar|team|person|seat)\b)?",
        RegexOptions.IgnoreCase);

    /// <summary>The catalog text without any amount: this is what the agent prompt and the API show.</summary>
    public static string PublicText => _public ??= Tidy(Amount.Replace(Text, ""));

    private static string Tidy(string s)
    {
        s = HuntRegex.Replace(s, @"[ \t]*[;,/]+[ \t]*(?=\|)", " ");      // punctuation left behind before the next column
        s = HuntRegex.Replace(s, @"[ \t]{2,}", " ");
        return s;
    }

    /// <summary>The products as the file lists them (the rows of its table), without prices.</summary>
    public static IReadOnlyList<Product> All
    {
        get
        {
            if (_all != null) return _all;
            var list = new List<Product>();
            foreach (var line in PublicText.Split('\n'))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (!line.TrimStart().StartsWith("|") || cells.Length < 4) continue;
                if (cells[0] == "key" || !HuntRegex.IsMatch(cells[0], @"^[a-z][a-z0-9]{1,15}$")) continue;   // the header and the separator row are not products
                list.Add(new Product(cells[0], cells[1], cells[2], cells[3]));
            }
            return _all = list;
        }
    }

    /// <summary>The key in its canonical (lower case) spelling ("CoachOS " gives "coachos"), or null when it is not a product we sell.</summary>
    public static string? Canonical(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        return Keys.Contains(k) ? k : null;
    }
}

/// <summary>One of our products that can help the business, with the observed trigger.</summary>
public record ProductFitEntry(string Product, string Reason, string EvidenceQuote, string SourceUrl);

public record NeedEntry(string Need, string Quote, string SourceUrl);

/// <summary>What the agent said about a candidate beyond the score: why it is a lead, what it needs, which product fits. Only verified parts are kept.</summary>
public class InsightResult
{
    /// <summary>A quote from the business's own pages that shows why it is interesting.</summary>
    public NeedEntry? Observation { get; set; }
    public List<NeedEntry> Needs { get; } = new();
    public List<ProductFitEntry> ProductFit { get; } = new();
    /// <summary>The agent looked and found no product that fits: a valid answer, stored as an empty list.</summary>
    public bool NoClearFit { get; set; }
    public List<DroppedClaim> Dropped { get; } = new();
    public bool Assessed => ProductFit.Count > 0 || NoClearFit;
}

/// <summary>
/// Reading and storing the insight of a candidate. STRICT: a need or a product fit is stored only with a quote that the server found in the
/// visible text of the business's own pages (not in a testimonial); an entry without a verified quote is dropped and listed in the evidence note,
/// never stored with an "unverified" flag. A product key that is not in the catalog is refused. "No clear fit" is accepted.
/// </summary>
public static class LeadInsight
{
    public const int MaxWhy = 1000;
    public const int MaxNeeds = 1000;
    public const int MaxNeedEntries = 5;
    public const int MaxFitEntries = 4;
    public const int MinQuoteLetters = 25;
    public const int MaxQuoteChars = 400;

    /// <summary>The page whose visible text (without testimonials) contains the quote, or null.</summary>
    private static string? Locate(string? quote, SitePages site)
    {
        var q = LeadVerifier.Squash(quote);
        if (q.Length < MinQuoteLetters) return null;
        foreach (var p in site.Pages)
            if (LeadVerifier.Squash(p.OwnerText).Contains(q)) return p.Url;
        return null;
    }

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static InsightResult Read(JsonElement submitted, SitePages site)
    {
        var result = new InsightResult();
        if (submitted.ValueKind != JsonValueKind.Object) return result;

        void Drop(string field, string value, string reason) => result.Dropped.Add(new DroppedClaim(field, TextSafety.Clean(value, 80), reason));

        if (submitted.TryGetProperty("observation", out var obs) && obs.ValueKind == JsonValueKind.Object)
        {
            var quote = Str(obs, "quote").Trim();
            if (quote.Length > MaxQuoteChars) Drop("observation", "", "The quote is too long.");
            else
            {
                var source = Locate(quote, site);
                if (source != null) result.Observation = new NeedEntry("", quote, source);
                else Drop("observation", quote, "The quote does not occur in the visible text of the business's own pages (or is too short).");
            }
        }

        if (submitted.TryGetProperty("needs", out var needs) && needs.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in needs.EnumerateArray().Take(MaxNeedEntries + 5))
            {
                var label = TextSafety.Clean(Str(n, "need"), 140);
                var quote = Str(n, "quote").Trim();
                if (label.Length < 3) { Drop("need", label, "A need needs a short description."); continue; }
                if (result.Needs.Count >= MaxNeedEntries) { Drop("need", label, $"At most {MaxNeedEntries} needs are kept."); continue; }
                if (quote.Length > MaxQuoteChars) { Drop("need", label, "The quote is too long."); continue; }
                var source = Locate(quote, site);
                if (source == null) { Drop("need", label, "The quote does not occur in the visible text of the business's own pages (or is too short): the need is not stored."); continue; }
                result.Needs.Add(new NeedEntry(label, quote, source));
            }
        }

        if (submitted.TryGetProperty("productFit", out var fits) && fits.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fits.EnumerateArray().Take(MaxFitEntries + 5))
            {
                var rawKey = Str(f, "product");
                var key = ProductCatalog.Canonical(rawKey);
                var reason = TextSafety.Clean(Str(f, "reason"), 300);
                var quote = Str(f, "quote").Trim();
                if (quote.Length == 0) quote = Str(f, "evidenceQuote").Trim();
                if (key == null) { Drop("productFit", rawKey, "This is not a product of ours (see the catalog)."); continue; }
                if (reason.Length < 3) { Drop("productFit", key, "A fit needs a reason."); continue; }
                if (result.ProductFit.Any(x => x.Product == key)) { Drop("productFit", key, "This product is already listed."); continue; }
                if (result.ProductFit.Count >= MaxFitEntries) { Drop("productFit", key, $"At most {MaxFitEntries} products are kept."); continue; }
                if (quote.Length > MaxQuoteChars) { Drop("productFit", key, "The quote is too long."); continue; }
                var source = Locate(quote, site);
                if (source == null) { Drop("productFit", key, "The observed trigger (the quote) does not occur in the visible text of the business's own pages (or is too short): the fit is not stored."); continue; }
                result.ProductFit.Add(new ProductFitEntry(key, reason, quote, source));
            }
        }

        // "No clear fit" is a valid answer, but only when no fit survived.
        if (result.ProductFit.Count == 0 && submitted.TryGetProperty("noClearFit", out var none) && none.ValueKind == JsonValueKind.True) result.NoClearFit = true;
        return result;
    }

    public static string SerializeFit(IEnumerable<ProductFitEntry> entries) =>
        JsonSerializer.Serialize(entries.Select(e => new { product = e.Product, reason = e.Reason, evidenceQuote = e.EvidenceQuote, sourceUrl = e.SourceUrl }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>The stored list, or null when none was stored or it cannot be read. An empty list means "assessed, no clear fit".</summary>
    public static List<ProductFitEntry>? ParseFit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<ProductFitEntry>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // An entry stored under an older catalog keeps its key (the screen shows it as a retired product); only NEW input is checked against the catalog.
            return list?.Where(e => !string.IsNullOrWhiteSpace(e.Product) && e.Product.Length <= 40).ToList();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Why this is a lead, written by the server from what it verified: who can be reached (the score and its reasons), the sector and place,
    /// the size and start in the business's own words, and one verified quote. Nothing in it comes from the agent unverified.
    /// </summary>
    public static string BuildWhy(Lead lead, VerificationResult r)
    {
        var parts = new List<string>();
        if (lead.SalesPriorityScore is int score)
        {
            var reasons = (lead.SalesPriorityReasoning ?? "").Replace("—", ",").Replace("–", ",");
            parts.Add($"Reachable decision maker, score {score} of 9{(string.IsNullOrWhiteSpace(lead.SalesPriorityLabel) ? "" : $" ({lead.SalesPriorityLabel})")}: {reasons}.".Replace(":  .", "."));
        }
        var where = string.Join(" in ", new[] { lead.Sector, lead.City }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (where.Length > 0) parts.Add($"Business: {where}.");
        if (r.Verified.TryGetValue("smallCompany", out var small))
            parts.Add($"One-person business, in its own words: \"{Clip(small.Quote, 160)}\".");
        if (r.Verified.TryGetValue("foundingYear", out var year))
            parts.Add($"Active since {year.Value}, in its own words: \"{Clip(year.Quote, 160)}\".");
        if (r.Insight.Observation != null)
            parts.Add($"On its own site: \"{Clip(r.Insight.Observation.Quote, 240)}\".");
        var why = string.Join(" ", parts);
        return why.Length <= MaxWhy ? why : why[..(MaxWhy - 3)].TrimEnd() + "...";
    }

    private static string Clip(string s, int max)
    {
        s = HuntRegex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
    }

    /// <summary>One line per need: the description and the quote that shows it. At most 1000 characters in all.</summary>
    public static string BuildNeeds(IEnumerable<NeedEntry> needs)
    {
        var sb = new StringBuilder();
        foreach (var n in needs)
        {
            var line = $"{n.Need}: \"{Clip(n.Quote, 220)}\"";
            if (sb.Length + line.Length + 1 > MaxNeeds) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Stores the verified insight on a lead from a hunt: the why (always, from verified facts), the needs and the product fit (when the agent
    /// assessed them). Values a person typed are not overwritten; everything written is recorded as written by the server.
    /// </summary>
    public static void Apply(Lead lead, VerificationResult r)
    {
        var written = new List<string>();
        var why = BuildWhy(lead, r);
        if (why.Length > 0 && string.IsNullOrWhiteSpace(lead.WhyLead)) { lead.WhyLead = why; written.Add("whyLead"); }
        if (r.Insight.Needs.Count > 0 && string.IsNullOrWhiteSpace(lead.Needs)) { lead.Needs = BuildNeeds(r.Insight.Needs); written.Add("needs"); }
        if (r.Insight.Assessed && string.IsNullOrWhiteSpace(lead.ProductFitJson)) { lead.ProductFitJson = SerializeFit(r.Insight.ProductFit); written.Add("productFit"); }
        HuntProvenance.Record(lead, written);
    }
}

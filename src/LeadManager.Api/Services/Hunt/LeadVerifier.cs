using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LeadManager.Api.Models;
using LeadManager.Api.Services.Enrichment;

namespace LeadManager.Api.Services.Hunt;

/// <summary>A claim with its source: what was found, where, and the exact words.</summary>
public record Claim(string Field, string Value, string SourceUrl, string Quote);
public record DroppedClaim(string Field, string Value, string Reason);

public class VerificationResult
{
    public string Website { get; set; } = "";
    public bool Reachable { get; set; }
    public List<string> PageUrls { get; } = new();
    public EvidenceFacts Facts { get; set; } = new();
    /// <summary>Score bearing claims that survived the quote and rule check of the server.</summary>
    public Dictionary<string, Claim> Verified { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DroppedClaim> Dropped { get; } = new();
    /// <summary>Plain descriptive fields (not score bearing).</summary>
    public Dictionary<string, string> Descriptive { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GoogleNote { get; set; }
    /// <summary>What the agent said about the business beyond the score (why, needs, product fit), only the parts whose quote the server verified.</summary>
    public InsightResult Insight { get; set; } = new();
    /// <summary>True when the pages were read and the claims were checked (false when the website did not answer).</summary>
    public bool Checked { get; set; }
}

/// <summary>The pages of one website, fetched step by step: a quick look first, a deeper look only when the quick look is promising.</summary>
public class SitePages
{
    /// <summary>The whole look at one site (all its pages) may take this long.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    public string Website { get; init; } = "";
    public DateTime Deadline { get; init; } = DateTime.UtcNow + Budget;
    public FetchedPage? Home { get; set; }
    public List<FetchedPage> Pages { get; } = new();
    public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Reachable => Home is { Status: >= 200 and < 300 };
}

public class LeadVerifier
{
    public const int QuickPages = 3;
    public const int MaxPages = 8;
    private const int PerPageChars = 5000;
    private const int TotalChars = 36000;

    private static readonly string[] ContactWords = { "contact", "kontakt", "bereik", "afspraak" };
    private static readonly string[] AboutWords = { "over", "about", "wie", "team", "ik-ben", "mijn-verhaal", "praktijk", "bureau", "het-bedrijf" };
    private static readonly string[] LegalWords = { "impressum", "privacy", "voorwaarden", "disclaimer", "colofon", "kvk", "algemene" };

    private readonly IPageFetcher _fetcher;
    private readonly LeadEvidenceService _evidence;
    private readonly ILogger<LeadVerifier> _logger;

    public LeadVerifier(IPageFetcher fetcher, LeadEvidenceService evidence, ILogger<LeadVerifier> logger)
    {
        _fetcher = fetcher;
        _evidence = evidence;
        _logger = logger;
    }

    // ---- pages -----------------------------------------------------------------------------------------

    public async Task<SitePages> QuickLookAsync(string website, CancellationToken ct)
    {
        var site = new SitePages { Website = website };
        if (!UrlGuard.TryNormalize(website, out var uri)) return site;
        site.Home = await _fetcher.FetchAsync(uri.ToString(), ct);
        if (site.Home == null || site.Home.Status is < 200 or >= 300) return site;
        site.Seen.Add(Canon(site.Home.Url));
        site.Pages.Add(site.Home);
        await FetchRankedAsync(site, QuickPages, new[] { ContactWords, AboutWords }, ct);
        return site;
    }

    public async Task DeepenAsync(SitePages site, CancellationToken ct)
    {
        if (!site.Reachable) return;
        await FetchRankedAsync(site, MaxPages, new[] { ContactWords, LegalWords, AboutWords }, ct);
    }

    private async Task FetchRankedAsync(SitePages site, int limit, string[][] priority, CancellationToken ct)
    {
        if (site.Home == null) return;
        var host = new Uri(site.Home.Url).Host.ToLowerInvariant().Replace("www.", "");
        var doc = site.Home.Dom;
        if (doc == null) return; // a page that nests too deep gives no links
        var links = new List<(string Url, string Label)>();
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? new List<HtmlNode>())
        {
            var href = System.Net.WebUtility.HtmlDecode(a.GetAttributeValue("href", "")).Trim();
            if (href.Length == 0 || href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("tel:") || href.StartsWith("javascript:")) continue;
            if (!Uri.TryCreate(new Uri(site.Home.Url), href, out var abs)) continue;
            if (abs.Scheme != "http" && abs.Scheme != "https") continue;
            if (abs.Host.ToLowerInvariant().Replace("www.", "") != host) continue; // same host only
            var label = (abs.AbsolutePath + " " + a.InnerText).ToLowerInvariant();
            links.Add((new UriBuilder(abs) { Fragment = "", Query = "" }.Uri.ToString(), label));
        }

        foreach (var words in priority)
        {
            foreach (var (url, label) in links)
            {
                if (site.Pages.Count >= limit || DateTime.UtcNow >= site.Deadline) return;
                if (!words.Any(w => label.Contains(w))) continue;
                if (!site.Seen.Add(Canon(url))) continue;
                var page = await _fetcher.FetchAsync(url, ct);
                if (page is { Status: >= 200 and < 300 }) site.Pages.Add(page);
            }
        }
    }

    private static string Canon(string url)
    {
        var u = HuntRegex.Replace(url.ToLowerInvariant(), @"^https?://(www\.)?", "");
        return u.Split('#')[0].Split('?')[0].TrimEnd('/');
    }

    // ---- the quick bound -------------------------------------------------------------------------------

    /// <summary>
    /// An optimistic upper bound for the score, from the quick look only. It is never a claim: it only decides whether a deeper (and
    /// more expensive) look can still lead somewhere. Unknown items count as possible unless the pages contradict them.
    /// </summary>
    public static int MaxReachable(EvidenceFacts facts, bool googlePlacesConfigured)
    {
        var max = 0;
        if (facts.LinkedIns.Any(l => l.Kind == "in")) max += 2; else if (facts.LinkedIns.Any()) max += 1;
        if (facts.Mobiles.Count > 0) max += 2;
        max += 1; // the owner's name: found by the language model later
        if (facts.Emails.Any(e => !e.Role)) max += 1;
        if (!LeadEvidenceService.ContradictsSmall(facts)) max += 1;
        if (!facts.Years.Any() || facts.Years.Any(y => y.Kind != "Copyright" && y.Year <= 2015 && !y.CareerLike)) max += 1;
        if (googlePlacesConfigured) max += 1;
        return Math.Min(max, 9);
    }

    // ---- verification ----------------------------------------------------------------------------------

    private static readonly string[] ScoreFields =
    {
        "ownerName", "ownerLinkedInUrl", "ownerMobile", "personalEmail", "companyLinkedInUrl", "foundingYear", "smallCompany", "kvkNumber",
    };

    /// <summary>
    /// Checks the claims a researcher submitted (the Jengo agent) against the fetched pages. The agent is not trusted: a claim counts only
    /// when its quote is in the VISIBLE text of a fetched page, the server's own extractor found the same fact on the site (a person with an
    /// owner cue, a first-person statement, a published number, a link, an address, a year) and the rule for the field holds. What is
    /// stored is the value the extractor found, never the string the agent sent.
    /// </summary>
    public VerificationResult VerifyClaims(SitePages site, JsonElement submitted)
    {
        var result = NewResult(site);
        if (!site.Reachable) return result;
        result.Checked = true;

        foreach (var field in ScoreFields)
        {
            var (claim, readError) = ReadClaim(submitted, field);
            if (readError != null) { result.Dropped.Add(new DroppedClaim(field, "", readError)); continue; }
            if (claim == null) continue;
            var (canonical, reason) = CheckClaim(claim, site, result.Facts, result.Verified);
            if (canonical != null) result.Verified[field] = canonical;
            else result.Dropped.Add(new DroppedClaim(field, TextSafety.Clean(claim.Value, 80), reason ?? "Not verified."));
        }
        ReadDescriptive(submitted, site, result);
        result.Insight = LeadInsight.Read(submitted, site);
        return result;
    }

    /// <summary>
    /// Verifies an existing lead from its own website without anyone's claims: only what the rules find on the pages is accepted, and the
    /// owner is the person the site names as owner (preferring the name the lead already has).
    /// </summary>
    public VerificationResult VerifyDeterministic(SitePages site, string? knownOwnerName)
    {
        var result = NewResult(site);
        if (!site.Reachable) return result;
        var f = result.Facts;
        var claims = new Dictionary<string, object>();

        OwnerFact? owner = null;
        if (!string.IsNullOrWhiteSpace(knownOwnerName))
        {
            var key = LeadEvidenceService.NameKey(knownOwnerName);
            owner = f.Owners.FirstOrDefault(o => LeadEvidenceService.NameKey(o.Name) == key);
        }
        // Structured data, an about heading, 'my name is' or 'I am' first. A name that only stands next to a role word (rank 4 and 5) is not enough for a lead
        // that has no owner yet: it can be a quoted person or somebody else's director. It may only confirm a name the lead already has.
        owner ??= string.IsNullOrWhiteSpace(knownOwnerName) ? f.Owners.Where(o => o.Rank <= 3).OrderBy(o => o.Rank).FirstOrDefault() : null;
        if (owner != null)
        {
            claims["ownerName"] = new { value = owner.Name, quote = owner.Quote, source_url = owner.SourceUrl };
            var link = f.LinkedIns.FirstOrDefault(l => l.Kind == "in" && LeadEvidenceService.HandleCarriesName(l.Handle, owner.Name));
            if (link != null) claims["ownerLinkedInUrl"] = new { value = link.Url, quote = "linkedin.com/in/" + link.Handle, source_url = link.SourceUrl };
        }
        var mobile = f.Mobiles.Where(m => !m.Credit).OrderByDescending(m => m.Contact).FirstOrDefault();
        if (mobile != null) claims["ownerMobile"] = new { value = mobile.AsWritten, quote = mobile.Quote, source_url = mobile.SourceUrl };
        var company = f.LinkedIns.FirstOrDefault(l => l.Kind == "company" && l.Trusted);
        if (company != null) claims["companyLinkedInUrl"] = new { value = company.Url, quote = "linkedin.com/company/" + company.Handle, source_url = company.SourceUrl };
        var mail = f.Emails.FirstOrDefault(e => !e.Role && e.SameDomain && e.Warning == null);
        if (mail != null) claims["personalEmail"] = new { value = mail.Address, quote = mail.Quote, source_url = mail.SourceUrl };
        // The year from the best-ranked statement of the business about itself (founded, then 'since'), not the earliest year found anywhere.
        var year = f.Years.Where(y => y.Kind != "Copyright" && !y.CareerLike).OrderBy(y => y.Rank).FirstOrDefault();
        if (year != null) claims["foundingYear"] = new { value = year.Year.ToString(), quote = year.Quote, source_url = year.SourceUrl };
        var small = f.Small.FirstOrDefault();
        if (small != null) claims["smallCompany"] = new { value = "1", quote = small.Quote, source_url = small.SourceUrl };
        var kvk = f.Kvk.FirstOrDefault();
        if (kvk != null) claims["kvkNumber"] = new { value = kvk.Number, quote = kvk.Quote, source_url = kvk.SourceUrl };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(claims));
        foreach (var field in ScoreFields)
        {
            var (claim, readError) = ReadClaim(doc.RootElement, field);
            if (readError != null || claim == null) continue;
            var (canonical, reason) = CheckClaim(claim, site, f, result.Verified);
            if (canonical != null) result.Verified[field] = canonical;
            else result.Dropped.Add(new DroppedClaim(field, TextSafety.Clean(claim.Value, 80), reason ?? "Not verified."));
        }
        result.Checked = true;
        return result;
    }

    private VerificationResult NewResult(SitePages site)
    {
        var result = new VerificationResult { Website = site.Website, Reachable = site.Reachable };
        foreach (var p in site.Pages) result.PageUrls.Add(p.Url);
        result.Facts = _evidence.Extract(site.Pages, site.Home?.Url ?? site.Website);
        return result;
    }

    // ---- claim reading and the rule check ----------------------------------------------------------------

    private const int MaxValueChars = 200;
    private const int MaxQuoteChars = 600;

    private static (Claim? Claim, string? Error) ReadClaim(JsonElement root, string field)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return (null, null);
        string S(string n) => el.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "" : "";
        var value = S("value").Trim();
        if (value.Length == 0) return (null, null);
        var quote = S("quote").Trim();
        if (value.Length > MaxValueChars || quote.Length > MaxQuoteChars) return (null, "The value or the quote is far too long to be a fact from a page.");
        return (new Claim(field, value, S("source_url").Trim(), quote), null);
    }

    public static string Squash(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>The quote must be on a fetched page, in its VISIBLE text. Links (a LinkedIn profile, a mailto) may also be found in the markup.</summary>
    private static bool QuoteOnPage(Claim claim, SitePages site, bool markupAllowed)
    {
        var q = Squash(claim.Quote);
        if (q.Length < 6) return false;
        foreach (var p in site.Pages)
        {
            if (Squash(p.Text).Contains(q)) return true;
            if (markupAllowed && Squash(System.Net.WebUtility.HtmlDecode(p.Html)).Contains(q)) return true;
        }
        return false;
    }

    private static readonly Regex LinkedInPersonal = HuntRegex.New(@"^https?://(?:[a-z]{2,3}\.|www\.)?linkedin\.com/in/([A-Za-z0-9\-_%.]{2,100})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LinkedInCompany = HuntRegex.New(@"^https?://(?:[a-z]{2,3}\.|www\.)?linkedin\.com/company/([A-Za-z0-9\-_%.]{2,100})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmailWhole = HuntRegex.New(@"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$", RegexOptions.Compiled);

    /// <summary>
    /// The canonical claim (the value taken from the matching fact the server found on the site) or the reason the claim is dropped.
    /// </summary>
    private static (Claim? Canonical, string? Reason) CheckClaim(Claim claim, SitePages site, EvidenceFacts facts, Dictionary<string, Claim> accepted)
    {
        var markup = claim.Field is "ownerLinkedInUrl" or "companyLinkedInUrl" or "personalEmail";
        if (!QuoteOnPage(claim, site, markup)) return (null, "The quote does not occur on any fetched page.");
        var quoteSquashed = Squash(claim.Quote);

        switch (claim.Field)
        {
            case "ownerName":
                {
                    var key = LeadEvidenceService.NameKey(claim.Value);
                    if (key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return (null, "A full name (first and last name) is needed.");
                    var fact = facts.Owners.FirstOrDefault(o => LeadEvidenceService.NameKey(o.Name) == key);
                    if (fact == null) return (null, "The site does not name this full name as its owner, founder or director (no owner cue next to the name).");
                    return (new Claim("ownerName", fact.Name, fact.SourceUrl, fact.Quote), null);
                }
            case "ownerLinkedInUrl":
                {
                    var m = LinkedInPersonal.Match(claim.Value);
                    if (!m.Success) return (null, "Not a personal LinkedIn profile address (linkedin.com/in/...).");
                    var handle = m.Groups[1].Value.TrimEnd('/', '.');
                    var fact = facts.LinkedIns.FirstOrDefault(l => l.Kind == "in" && l.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
                    if (fact == null) return (null, "The business does not publish this link on its own pages.");
                    if (!accepted.TryGetValue("ownerName", out var owner)) return (null, "No verified owner name to match the profile to.");
                    if (!LeadEvidenceService.HandleCarriesName(fact.Handle, owner.Value)) return (null, "The profile handle does not carry the owner's name.");
                    return (new Claim("ownerLinkedInUrl", fact.Url, fact.SourceUrl, "linkedin.com/in/" + fact.Handle), null);
                }
            case "companyLinkedInUrl":
                {
                    var m = LinkedInCompany.Match(claim.Value);
                    if (!m.Success) return (null, "Not a company LinkedIn page address (linkedin.com/company/...).");
                    var fact = facts.LinkedIns.FirstOrDefault(l => l.Kind == "company" && l.Handle.Equals(m.Groups[1].Value.TrimEnd('/', '.'), StringComparison.OrdinalIgnoreCase));
                    if (fact == null) return (null, "The business does not publish this link on its own pages.");
                    if (!fact.Trusted) return (null, "The link does not stand in the header, footer or a social block of the site and its name does not resemble the business: it may be a client's.");
                    return (new Claim("companyLinkedInUrl", fact.Url, fact.SourceUrl, "linkedin.com/company/" + fact.Handle), null);
                }
            case "ownerMobile":
                {
                    var norm = PhoneNumbers.NormalizeMobileStrict(claim.Value);
                    if (norm == null) return (null, "Not a single Dutch mobile number (06). Landlines, 085, 088, 0800, 0900 and text around the number do not count.");
                    var facts06 = facts.Mobiles.Where(x => x.Normalized == norm).ToList();
                    if (facts06.Count == 0) return (null, "The number is not published on the business's own pages.");
                    var fact = PublishedMobile(facts, norm, out var mobileRefusal);
                    if (fact == null) return (null, mobileRefusal);
                    return (new Claim("ownerMobile", PhoneNumbers.Format(norm), fact.SourceUrl, fact.Quote), null);
                }
            case "personalEmail":
                {
                    var addr = claim.Value.Trim().ToLowerInvariant();
                    if (!EmailWhole.IsMatch(addr)) return (null, "Not a single e-mail address.");
                    var matching = facts.Emails.Where(e => e.Address == addr).ToList();
                    if (matching.Count == 0) return (null, "The address is not published on the business's own pages.");
                    if (matching.All(e => e.Role)) return (null, "A role mailbox (info@, contact@, ...) does not count as a personal address.");
                    if (matching.All(e => e.Warning != null && e.Warning.StartsWith("The link shows"))) return (null, "The link on the page opens a different address than the one it shows.");
                    var best = matching.FirstOrDefault(e => !e.Role && e.SameDomain && (e.Warning == null || !e.Warning.StartsWith("The link shows")));
                    if (best == null) return (null, matching.Any(e => e.FreeProvider) ? "An address on a free provider (gmail, hotmail, ...) is not counted as the business's own mailbox." : "The address is not on the business's own domain.");

                    var local = best.Address[..best.Address.IndexOf('@')];
                    var foldedLocal = LeadEvidenceService.Fold(local);
                    var parts = local.Split(new[] { '.', '-', '_', '+' }, StringSplitOptions.RemoveEmptyEntries).Select(LeadEvidenceService.Fold).Where(t => t.Length > 0).ToList();
                    var names = facts.Owners.Select(o => o.Name).ToList();
                    if (accepted.TryGetValue("ownerName", out var ownerClaim)) names.Add(ownerClaim.Value);
                    var tied = names.Any(n =>
                    {
                        var first = LeadEvidenceService.FirstName(n);
                        var last = LeadEvidenceService.LastName(n);
                        if (first.Length >= 2 && parts.Contains(first)) return true;                       // jan@, jan.devries@
                        if (last.Length >= 3 && parts.Contains(last)) return true;                         // j.devries@
                        if (first.Length >= 2 && last.Length >= 3 && (foldedLocal == first + last || foldedLocal == last + first)) return true; // jandevries@
                        return first.Length >= 1 && last.Length >= 3 && foldedLocal == first[..1] + last;  // jdevries@ (initial and surname, exactly)
                    });
                    if (!tied) return (null, "The mailbox name is not tied to a person the site names as its owner.");
                    return (new Claim("personalEmail", best.Address, best.SourceUrl, best.Quote), null);
                }
            case "foundingYear":
                {
                    if (!int.TryParse(claim.Value.Trim(), out var year)) return (null, "Not a year.");
                    var hits = facts.Years.Where(y => y.Year == year && y.Kind != "Copyright").ToList();
                    if (hits.Count == 0) return (null, facts.Years.Any(y => y.Year == year) ? "The year only appears in a copyright notice, which is the age of the website, not of the business." : "No sentence on the site states that the business started in this year.");
                    var good = hits.FirstOrDefault(y => !y.CareerLike);
                    if (good == null) return (null, "The sentence describes the owner's career, not the start of this business.");
                    if (!quoteSquashed.Contains(year.ToString())) return (null, "The quote does not contain the year.");
                    return (new Claim("foundingYear", year.ToString(), good.SourceUrl, good.Quote), null);
                }
            case "smallCompany":
                {
                    var fact = facts.Small.FirstOrDefault(s => Squash(s.Quote).Contains(quoteSquashed) || quoteSquashed.Contains(Squash(s.Quote)));
                    if (fact == null) return (null, "The quote is not a first-person or legal statement of the business that it is a one-person business (a plural, a negation, a customer or a testimonial does not count).");
                    if (LeadEvidenceService.ContradictsSmall(facts)) return (null, "The site also names a B.V., N.V., partnership, foundation or a team, which contradicts 'small'.");
                    return (new Claim("smallCompany", "1", fact.SourceUrl, fact.Quote), null);
                }
            case "kvkNumber":
                {
                    var m = HuntRegex.Match(claim.Value, @"^\s*(?:kvk[\s:\-]*)?(\d{8})\s*$", RegexOptions.IgnoreCase);
                    if (!m.Success) return (null, "Not an 8 digit KvK number.");
                    var fact = facts.Kvk.FirstOrDefault(k => k.Number == m.Groups[1].Value);
                    return fact == null ? (null, "The KvK number is not published on the business's own pages.") : (new Claim("kvkNumber", fact.Number, fact.SourceUrl, fact.Quote), null);
                }
        }
        return (null, "Unknown field.");
    }

    /// <summary>
    /// The mobile fact that may be stored for this number: not next to a review or a web designer's credit and, when the site shows several
    /// numbers, in a contact context. The same rule for the owner's mobile and for the phone field (a mobile number in Phone scores too).
    /// </summary>
    private static MobileFact? PublishedMobile(EvidenceFacts facts, string norm, out string refusal)
    {
        refusal = "";
        var same = facts.Mobiles.Where(x => x.Normalized == norm).ToList();
        if (same.Count == 0) { refusal = "The number is not published on the business's own pages."; return null; }
        var many = facts.Mobiles.Select(x => x.Normalized).Distinct().Count() > 1;
        var fact = same.FirstOrDefault(x => !x.Credit && (!many || x.Contact));
        if (fact == null)
            refusal = many ? "Several mobile numbers are on the site and this one does not stand in a contact context (it is next to a review or a credit)." : "The number stands next to a review or a web designer's credit.";
        return fact;
    }

    private static readonly Regex ZipWhole = HuntRegex.New(@"^\d{4}\s?[A-Za-z]{2}$", RegexOptions.Compiled);
    private static readonly Regex SocialWhole = HuntRegex.New(@"^https://(?:www\.)?(facebook|instagram)\.com/[A-Za-z0-9._\-/]{1,100}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A text value that must literally stand on ONE page (digits and words are never joined across tokens or pages).</summary>
    private static bool LiteralOnOnePage(string value, SitePages site)
    {
        var v = HuntRegex.Replace(value, @"\s+", " ").Trim();
        return v.Length >= 4 && site.Pages.Any(p => p.Text.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A phone value is stored only when it IS a number the pages publish: a mobile fact, a landline fact or a tel: link.</summary>
    public static string? CanonicalPhone(string value, EvidenceFacts facts)
    {
        var norm = PhoneNumbers.NormalizeMobileStrict(value);
        if (norm != null) return PublishedMobile(facts, norm, out _) != null ? PhoneNumbers.Format(norm) : null;
        if (!HuntRegex.IsMatch(value, @"^[0-9 +\-().]{9,20}$")) return null;
        var digits = HuntRegex.Replace(value, @"\D", "");
        if (digits.Length < 9) return null;
        // A number that is a mobile number in another spelling must pass the mobile rule, not the landline route.
        if (PhoneNumbers.NormalizeMobile(value) != null) return null;
        var landline = facts.Landlines.FirstOrDefault(l => HuntRegex.Replace(l, @"\D", "") == digits);
        if (landline != null) return HuntRegex.Replace(landline, @"\s+", " ").Trim();
        var tail = digits[^9..];
        return facts.TelNumbers.Any(t => HuntRegex.Replace(t, @"\D", "").EndsWith(tail)) ? value.Trim() : null;
    }

    private static void ReadDescriptive(JsonElement root, SitePages site, VerificationResult result)
    {
        var html = string.Join(" ", site.Pages.Select(p => System.Net.WebUtility.HtmlDecode(p.Html)));
        string? Get(string name, int max)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var el)) return null;
            var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            s = TextSafety.Clean(s, max);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // Free text about the business: cleaned of anything that looks like an instruction and clamped. It is shown to people and read by
        // a language model later, so it is treated as untrusted whatever the agent wrote.
        foreach (var (name, max) in new[] { ("sector", 80), ("city", 60),
            ("description", 400), ("services", 400), ("targetAudience", 300), ("workingArea", 200), ("certifications", 300), ("openingHours", 300) })
        {
            var v = Get(name, max);
            if (v != null) result.Descriptive[name] = v;
        }

        var phone = Get("phone", 40);
        if (phone != null)
        {
            var canonical = CanonicalPhone(phone, result.Facts);
            if (canonical != null) result.Descriptive["phone"] = canonical;
            else result.Dropped.Add(new DroppedClaim("phone", phone, "The number is not a mobile, landline or tel: number that the pages publish."));
        }
        var companyEmail = Get("companyEmail", 100);
        if (companyEmail != null)
        {
            var fact = result.Facts.Emails.FirstOrDefault(e => e.Address.Equals(companyEmail.Trim(), StringComparison.OrdinalIgnoreCase));
            if (fact != null) result.Descriptive["companyEmail"] = fact.Address;
            else result.Dropped.Add(new DroppedClaim("companyEmail", companyEmail, "The address is not published on the fetched pages."));
        }
        var street = Get("street", 100);
        if (street != null)
        {
            if (LiteralOnOnePage(street, site)) result.Descriptive["street"] = street;
            else result.Dropped.Add(new DroppedClaim("street", street, "The value does not occur on one fetched page."));
        }
        var zip = Get("zipCode", 10);
        if (zip != null)
        {
            if (ZipWhole.IsMatch(zip) && LiteralOnOnePage(zip, site)) result.Descriptive["zipCode"] = zip.ToUpperInvariant();
            else result.Dropped.Add(new DroppedClaim("zipCode", zip, "Not a postcode that a fetched page shows."));
        }
        foreach (var name in new[] { "facebookUrl", "instagramUrl" })
        {
            var v = Get(name, 140);
            if (v == null) continue;
            if (SocialWhole.IsMatch(v) && html.Contains(v, StringComparison.OrdinalIgnoreCase)) result.Descriptive[name] = v;
            else result.Dropped.Add(new DroppedClaim(name, v, "Not a facebook or instagram address that the fetched pages link to."));
        }
    }

    // ---- applying --------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the verified state to the lead. With clearUnverified (an existing lead) score bearing values that could not be verified are
    /// removed, so the score never keeps a point nobody can prove. The score itself is computed by SalesScoreService, never by the model.
    /// </summary>
    public static List<string> Apply(Lead lead, VerificationResult r, bool clearUnverified, SalesScoreService scoring, List<string>? kept = null, Func<string, bool>? mayClear = null)
    {
        var removed = new List<string>();
        var written = new List<string>();
        // Clearing is decided per field: with mayClear only the fields the server wrote itself (and nobody changed since) may go.
        bool Clearable(string field) => clearUnverified && (mayClear == null || mayClear(field));
        // With clearUnverified a value that could not be verified is removed; without it the value stays and is listed in 'kept' (what a run with clearing would remove).
        void Drop(string field, string what, Action clear) { if (Clearable(field)) { removed.Add(what); clear(); } else kept?.Add(what); }
        string Val(string f) => r.Verified.TryGetValue(f, out var c) ? c.Value : "";
        static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        // Without clearing, a value a person already has is never overwritten, not even by a verified one: it is listed and the person decides.
        bool CanWrite(string field, string? current, string verified)
        {
            if (Clearable(field) || string.IsNullOrWhiteSpace(current) || string.Equals(current.Trim(), verified.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            kept?.Add($"{field} '{current}' differs from what the website shows ('{verified}')");
            return false;
        }

        if (r.Reachable) { lead.WebsiteStatus = WebsiteStatus.Reachable; lead.ResolvedUrl = r.PageUrls.FirstOrDefault(); }

        if (r.Verified.ContainsKey("ownerName"))
        {
            if (!CanWrite("ownerName", lead.OwnerName, Val("ownerName"))) goto afterOwner;
            if (!Same(lead.OwnerName, Val("ownerName")))   // the same name that a person typed stays the person's
            {
                lead.OwnerName = Val("ownerName");
                written.Add("ownerName");
                // The first name, the last name and the title come from the verified owner and his or her role word, not from the agent.
                var parts = lead.OwnerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                lead.OwnerFirstName = parts[0];
                lead.OwnerLastName = parts.Length > 1 ? parts[1] : "";
                var role = LeadEvidenceService.RoleFromQuote(r.Verified["ownerName"].Quote);
                if (role != null && string.IsNullOrWhiteSpace(lead.OwnerTitle)) lead.OwnerTitle = role;
            }
        }
        else if (!string.IsNullOrWhiteSpace(lead.OwnerName))
            Drop("ownerName", $"ownerName '{lead.OwnerName}'", () => { lead.OwnerName = ""; lead.OwnerFirstName = ""; lead.OwnerLastName = ""; lead.OwnerTitle = ""; });
        afterOwner:

        Set(r.Verified, "ownerLinkedInUrl", "ownerLinkedInUrl", v => lead.OwnerLinkedInUrl = v, () => lead.OwnerLinkedInUrl, () => lead.OwnerLinkedInUrl = null);
        Set(r.Verified, "companyLinkedInUrl", "linkedInUrl", v => lead.LinkedInUrl = v, () => lead.LinkedInUrl, () => lead.LinkedInUrl = "");
        Set(r.Verified, "ownerMobile", "ownerMobile", v => lead.OwnerMobile = v, () => lead.OwnerMobile, () => lead.OwnerMobile = null);
        Set(r.Verified, "personalEmail", "personalEmail", v => lead.PersonalEmail = v, () => lead.PersonalEmail, () => lead.PersonalEmail = "");
        if (r.Verified.ContainsKey("foundingYear") && int.TryParse(Val("foundingYear"), out var year))
        {
            if (CanWrite("foundingYear", lead.FoundingYear?.ToString(), year.ToString()) && lead.FoundingYear != year) { lead.FoundingYear = year; written.Add("foundingYear"); }
        }
        else if (lead.FoundingYear != null) Drop("foundingYear", $"foundingYear {lead.FoundingYear}", () => lead.FoundingYear = null);
        if (r.Verified.ContainsKey("kvkNumber"))
        {
            var kvk = HuntRegex.Replace(Val("kvkNumber"), @"\D", "");
            if (CanWrite("kvkNumber", lead.KvkNumber, kvk) && !Same(lead.KvkNumber, kvk)) { lead.KvkNumber = kvk; written.Add("kvkNumber"); }
        }

        if (r.Verified.TryGetValue("smallCompany", out var small))
        {
            if (CanWrite("employeeCount", lead.EmployeeCount, "1") && !Same(lead.EmployeeCount, "1")) { lead.EmployeeCount = "1"; written.Add("employeeCount"); }
            if (HuntRegex.IsMatch(small.Quote, @"eenmanszaak", RegexOptions.IgnoreCase) && CanWrite("legalForm", lead.LegalForm, "Eenmanszaak") && !Same(lead.LegalForm, "Eenmanszaak")) { lead.LegalForm = "Eenmanszaak"; written.Add("legalForm"); }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(lead.EmployeeCount)) Drop("employeeCount", $"employeeCount '{lead.EmployeeCount}'", () => lead.EmployeeCount = null);
            if (!string.IsNullOrWhiteSpace(lead.LegalForm)) Drop("legalForm", $"legalForm '{lead.LegalForm}'", () => lead.LegalForm = null);
        }

        // Phone: a mobile number in this field also scores, so it must be a published one.
        if (r.Descriptive.TryGetValue("phone", out var phone) && CanWrite("phone", lead.Phone, phone)) { if (!Same(lead.Phone, phone)) { lead.Phone = phone; written.Add("phone"); } }
        else if (PhoneNumbers.NormalizeMobile(lead.Phone) is { } existingMobile && PublishedMobile(r.Facts, existingMobile, out _) == null)
            // The same rule as for the owner's mobile: a number that stands next to a review or a credit, or outside the contact context of a site
            // with several numbers, is not the business's number, and it would score as one.
            Drop("phone", $"phone '{lead.Phone}' (a mobile number that is not published as the business's own on the website)", () => lead.Phone = "");

        if (r.Descriptive.TryGetValue("companyEmail", out var ce) && string.IsNullOrWhiteSpace(lead.CompanyEmail)) lead.CompanyEmail = ce;
        FillIfEmpty(r, "description", v => lead.Description = v, () => lead.Description);
        FillIfEmpty(r, "services", v => lead.Services = v, () => lead.Services);
        FillIfEmpty(r, "targetAudience", v => lead.TargetAudience = v, () => lead.TargetAudience);
        FillIfEmpty(r, "workingArea", v => lead.WorkingArea = v, () => lead.WorkingArea);
        FillIfEmpty(r, "certifications", v => lead.Certifications = v, () => lead.Certifications);
        FillIfEmpty(r, "openingHours", v => lead.OpeningHours = v, () => lead.OpeningHours);
        FillIfEmpty(r, "street", v => lead.Street = v, () => lead.Street);
        FillIfEmpty(r, "zipCode", v => lead.ZipCode = v, () => lead.ZipCode);
        FillIfEmpty(r, "facebookUrl", v => lead.FacebookUrl = v, () => lead.FacebookUrl);
        FillIfEmpty(r, "instagramUrl", v => lead.InstagramUrl = v, () => lead.InstagramUrl);
        if (string.IsNullOrWhiteSpace(lead.City) && r.Descriptive.TryGetValue("city", out var city)) lead.City = city;
        if (string.IsNullOrWhiteSpace(lead.Sector) && r.Descriptive.TryGetValue("sector", out var sector)) lead.Sector = sector;

        HuntProvenance.Record(lead, written);
        lead.IsEnriched = true;
        lead.EnrichedAt = DateTime.UtcNow;
        lead.CrawledAt = DateTime.UtcNow;

        var s = scoring.CalculateScore(lead);
        lead.SalesPriorityScore = s.Score;
        lead.SalesPriorityLabel = s.Label;
        lead.SalesPriorityReasoning = s.Reasoning;
        return removed;

        void Set(Dictionary<string, Claim> verified, string field, string key, Action<string> set, Func<string?> get, Action clear)
        {
            if (verified.TryGetValue(field, out var c)) { if (CanWrite(key, get(), c.Value) && !Same(get(), c.Value)) { set(c.Value); written.Add(key); } }
            else if (!string.IsNullOrWhiteSpace(get())) Drop(key, $"{field} '{get()}'", clear);
        }
    }

    /// <summary>A rating without a Google listing behind it (a widget, a review site, a guess) is not a Google rating.</summary>
    public static void ClearUnsourcedGoogle(Lead lead, List<string> removed, SalesScoreService scoring, bool mayClear = true, List<string>? kept = null)
    {
        if (lead.GoogleRating == null || !string.IsNullOrWhiteSpace(lead.GoogleMapsUrl)) return;
        var what = $"googleRating {lead.GoogleRating} (it is not from a Google listing)";
        if (!mayClear) { kept?.Add(what); return; }
        removed.Add(what);
        lead.GoogleRating = null;
        lead.GoogleReviewCount = null;
        var s = scoring.CalculateScore(lead);
        lead.SalesPriorityScore = s.Score; lead.SalesPriorityLabel = s.Label; lead.SalesPriorityReasoning = s.Reasoning;
        HuntProvenance.Reconcile(lead);
    }

    private static void FillIfEmpty(VerificationResult r, string field, Action<string> set, Func<string?> get)
    {
        if (string.IsNullOrWhiteSpace(get()) && r.Descriptive.TryGetValue(field, out var v)) set(v);
    }

    // ---- notes and evidence ----------------------------------------------------------------------------

    public static string BuildNote(string title, int score, VerificationResult r, IReadOnlyList<string> removed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title}. Score {score}, computed from verified facts only.");
        sb.AppendLine("Verified (quote and source):");
        if (r.Verified.Count == 0) sb.AppendLine("- nothing score bearing could be verified");
        foreach (var c in r.Verified.Values)
            sb.AppendLine($"- {c.Field}: {c.Value} | {c.SourceUrl} | \"{Trim(c.Quote, 200)}\"");
        if (r.Dropped.Count > 0)
        {
            sb.AppendLine("Not counted:");
            foreach (var d in r.Dropped.Where(d => d.Field != "all" || d.Reason.Length > 0).Take(12))
                sb.AppendLine($"- {d.Field}{(string.IsNullOrEmpty(d.Value) ? "" : " " + Trim(d.Value, 80))}: {d.Reason}");
        }
        AppendInsight(sb, r.Insight);
        if (removed.Count > 0) sb.AppendLine("Removed because it could not be verified: " + string.Join("; ", removed));
        if (!string.IsNullOrEmpty(r.GoogleNote)) sb.AppendLine("Google: " + r.GoogleNote);
        sb.AppendLine("Pages read: " + string.Join(", ", r.PageUrls));
        return sb.ToString().TrimEnd();
    }

    private static void AppendInsight(StringBuilder sb, InsightResult i)
    {
        if (i.Observation != null) sb.AppendLine($"Observation (quote and source): \"{Trim(i.Observation.Quote, 240)}\" | {i.Observation.SourceUrl}");
        if (i.Needs.Count > 0)
        {
            sb.AppendLine("Needs (each with the quote that shows it):");
            foreach (var n in i.Needs) sb.AppendLine($"- {n.Need} | {n.SourceUrl} | \"{Trim(n.Quote, 200)}\"");
        }
        if (i.ProductFit.Count > 0)
        {
            sb.AppendLine("Product fit (each with the trigger on the business's own pages):");
            foreach (var f in i.ProductFit) sb.AppendLine($"- {f.Product}: {f.Reason} | {f.SourceUrl} | \"{Trim(f.EvidenceQuote, 200)}\"");
        }
        else if (i.NoClearFit) sb.AppendLine("Product fit: the agent found no clear fit.");
        if (i.Dropped.Count > 0)
        {
            sb.AppendLine("Needs and fit not stored (no verified quote):");
            foreach (var d in i.Dropped.Take(12)) sb.AppendLine($"- {d.Field}{(string.IsNullOrEmpty(d.Value) ? "" : " " + Trim(d.Value, 60))}: {d.Reason}");
        }
    }

    public static string BuildEvidenceJson(VerificationResult r, int? maxReachable)
    {
        return JsonSerializer.Serialize(new
        {
            website = r.Website,
            reachable = r.Reachable,
            maxReachable,
            pages = r.PageUrls,
            verified = r.Verified.Values.Select(c => new { field = c.Field, value = c.Value, source = c.SourceUrl, quote = Trim(c.Quote, 300),  }),
            dropped = r.Dropped.Select(d => new { field = d.Field, value = Trim(d.Value, 120), reason = d.Reason }),
            google = r.GoogleNote,
            insight = new
            {
                observation = r.Insight.Observation == null ? null : new { quote = Trim(r.Insight.Observation.Quote, 300), source = r.Insight.Observation.SourceUrl },
                needs = r.Insight.Needs.Select(n => new { need = n.Need, quote = Trim(n.Quote, 300), source = n.SourceUrl }),
                productFit = r.Insight.ProductFit.Select(f => new { product = f.Product, reason = f.Reason, quote = Trim(f.EvidenceQuote, 300), source = f.SourceUrl }),
                noClearFit = r.Insight.NoClearFit,
                dropped = r.Insight.Dropped.Select(d => new { field = d.Field, value = Trim(d.Value, 80), reason = d.Reason }),
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string BuildDigest(IReadOnlyList<FetchedPage> pages)
    {
        var sb = new StringBuilder();
        foreach (var p in pages)
        {
            var text = p.Text.Length > PerPageChars ? p.Text[..PerPageChars] : p.Text;
            if (sb.Length + text.Length > TotalChars) break;
            sb.AppendLine($"### PAGE {p.Url}");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

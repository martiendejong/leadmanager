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
    /// <summary>True when the pages were read and the claims were checked (false when the website did not answer).</summary>
    public bool Checked { get; set; }
}

/// <summary>The pages of one website, fetched step by step: a quick look first, a deeper look only when the quick look is promising.</summary>
public class SitePages
{
    public string Website { get; init; } = "";
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
        var doc = new HtmlDocument();
        doc.LoadHtml(site.Home.Html);
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
                if (site.Pages.Count >= limit) return;
                if (!words.Any(w => label.Contains(w))) continue;
                if (!site.Seen.Add(Canon(url))) continue;
                var page = await _fetcher.FetchAsync(url, ct);
                if (page is { Status: >= 200 and < 300 }) site.Pages.Add(page);
            }
        }
    }

    private static string Canon(string url)
    {
        var u = Regex.Replace(url.ToLowerInvariant(), @"^https?://(www\.)?", "");
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
    /// Checks the claims a researcher submitted (the Jengo agent) against the fetched pages. The model is not trusted: a claim counts
    /// only when its quote occurs on a fetched page and the rule for its field holds (and the independent extractor agrees where it can).
    /// </summary>
    public VerificationResult VerifyClaims(SitePages site, JsonElement submitted)
    {
        var result = NewResult(site);
        if (!site.Reachable) return result;
        result.Checked = true;

        foreach (var field in ScoreFields)
        {
            var claim = ReadClaim(submitted, field);
            if (claim == null) continue;
            var check = CheckClaim(claim, site, result.Facts, result.Verified);
            if (check == null) result.Verified[field] = ResolveSource(claim, site) ?? claim;
            else result.Dropped.Add(new DroppedClaim(field, claim.Value, check));
        }
        ReadDescriptive(submitted, site, result);
        return result;
    }

    /// <summary>
    /// Verifies an existing lead from its own website without anyone's claims: only what the rules find on the pages (and the owner
    /// name the lead already has, when the pages show it) is accepted.
    /// </summary>
    public VerificationResult VerifyDeterministic(SitePages site, string? knownOwnerName)
    {
        var result = NewResult(site);
        if (!site.Reachable) return result;
        var f = result.Facts;
        var claims = new Dictionary<string, object>();

        string? owner = null;
        if (!string.IsNullOrWhiteSpace(knownOwnerName))
        {
            var parts = knownOwnerName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                foreach (var p in site.Pages)
                {
                    var i = p.Text.IndexOf(parts[^1], StringComparison.OrdinalIgnoreCase);
                    if (i < 0 || !Squash(p.Text).Contains(Squash(parts[0]))) continue;
                    claims["ownerName"] = new { value = knownOwnerName.Trim(), quote = Snippet(p.Text, i, parts[^1].Length), source_url = p.Url };
                    owner = knownOwnerName.Trim();
                    break;
                }
        }
        var mobile = f.Mobiles.FirstOrDefault();
        if (mobile != null) claims["ownerMobile"] = new { value = mobile.AsWritten, quote = mobile.Quote, source_url = mobile.SourceUrl };
        if (owner != null)
        {
            var link = f.LinkedIns.FirstOrDefault(l => l.Kind == "in" && LeadEvidenceService.HandleCarriesName(l.Handle, owner));
            if (link != null) claims["ownerLinkedInUrl"] = new { value = link.Url, quote = "linkedin.com/in/" + link.Handle, source_url = link.SourceUrl };
        }
        var company = f.LinkedIns.FirstOrDefault(l => l.Kind == "company");
        if (company != null) claims["companyLinkedInUrl"] = new { value = company.Url, quote = "linkedin.com/company/" + company.Handle, source_url = company.SourceUrl };
        var mail = f.Emails.FirstOrDefault(e => !e.Role && e.SameDomain && e.Warning == null);
        if (mail != null) claims["personalEmail"] = new { value = mail.Address, quote = mail.Quote, source_url = mail.SourceUrl };
        var year = f.Years.Where(y => y.Kind != "Copyright" && !y.CareerLike).OrderBy(y => y.Year).FirstOrDefault();
        if (year != null) claims["foundingYear"] = new { value = year.Year.ToString(), quote = year.Quote, source_url = year.SourceUrl };
        foreach (var p in site.Pages)
        {
            var m = LeadEvidenceService.FindSmall(p.Text);
            if (m == null) continue;
            claims["smallCompany"] = new { value = "1", quote = Snippet(p.Text, m.Index, m.Length), source_url = p.Url };
            break;
        }
        var kvk = f.Kvk.FirstOrDefault();
        if (kvk != null) claims["kvkNumber"] = new { value = kvk.Number, quote = kvk.Quote, source_url = kvk.SourceUrl };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(claims));
        foreach (var field in ScoreFields)
        {
            var claim = ReadClaim(doc.RootElement, field);
            if (claim == null) continue;
            var check = CheckClaim(claim, site, f, result.Verified);
            if (check == null) result.Verified[field] = ResolveSource(claim, site) ?? claim;
            else result.Dropped.Add(new DroppedClaim(field, claim.Value, check));
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

    private static string Snippet(string text, int index, int length)
    {
        var start = Math.Max(0, index - 70);
        var end = Math.Min(text.Length, index + length + 70);
        return Regex.Replace(text[start..end], @"\s+", " ").Trim();
    }

    // ---- claim reading and the rule check ----------------------------------------------------------------

    private static Claim? ReadClaim(JsonElement root, string field)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        string S(string n) => el.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "" : "";
        var value = S("value").Trim();
        if (value.Length == 0) return null;
        return new Claim(field, value, S("source_url").Trim(), S("quote").Trim());
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

    /// <summary>The page that really contains the quote (the claimed one first), or null when the quote is nowhere: an invented quote is dropped.</summary>
    private static Claim? ResolveSource(Claim claim, SitePages site)
    {
        var q = Squash(claim.Quote);
        if (q.Length < 6) return null;
        var ordered = site.Pages.OrderByDescending(p => p.Url.Equals(claim.SourceUrl, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        foreach (var p in ordered)
            if (Squash(p.Text).Contains(q) || Squash(System.Net.WebUtility.HtmlDecode(p.Html)).Contains(q))
                return claim with { SourceUrl = p.Url };
        return null;
    }

    /// <summary>Returns null when the claim holds, or the reason it is dropped.</summary>
    private static string? CheckClaim(Claim claim, SitePages site, EvidenceFacts facts, Dictionary<string, Claim> accepted)
    {
        if (ResolveSource(claim, site) == null) return "The quote does not occur on any fetched page.";
        var quoteSquashed = Squash(claim.Quote);

        switch (claim.Field)
        {
            case "ownerName":
                {
                    var parts = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return "A full name (first and last name) is needed.";
                    if (!Squash(claim.Quote).Contains(Squash(parts[^1]))) return "The quote does not contain the name.";
                    return null;
                }
            case "ownerLinkedInUrl":
                {
                    var m = Regex.Match(claim.Value, @"linkedin\.com/in/([A-Za-z0-9\-_%.]+)", RegexOptions.IgnoreCase);
                    if (!m.Success) return "Not a personal LinkedIn profile (/in/).";
                    var handle = m.Groups[1].Value.TrimEnd('/', '.');
                    if (!facts.LinkedIns.Any(l => l.Kind == "in" && l.Handle.TrimEnd('/', '.').Equals(handle, StringComparison.OrdinalIgnoreCase)))
                        return "The business does not publish this link on its own pages.";
                    if (!accepted.TryGetValue("ownerName", out var owner)) return "No verified owner name to match the profile to.";
                    if (!LeadEvidenceService.HandleCarriesName(handle, owner.Value)) return "The profile handle does not carry the owner's name.";
                    return null;
                }
            case "companyLinkedInUrl":
                {
                    var m = Regex.Match(claim.Value, @"linkedin\.com/company/([A-Za-z0-9\-_%.]+)", RegexOptions.IgnoreCase);
                    if (!m.Success) return "Not a company LinkedIn page (/company/).";
                    return facts.LinkedIns.Any(l => l.Kind == "company" && l.Handle.Equals(m.Groups[1].Value.TrimEnd('/', '.'), StringComparison.OrdinalIgnoreCase))
                        ? null : "The business does not publish this link on its own pages.";
                }
            case "ownerMobile":
                {
                    var norm = PhoneNumbers.NormalizeMobile(claim.Value);
                    if (norm == null) return "Not a Dutch mobile number (06). Landlines, 085, 088, 0800 and 0900 do not count.";
                    return facts.Mobiles.Any(x => x.Normalized == norm) ? null : "The number is not published on the business's own pages.";
                }
            case "personalEmail":
                {
                    var addr = claim.Value.Trim().ToLowerInvariant();
                    var matching = facts.Emails.Where(e => e.Address == addr).ToList();
                    if (matching.Count == 0) return "The address is not published on the business's own pages.";
                    if (matching.All(e => e.Role)) return "A role mailbox (info@, contact@, ...) does not count as a personal address.";
                    if (matching.All(e => e.Warning != null && e.Warning.StartsWith("The link shows"))) return "The link on the page opens a different address than the one it shows.";
                    return null;
                }
            case "foundingYear":
                {
                    if (!int.TryParse(claim.Value, out var year)) return "Not a year.";
                    var hits = facts.Years.Where(y => y.Year == year && y.Kind != "Copyright").ToList();
                    if (hits.Count == 0) return facts.Years.Any(y => y.Year == year) ? "The year only appears in a copyright notice, which is the age of the website, not of the business." : "No sentence on the site states that the business started in this year.";
                    if (hits.All(y => y.CareerLike)) return "The sentence describes the owner's career, not the start of this business.";
                    if (!quoteSquashed.Contains(year.ToString())) return "The quote does not contain the year.";
                    return null;
                }
            case "smallCompany":
                {
                    if (!LeadEvidenceService.QuoteStatesSmall(claim.Quote)) return "The quote is not the business's own statement that it is a sole proprietorship or one person.";
                    if (LeadEvidenceService.ContradictsSmall(facts)) return "The site also names a B.V., N.V., partnership, foundation or a team, which contradicts 'small'.";
                    return null;
                }
            case "kvkNumber":
                return facts.Kvk.Any(k => k.Number == Regex.Replace(claim.Value, @"\D", "")) ? null : "The KvK number is not published on the business's own pages.";
        }
        return "Unknown field.";
    }

    private static void ReadDescriptive(JsonElement root, SitePages site, VerificationResult result)
    {
        var all = Squash(string.Join(" ", site.Pages.Select(p => p.Text)));
        var html = string.Join(" ", site.Pages.Select(p => p.Html));
        string? Get(string name, int max)
        {
            if (!root.TryGetProperty(name, out var el)) return null;
            var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length > max ? s[..max] : s;
        }

        foreach (var (name, max) in new[] { ("ownerFirstName", 80), ("ownerLastName", 120), ("ownerTitle", 120), ("sector", 120), ("city", 80),
            ("description", 400), ("services", 400), ("targetAudience", 300), ("workingArea", 200), ("certifications", 300), ("openingHours", 300) })
        {
            var v = Get(name, max);
            if (v != null) result.Descriptive[name] = v;
        }
        // Values that must literally occur on the pages.
        foreach (var name in new[] { "phone", "companyEmail", "street", "zipCode" })
        {
            var v = Get(name, 200);
            if (v == null) continue;
            if (Squash(v).Length >= 4 && all.Contains(Squash(v))) result.Descriptive[name] = v;
            else result.Dropped.Add(new DroppedClaim(name, v, "The value does not occur on the fetched pages."));
        }
        foreach (var name in new[] { "facebookUrl", "instagramUrl" })
        {
            var v = Get(name, 300);
            if (v == null) continue;
            if (html.Contains(v, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(v, @"^https?://(www\.)?(facebook|instagram)\.com/", RegexOptions.IgnoreCase)) result.Descriptive[name] = v;
            else result.Dropped.Add(new DroppedClaim(name, v, "The link is not published on the fetched pages."));
        }
        if (result.Descriptive.TryGetValue("phone", out var ph)) result.Descriptive["phone"] = ph;
    }

    // ---- applying --------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the verified state to the lead. With clearUnverified (an existing lead) score bearing values that could not be verified are
    /// removed, so the score never keeps a point nobody can prove. The score itself is computed by SalesScoreService, never by the model.
    /// </summary>
    public static List<string> Apply(Lead lead, VerificationResult r, bool clearUnverified, SalesScoreService scoring)
    {
        var removed = new List<string>();
        string Val(string f) => r.Verified.TryGetValue(f, out var c) ? c.Value : "";

        if (r.Reachable) { lead.WebsiteStatus = WebsiteStatus.Reachable; lead.ResolvedUrl = r.PageUrls.FirstOrDefault(); }

        if (r.Verified.ContainsKey("ownerName"))
        {
            lead.OwnerName = Val("ownerName");
            var parts = lead.OwnerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            lead.OwnerFirstName = r.Descriptive.GetValueOrDefault("ownerFirstName") is { Length: > 0 } f ? f : parts[0];
            lead.OwnerLastName = r.Descriptive.GetValueOrDefault("ownerLastName") is { Length: > 0 } l ? l : parts.Length > 1 ? parts[1] : "";
            if (r.Descriptive.TryGetValue("ownerTitle", out var title)) lead.OwnerTitle = title;
        }
        else if (clearUnverified && !string.IsNullOrWhiteSpace(lead.OwnerName)) { removed.Add($"ownerName '{lead.OwnerName}'"); lead.OwnerName = ""; }

        Set(r.Verified, "ownerLinkedInUrl", v => lead.OwnerLinkedInUrl = v, () => lead.OwnerLinkedInUrl, () => lead.OwnerLinkedInUrl = null);
        Set(r.Verified, "companyLinkedInUrl", v => lead.LinkedInUrl = v, () => lead.LinkedInUrl, () => lead.LinkedInUrl = "");
        Set(r.Verified, "ownerMobile", v => lead.OwnerMobile = v, () => lead.OwnerMobile, () => lead.OwnerMobile = null);
        Set(r.Verified, "personalEmail", v => lead.PersonalEmail = v, () => lead.PersonalEmail, () => lead.PersonalEmail = "");
        if (r.Verified.ContainsKey("foundingYear") && int.TryParse(Val("foundingYear"), out var year)) lead.FoundingYear = year;
        else if (clearUnverified && lead.FoundingYear != null) { removed.Add($"foundingYear {lead.FoundingYear}"); lead.FoundingYear = null; }
        if (r.Verified.ContainsKey("kvkNumber")) lead.KvkNumber = Regex.Replace(Val("kvkNumber"), @"\D", "");

        if (r.Verified.TryGetValue("smallCompany", out var small))
        {
            lead.EmployeeCount = "1";
            if (Regex.IsMatch(small.Quote, @"eenmanszaak", RegexOptions.IgnoreCase)) lead.LegalForm = "Eenmanszaak";
        }
        else if (clearUnverified)
        {
            if (!string.IsNullOrWhiteSpace(lead.EmployeeCount)) { removed.Add($"employeeCount '{lead.EmployeeCount}'"); lead.EmployeeCount = null; }
            if (!string.IsNullOrWhiteSpace(lead.LegalForm)) { removed.Add($"legalForm '{lead.LegalForm}'"); lead.LegalForm = null; }
        }

        // Phone: a mobile number in this field also scores, so it must be a published one.
        if (r.Descriptive.TryGetValue("phone", out var phone) && (string.IsNullOrWhiteSpace(lead.Phone) || clearUnverified)) lead.Phone = phone;
        else if (clearUnverified && PhoneNumbers.IsDutchMobile(lead.Phone) && !r.Facts.Mobiles.Any(m => m.Normalized == PhoneNumbers.NormalizeMobile(lead.Phone)))
        { removed.Add($"phone '{lead.Phone}' (a mobile number that is not on the website)"); lead.Phone = ""; }

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

        lead.IsEnriched = true;
        lead.EnrichedAt = DateTime.UtcNow;
        lead.CrawledAt = DateTime.UtcNow;

        var s = scoring.CalculateScore(lead);
        lead.SalesPriorityScore = s.Score;
        lead.SalesPriorityLabel = s.Label;
        lead.SalesPriorityReasoning = s.Reasoning;
        return removed;

        void Set(Dictionary<string, Claim> verified, string field, Action<string> set, Func<string?> get, Action clear)
        {
            if (verified.TryGetValue(field, out var c)) set(c.Value);
            else if (clearUnverified && !string.IsNullOrWhiteSpace(get())) { removed.Add($"{field} '{get()}'"); clear(); }
        }
    }

    /// <summary>A rating without a Google listing behind it (a widget, a review site, a guess) is not a Google rating.</summary>
    public static void ClearUnsourcedGoogle(Lead lead, List<string> removed, SalesScoreService scoring)
    {
        if (lead.GoogleRating != null && string.IsNullOrWhiteSpace(lead.GoogleMapsUrl))
        {
            removed.Add($"googleRating {lead.GoogleRating} (it is not from a Google listing)");
            lead.GoogleRating = null;
            lead.GoogleReviewCount = null;
            var s = scoring.CalculateScore(lead);
            lead.SalesPriorityScore = s.Score; lead.SalesPriorityLabel = s.Label; lead.SalesPriorityReasoning = s.Reasoning;
        }
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
        if (removed.Count > 0) sb.AppendLine("Removed because it could not be verified: " + string.Join("; ", removed));
        if (!string.IsNullOrEmpty(r.GoogleNote)) sb.AppendLine("Google: " + r.GoogleNote);
        sb.AppendLine("Pages read: " + string.Join(", ", r.PageUrls));
        return sb.ToString().TrimEnd();
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

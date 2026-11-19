using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadManager.Api.Services.Hunt;

public record MobileFact(string Normalized, string AsWritten, string SourceUrl, string Quote);
public record LinkedInFact(string Kind, string Url, string Handle, string SourceUrl);
public record EmailFact(string Address, bool FromMailto, bool Role, bool FreeProvider, bool SameDomain, string SourceUrl, string Quote,
    /// <summary>The visible text of the link shows another address than the link opens (or the link is not a mailto at all).</summary>
    string? Warning);
public record LegalFact(string Kind, string SourceUrl, string Quote);
public record YearFact(int Year, string Kind, bool CareerLike, string SourceUrl, string Quote);
public record KvkFact(string Number, string SourceUrl, string Quote);

/// <summary>What the pages of one website show, found with plain rules and no language model.</summary>
public class EvidenceFacts
{
    public List<MobileFact> Mobiles { get; } = new();
    public List<LinkedInFact> LinkedIns { get; } = new();
    public List<EmailFact> Emails { get; } = new();
    public List<LegalFact> LegalForms { get; } = new();
    public List<YearFact> Years { get; } = new();
    public List<KvkFact> Kvk { get; } = new();
    public List<string> Landlines { get; } = new();
}

/// <summary>
/// Deterministic fact extraction. It knows the traps of the first manual hunt: a landline is not a mobile number, a link can
/// show one address and open another, a footer copyright range is not a founding year, a LinkedIn handle without the
/// owner's name proves nothing, and a B.V. in the footer means the business is not a sole proprietorship.
/// </summary>
public class LeadEvidenceService
{
    private static readonly HashSet<string> RoleMailboxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "info", "contact", "kantoor", "administratie", "support", "sales", "hallo", "hello", "mail", "post", "receptie", "secretariaat",
        "office", "service", "welkom", "boekhouding", "factuur", "facturen", "orders", "bestellingen", "reserveringen", "reservering",
        "webmaster", "noreply", "no-reply", "klantenservice", "verkoop", "inkoop", "praktijk", "team", "algemeen", "afspraak", "afspraken",
        "nl", "be", "contactus", "enquiries", "admin", "hr", "jobs", "vacatures", "pers", "press", "marketing", "finance", "debiteuren",
    };

    private static readonly HashSet<string> FreeProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "hotmail.com", "hotmail.nl", "outlook.com", "outlook.nl", "live.nl", "live.com", "yahoo.com", "yahoo.nl",
        "icloud.com", "me.com", "online.nl", "ziggo.nl", "kpnmail.nl", "home.nl", "planet.nl", "xs4all.nl", "hetnet.nl", "casema.nl",
        "chello.nl", "telfort.nl", "tele2.nl", "msn.com", "proton.me", "protonmail.com", "mail.com", "zonnet.nl", "upcmail.nl", "kpn.nl",
    };

    private static readonly HashSet<string> NotATld = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "svg", "webp", "css", "js", "ico", "woff", "woff2", "ttf", "map", "pdf", "mp4",
    };

    private static readonly Regex EmailRegex = new(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex LinkedInRegex = new(@"https?://(?:[a-z]{2,3}\.|www\.)?linkedin\.com/(in|company)/([A-Za-z0-9\-_%.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KvkRegex = new(@"\bkvk(?:[\s\-]*(?:nummer|nr\.?|number))?[\s:\-#]*(\d{8})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LandlineRegex = new(@"(?<![\d])0(?:[1-5]|[7-9])\d(?:[\s\-\.]*\d){7}(?![\d])", RegexOptions.Compiled);

    private static readonly (string Kind, Regex Pattern)[] LegalPatterns =
    {
        ("eenmanszaak", new Regex(@"\beenmanszaak\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("zzp", new Regex(@"\bzzp(?:'er|er)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("zelfstandig", new Regex(@"\bzelfstandig(?:e)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("alleen", new Regex(@"\b(?:ik werk (?:graag )?alleen|één persoon|een[- ]persoons|one[- ]person|solo[- ]?(?:ondernemer|onderneemster))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("vof", new Regex(@"\b(?:v\.o\.f\.?|vof)\b", RegexOptions.Compiled)),
        ("bv", new Regex(@"(?<!\w)(?:B\.V\.|BV|b\.v\.|Besloten Vennootschap)(?!\w)", RegexOptions.Compiled)),
        ("nv", new Regex(@"(?<!\w)(?:N\.V\.|Naamloze Vennootschap)(?!\w)", RegexOptions.Compiled)),
        ("maatschap", new Regex(@"\bmaatschap\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("stichting", new Regex(@"\bstichting\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("team", new Regex(@"\b(?:ons team|mijn collega'?s|onze (?:medewerkers|collega'?s)|\d+ medewerkers)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly Regex SinceRegex = new(@"\b(?:sinds|since|vanaf)\s+(?:het jaar\s+)?(?:\d{1,2}\s+\w+\s+)?(19[5-9]\d|20[0-2]\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FoundedRegex = new(@"\b(?:opgericht|gestart|begonnen|ontstaan|founded|established|oprichting|bestaat)\b[^.]{0,40}?\b(19[5-9]\d|20[0-2]\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InYearStartRegex = new(@"\bin\s+(19[5-9]\d|20[0-2]\d)\s+(?:ben ik|zijn wij|startte|starten|begon|begonnen|heb ik|hebben wij|ben|is)\b[^.]{0,80}?\b(?:start|opgericht|begon|gestart|oprichten|eigen bedrijf|onderneming|praktijk|bureau)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CopyrightRegex = new(@"(?:©|&copy;|copyright)\s*(?:(\d{4})\s*[-–—]\s*)?(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CareerRegex = new(@"\b(?:werk ik als|werkzaam als|ervaring|carrière|loopbaan|werkte|werkte ik|in dienst|bij (?:de|het)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EvidenceFacts Extract(IReadOnlyList<FetchedPage> pages, string? websiteUrl)
    {
        var facts = new EvidenceFacts();
        var siteDomain = UrlGuard.DomainOf(websiteUrl);
        foreach (var page in pages)
        {
            if (string.IsNullOrEmpty(page.Html) && string.IsNullOrEmpty(page.Text)) continue;
            ExtractMobiles(page, facts);
            ExtractLinkedIn(page, facts);
            ExtractEmails(page, siteDomain, facts);
            ExtractTextFacts(page, facts);
        }
        return facts;
    }

    private static string Quote(string text, int index, int length, int pad = 90)
    {
        var start = Math.Max(0, index - pad);
        var end = Math.Min(text.Length, index + length + pad);
        return Regex.Replace(text.Substring(start, end - start), @"\s+", " ").Trim();
    }

    private static void ExtractMobiles(FetchedPage page, EvidenceFacts facts)
    {
        var seen = new HashSet<string>();
        var text = page.Text;
        var matches = PhoneRegexMatches(text);
        foreach (var (norm, written, index) in matches)
            if (seen.Add(norm)) facts.Mobiles.Add(new MobileFact(norm, written, page.Url, Quote(text, index, written.Length)));
        foreach (Match m in LandlineRegex.Matches(text)) facts.Landlines.Add(m.Value.Trim());
    }

    private static List<(string Norm, string Written, int Index)> PhoneRegexMatches(string text)
    {
        var list = new List<(string, string, int)>();
        foreach (var (norm, written) in PhoneNumbers.FindMobiles(text))
        {
            var idx = text.IndexOf(written, StringComparison.Ordinal);
            list.Add((norm, written, Math.Max(idx, 0)));
        }
        return list;
    }

    private static void ExtractLinkedIn(FetchedPage page, EvidenceFacts facts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LinkedInRegex.Matches(WebUtility.HtmlDecode(page.Html)))
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            var handle = m.Groups[2].Value.TrimEnd('/', '.');
            var url = $"https://www.linkedin.com/{kind}/{handle}/";
            if (kind == "in" && handle.Equals("share", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(kind + handle)) facts.LinkedIns.Add(new LinkedInFact(kind, url, handle, page.Url));
        }
    }

    private static bool PlausibleAddress(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 1) return false;
        var domain = address[(at + 1)..];
        var tld = domain[(domain.LastIndexOf('.') + 1)..];
        return !NotATld.Contains(tld) && !Regex.IsMatch(address, @"@\d+x\b|\d+x\.");
    }

    private static void ExtractEmails(FetchedPage page, string siteDomain, EvidenceFacts facts)
    {
        var index = new Dictionary<string, EmailFact>(StringComparer.OrdinalIgnoreCase);

        EmailFact Make(string address, bool mailto, string quote, string? warning)
        {
            var local = address[..address.IndexOf('@')];
            var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
            var role = RoleMailboxes.Contains(local);
            var free = FreeProviders.Contains(domain);
            var same = siteDomain != "" && (domain == siteDomain || domain.EndsWith("." + siteDomain));
            return new EmailFact(address.ToLowerInvariant(), mailto, role, free, same, page.Url, quote, warning);
        }

        // Links first: they say where the address really goes.
        var doc = new HtmlDocument();
        doc.LoadHtml(page.Html ?? "");
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? new List<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", "")).Trim();
            var shown = Regex.Replace(WebUtility.HtmlDecode(a.InnerText ?? ""), @"\s+", " ").Trim();
            string? target = null;
            string? warning = null;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                var addr = Uri.UnescapeDataString(href[7..].Split('?')[0]).Split(',')[0].Trim();
                if (EmailRegex.IsMatch(addr) && PlausibleAddress(addr)) target = addr;
            }
            else if (EmailRegex.IsMatch(href) && !href.Contains("/") && PlausibleAddress(href))
            {
                // href="name@site.nl" without mailto: it does not open a mail window, but the address is the same.
                target = href;
                warning = "The link has no mailto: prefix, so it does not open a mail window.";
            }
            if (target == null) continue;

            var shownMail = EmailRegex.Match(shown);
            if (shownMail.Success && !shownMail.Value.Equals(target, StringComparison.OrdinalIgnoreCase))
                warning = $"The link shows {shownMail.Value} but opens {target}.";
            index[target] = Make(target, true, shown.Length > 0 ? shown : target, warning);
            if (shownMail.Success && !index.ContainsKey(shownMail.Value) && PlausibleAddress(shownMail.Value))
                index[shownMail.Value] = Make(shownMail.Value, false, shown, warning);
        }

        // Plain text addresses.
        foreach (Match m in EmailRegex.Matches(page.Text))
        {
            if (!PlausibleAddress(m.Value) || index.ContainsKey(m.Value)) continue;
            index[m.Value] = Make(m.Value, false, Quote(page.Text, m.Index, m.Length, 60), null);
        }
        facts.Emails.AddRange(index.Values);
    }

    private static void ExtractTextFacts(FetchedPage page, EvidenceFacts facts)
    {
        var text = page.Text;
        foreach (Match m in KvkRegex.Matches(text))
            if (!facts.Kvk.Any(k => k.Number == m.Groups[1].Value)) facts.Kvk.Add(new KvkFact(m.Groups[1].Value, page.Url, Quote(text, m.Index, m.Length)));

        foreach (var (kind, pattern) in LegalPatterns)
        {
            var m = pattern.Match(text);
            if (m.Success) facts.LegalForms.Add(new LegalFact(kind, page.Url, Quote(text, m.Index, m.Length, 70)));
        }

        void AddYear(Match m, string kind, int group)
        {
            var year = int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
            var quote = Quote(text, m.Index, m.Length, 70);
            var career = CareerRegex.IsMatch(quote);
            if (!facts.Years.Any(y => y.Year == year && y.Kind == kind && y.SourceUrl == page.Url))
                facts.Years.Add(new YearFact(year, kind, career, page.Url, quote));
        }
        foreach (Match m in SinceRegex.Matches(text)) AddYear(m, "Since", 1);
        foreach (Match m in FoundedRegex.Matches(text)) AddYear(m, "Founded", 1);
        foreach (Match m in InYearStartRegex.Matches(text)) AddYear(m, "Founded", 1);
        foreach (Match m in CopyrightRegex.Matches(text))
        {
            var group = m.Groups[1].Success ? 1 : 2; // the first year of a range "2020 - 2026" is when the site started, never the business
            AddYear(m, "Copyright", group);
        }
    }

    // ---- name checks used by the verification --------------------------------------------------------------

    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase)
    { "van", "de", "der", "den", "het", "ten", "ter", "te", "vd", "v", "d", "von", "la", "le", "el", "al" };

    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Does a LinkedIn handle visibly carry the owner's name? "marijeweijers" yes, "sabobomediation" for Petra Delhez no.</summary>
    public static bool HandleCarriesName(string handle, string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(ownerName)) return false;
        var folded = Fold(Uri.UnescapeDataString(handle));
        var tokens = ownerName.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NameParticles.Contains(t)).Select(Fold).Where(t => t.Length > 0).ToList();
        if (tokens.Count < 2) return false;
        var first = tokens[0];
        var last = tokens.OrderByDescending(t => t.Length).First() == first ? tokens[^1] : tokens.Skip(1).OrderByDescending(t => t.Length).First();
        if (last.Length < 3 || !folded.Contains(last)) return false;
        return folded.Contains(first) || folded.StartsWith(first[0].ToString());
    }

    private static readonly Regex SmallPhrase = new(
        @"\b(eenmanszaak|zzp(?:'er|er)?|zelfstandig(?:e)?\b|ik werk (?:graag )?alleen|één persoon|een[- ]persoons|one[- ]person|solo[- ]?ondernemer)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A quote counts as the business's own statement that it is small only when it carries one of these phrases.</summary>
    public static bool QuoteStatesSmall(string? quote) => !string.IsNullOrWhiteSpace(quote) && SmallPhrase.IsMatch(quote);

    /// <summary>True when the pages call the business a B.V., N.V., maatschap, stichting or name a team: it cannot count as small on a 'zelfstandig' remark.</summary>
    public static bool ContradictsSmall(EvidenceFacts facts) =>
        facts.LegalForms.Any(f => f.Kind is "bv" or "nv" or "maatschap" or "stichting" or "vof" or "team");
}
